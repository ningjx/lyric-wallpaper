import { NowPlayingApi, toSnapshot } from "../api/nowPlaying";
import { SseClient } from "../api/sse";
import { API_CONFIG } from "../api/config";
import type { PlayerSnapshot } from "../api/types";
import type { SyncClock } from "./SyncClock";
import type { LyricLine } from "../lyrics/parser";
import { parseLrc, mergeTranslation } from "../lyrics/parser";
import type { SceneController } from "../scene";

/** 歌词渲染目标：MusicState 与渲染层之间的最小契约 */
export interface LyricsTarget {
  /** 设置歌词行（fallback 为无歌词/加载失败时显示的内容） */
  setLines(lines: LyricLine[], fallback?: string): void;
  /** 切歌的第一阶段：将新歌信息行接入当前视觉歌词队列。 */
  beginSong(title: string, author?: string): void;
  /** 切歌的第二阶段：将异步到达的新歌歌词接到信息行之后。 */
  resolveSongLyrics(lines: LyricLine[], position: number): void;
  /** 清空歌词（未播放/无歌曲） */
  clear(): void;
  /** 切歌时清空「当前歌」临时偏移（可选，仅需临时偏移功能的渲染层实现） */
  clearTempOffset?(): void;
}

/** 在没有可展示歌词时承接画面的独立内容层。 */
export interface IdleContentTarget {
  setVisible(visible: boolean): void;
}
/**
 * 音乐状态机：SSE 事件驱动 + 低频进度校准。
 *
 * 职责分工（对应「适合 SSE 走 SSE、不适合就轮询」）：
 * - 切歌 / 播放暂停翻转 / 歌词解析完成 → SSE 即时事件（applySnapshot）
 * - 播放进度 → 本地 SyncClock 推进，靠低频 /query 拉回真实值（漂移校准）
 * - SSE 掉线 → 低频 /query 兼做兜底的状态感知，同时指数退避重连
 *
 * 竞态防护：切歌触发的 loadLyrics 是异步的；用自增序号 seq 保证
 * 过期的返回结果不会覆盖新歌。
 */
export class MusicState {
  private songKey: string | null = null;
  private online = false;
  /** 竞态序号：每次发起 loadLyrics 前自增，回来时校验是否仍是最新 */
  private seq = 0;
  private readonly sse: SseClient;
  private calibrateTimer: number | null = null;
  private calibrating = false;
  private pollIntervalMs = 200;
  private idleTimer: number | null = null;
  private idleDelayMs = 30_000;
  private idleReason: "no-song" | "paused" | null = null;
  /** 暂停超时后清空过歌词；恢复时应像切歌一样重新装载。 */
  private pausedSongWasCleared = false;

  constructor(
    private readonly api: NowPlayingApi,
    private readonly clock: SyncClock,
    private readonly target: LyricsTarget,
    private readonly scene: SceneController,
    private readonly idleContent?: IdleContentTarget,
    sse?: SseClient,
  ) {
    this.sse = sse ?? new SseClient();
  }

  /** 启动：SSE 事件流 + 低频校准轮询 */
  start(): void {
    this.sse.onState = (s) => this.applySnapshot(s);
    this.sse.onStatus = (online) => this.setOnline(online);
    this.sse.start();
    void this.calibrate();

    this.restartCalibrateTimer();
  }

  /** 停止（页面卸载时调用，避免 SSE 连接与定时器泄漏） */
  stop(): void {
    if (this.calibrateTimer !== null) {
      window.clearInterval(this.calibrateTimer);
      this.calibrateTimer = null;
    }
    this.cancelIdleTimer();
    this.idleContent?.setVisible(false);
    this.sse.stop();
  }

  /** 更新 /query 校准频率；Wallpaper Engine 用户属性会持久化此设置。 */
  setPollInterval(ms: number): void {
    const next = Math.max(100, Math.min(10_000, Math.round(ms)));
    if (next === this.pollIntervalMs) return;
    this.pollIntervalMs = next;
    if (this.calibrateTimer !== null) this.restartCalibrateTimer();
  }

  /** 设置无歌曲或暂停多久后切到无播放内容；0 表示立即切换。 */
  setIdleDelay(ms: number): void {
    this.idleDelayMs = Math.max(0, Math.round(ms));
    if (this.idleTimer !== null) {
      const reason = this.idleReason ?? "paused";
      this.cancelIdleTimer();
      this.scheduleIdle(reason);
    }
  }

  private restartCalibrateTimer(): void {
    if (this.calibrateTimer !== null) window.clearInterval(this.calibrateTimer);
    this.calibrateTimer = window.setInterval(() => void this.calibrate(), this.pollIntervalMs);
  }

