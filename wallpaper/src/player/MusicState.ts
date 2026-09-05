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
  /** 清空歌词（未播放/无歌曲） */
  clear(): void;
  /** 切歌时清空「当前歌」临时偏移（可选，仅需临时偏移功能的渲染层实现） */
  clearTempOffset?(): void;
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

  constructor(
    private readonly api: NowPlayingApi,
    private readonly clock: SyncClock,
    private readonly target: LyricsTarget,
    private readonly scene: SceneController,
    sse?: SseClient,
  ) {
    this.sse = sse ?? new SseClient();
  }

  /** 启动：SSE 事件流 + 低频校准轮询 */
  start(): void {
    this.sse.onState = (s) => this.applySnapshot(s);
    this.sse.onStatus = (online) => this.setOnline(online);
    this.sse.start();

    this.calibrateTimer = window.setInterval(
      () => void this.calibrate(),
      API_CONFIG.calibrateIntervalMs,
    );
  }

  /** 停止（页面卸载时调用，避免 SSE 连接与定时器泄漏） */
  stop(): void {
    if (this.calibrateTimer !== null) {
      window.clearInterval(this.calibrateTimer);
      this.calibrateTimer = null;
    }
    this.sse.stop();
  }

  /**
   * 统一的状态入口：无论是 SSE 推送还是 /query 轮询，最终都归一成
   * PlayerSnapshot 走到这里。
   */
  private applySnapshot(s: PlayerSnapshot): void {
    this.setOnline(true);
    this.clock.calibrate(s.progress || 0, s.hasSong && s.playing);

    if (!s.hasSong) {
      // 无歌曲/停止播放：清空歌词、重置时钟并淡出场景层
      this.clock.reset();
      this.scene.hide();
      if (this.songKey !== null) {
        this.songKey = null;
        this.target.clear();
      }
      return;
    }

    // 歌曲变化：切歌（用 song|author 作 key，切歌事件的瞬间就有，不必等搜索返回 id）
    const key = `${s.song}|${s.author}`;
    if (key !== this.songKey) {
      this.songKey = key;
      // 切歌 → 清空上一首的临时偏移（仅当前歌有效）
      this.target.clearTempOffset?.();
      // 歌词加载完成后才淡入场景层，避免"背景已显示、歌词还空白"的闪烁
      void this.loadLyrics(s.song, s.author);
    } else {
      // 歌词已就绪：保持显示（含暂停状态，暂停时保留歌词停在当前句）
      this.scene.show();
    }
  }

  /** 低频校准：SSE 在线时仅做进度锚点校正；离线时兼做兜底状态感知。 */
  private async calibrate(): Promise<void> {
    try {
      const state = await this.api.fetchState();
      this.applySnapshot(toSnapshot(state));
    } catch {
      this.setOnline(false);
    }
  }

  private async loadLyrics(title: string, author?: string): Promise<void> {
    const mySeq = ++this.seq;
    try {
      const ly = await this.api.fetchLyrics();
      // 拉取期间可能已切歌/停播：过期结果直接丢弃
      if (mySeq !== this.seq || this.songKey === null) return;
      if (!ly.hasLyric || !ly.lrc) {
        this.target.setLines([], title);
      } else {
        const original = parseLrc(ly.lrc);
        const lines =
          ly.hasTranslatedLyric && ly.translatedLyric
            ? mergeTranslation(original, parseLrc(ly.translatedLyric))
            : original;
        // 前奏期间还没有歌词行，前置一行"歌曲名 - 歌手"占位作为前奏的当前行
        this.target.setLines(withHeader(lines, title, author));
      }
    } catch {
      // 歌词拉取失败：显示歌曲信息兜底，不白屏（同样要校验序号）
      if (mySeq === this.seq) this.target.setLines([], title);
    }
    // 歌词已就绪（或兜底文案已显示）：淡入场景层（仅在仍是最新歌时）
    if (mySeq === this.seq) this.scene.show();
  }

  private setOnline(v: boolean): void {
    if (this.online === v) return;
    this.online = v;
    const el = document.getElementById("offline");
    if (el) el.hidden = v;
  }
}

/**
 * 前置一行"歌曲名 - 歌手"作为前奏占位。
 * time 设为 -1，永远先于任何歌词行，因此前奏（时间早于第一句歌词）时它就是当前行，
 * 居中大字显示；第一句歌词开始后随滚动自然上移、淡出窗口。
 * 若歌词首行已自带歌名/歌手信息，则不再重复添加。
 */
function withHeader(lines: LyricLine[], title: string, author?: string): LyricLine[] {
  const t = title?.trim() ?? "";
  const a = author?.trim() ?? "";
  if (!t && !a) return lines;
  if (firstLineCarriesSongInfo(lines, t, a)) return lines;
  return [{ time: -1, text: [t, a].filter(Boolean).join(" - ") }, ...lines];
}

/** 首行是否已自带歌名/歌手信息：完全等于歌名或歌手，或同时包含两者 */
function firstLineCarriesSongInfo(lines: LyricLine[], title: string, author: string): boolean {
  const first = (lines[0]?.text ?? "").trim();
  if (!first) return false;
  if (title && first === title) return true;
  if (author && first === author) return true;
  return !!(title && author && first.includes(title) && first.includes(author));
}