  /**
   * 统一的状态入口：无论是 SSE 推送还是 /query 轮询，最终都归一成
   * PlayerSnapshot 走到这里。
   */
  private applySnapshot(s: PlayerSnapshot): void {
    this.setOnline(true);
    this.clock.calibrate(s.progress || 0, s.hasSong && s.playing);

    if (!s.hasSong) {
      // 无歌曲与暂停共用同一套空闲计时，防止切歌的瞬时空窗导致立即切换。
      this.scheduleIdle("no-song");
      return;
    }

    if (s.playing) {
      this.cancelIdleTimer();
      this.idleContent?.setVisible(false);
      if (this.pausedSongWasCleared) {
        // 清空过的暂停歌曲恢复播放：强制走一次完整的切歌/歌词加载流程。
        this.pausedSongWasCleared = false;
        this.songKey = null;
      }
    } else {
      this.scheduleIdle("paused");
    }

    // 歌曲变化：切歌（用 song|author 作 key，切歌事件的瞬间就有，不必等搜索返回 id）
    const key = `${s.song}|${s.author}`;
    if (key !== this.songKey) {
      // 空闲层期间换了一首（即使新歌恰好也是暂停）：应立刻回到歌词状态，
      // 并从这次暂停重新计时，不能沿用上一首歌已经触发过的空闲标记。
      this.pausedSongWasCleared = false;
      this.cancelIdleTimer();
      this.idleContent?.setVisible(false);
      if (!s.playing) this.scheduleIdle("paused");
      this.songKey = key;
      // 切歌 → 清空上一首的临时偏移（仅当前歌有效）
      this.target.clearTempOffset?.();
      // 立即把信息行接到旧歌的未渲染位置；歌词可稍后异步接入。
      this.target.beginSong(s.song, s.author);
      void this.loadLyrics(s.song, s.author);
    } else {
      // 歌词已就绪：保持显示（含暂停状态，暂停时保留歌词停在当前句）
      this.scene.show();
    }
  }

  /** 低频校准：SSE 在线时仅做进度锚点校正；离线时兼做兜底状态感知。 */
  private async calibrate(): Promise<void> {
    // 高频轮询时不叠加未完成请求，避免慢服务导致请求堆积。
    if (this.calibrating) return;
    this.calibrating = true;
    try {
      const state = await this.api.fetchState();
      this.applySnapshot(toSnapshot(state));
    } catch {
      this.setOnline(false);
    } finally {
      this.calibrating = false;
    }
  }

  private async loadLyrics(title: string, author?: string): Promise<void> {
    const mySeq = ++this.seq;
    try {
      const ly = await this.api.fetchLyrics();
      // 拉取期间可能已切歌/停播：过期结果直接丢弃
      if (mySeq !== this.seq || this.songKey === null) return;
      if (ly.hasLyric && ly.lrc) {
        const original = parseLrc(ly.lrc);
        const lines =
          ly.hasTranslatedLyric && ly.translatedLyric
            ? mergeTranslation(original, parseLrc(ly.translatedLyric))
            : original;
        this.target.resolveSongLyrics(lines, this.clock.now());
      }
    } catch {
      // 信息行已经在视觉队列中，失败时保持它作为无歌词兜底。
    }
    // 场景保持常驻；此处不再控制整层显隐。
    if (mySeq === this.seq) this.scene.show();
  }

  private setOnline(v: boolean): void {
    if (!v) {
      if (!this.online && this.idleContent) {
        // 初始连接尚未建立时也应立即有内容可显示。
        this.idleContent.setVisible(true);
        return;
      }
      if (!this.online) return;
      // 本地服务不可达时没有可用的播放状态，直接交给空闲内容层。
      // 清空并重置 key，确保服务恢复后会重新请求当前歌曲歌词。
      this.cancelIdleTimer();
      this.clock.reset();
      this.songKey = null;
      this.seq++;
      this.target.clear();
      this.pausedSongWasCleared = false;
      this.idleContent?.setVisible(true);
    }
    if (this.online === v) return;
    this.online = v;
    // 服务刚恢复时先收起旧的时钟，再由本次播放器状态决定是否重新开始 30 秒计时。
    if (v) this.idleContent?.setVisible(false);
  }

  /**
   * 切歌时播放器可能有一帧短暂上报 hasSong=false；延迟确认避免把旧歌词队列
   * 清掉。真正停止播放后才清空歌词，Canvas 背景仍保持显示。
   */
  private scheduleIdle(reason: "no-song" | "paused"): void {
    if (this.idleTimer !== null || this.pausedSongWasCleared) return;
    this.idleReason = reason;
    this.idleTimer = window.setTimeout(() => {
      this.idleTimer = null;
      this.clock.reset();
      this.target.clear();
      // 暂停时必须保留 songKey；否则下一轮 200ms 校准会把同一首暂停
      // 歌曲误判为切歌，随即重新挂回歌词，导致时钟只闪一下。
      if (this.idleReason === "no-song") {
        this.songKey = null;
        this.pausedSongWasCleared = false;
      } else {
        this.pausedSongWasCleared = true;
      }
      this.idleContent?.setVisible(true);
    }, this.idleDelayMs);
  }

  private cancelIdleTimer(): void {
    if (this.idleTimer === null) return;
    window.clearTimeout(this.idleTimer);
    this.idleTimer = null;
    this.idleReason = null;
  }
}
