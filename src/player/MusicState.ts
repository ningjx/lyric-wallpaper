import type { NowPlayingApi } from "../api/nowPlaying";
import type { SyncClock } from "./SyncClock";
import type { LyricLine } from "../lyrics/parser";
import { parseLrc, mergeTranslation } from "../lyrics/parser";
import { API_CONFIG } from "../api/config";
import type { SceneController } from "../scene";

/** 歌词渲染目标：MusicState 与渲染层之间的最小契约 */
export interface LyricsTarget {
  /** 设置歌词行（fallback 为无歌词/加载失败时显示的内容） */
  setLines(lines: LyricLine[], fallback?: string): void;
  /** 清空歌词（未播放/无歌曲） */
  clear(): void;
}

/**
 * 音乐状态机：低频轮询 /query，检测歌曲/状态变化，拉取歌词并交给渲染层。
 *
 * - 连接失败 → 通知渲染层离线（不白屏），定时重试
 * - 歌曲变化 → 拉取歌词并解析
 * - 每轮校准 SyncClock
 */
export class MusicState {
  private songKey: string | null = null;
  private online = false;

  constructor(
    private readonly api: NowPlayingApi,
    private readonly clock: SyncClock,
    private readonly target: LyricsTarget,
    private readonly scene: SceneController,
  ) {}

  /** 启动轮询 */
  start(): void {
    this.tick();
    window.setInterval(() => this.tick(), API_CONFIG.pollIntervalMs);
  }

  private async tick(): Promise<void> {
    let state;
    try {
      state = await this.api.fetchState();
    } catch {
      this.setOnline(false);
      return;
    }
    this.setOnline(true);

    const { player, track } = state;
    const hasSong = !!player.hasSong && !!track.id;

    // 校准本地时钟
    this.clock.calibrate(player.seekbarCurrentPosition || 0, hasSong && !player.isPaused);

    if (!hasSong) {
      // 无歌曲/停止播放：清空歌词、重置时钟并淡出场景层
      this.clock.reset();
      this.scene.hide();
      if (this.songKey !== null) {
        this.songKey = null;
        this.target.clear();
      }
      return;
    }

    // 歌曲变化：切歌
    const key = `${track.id}:${track.title}`;
    if (key !== this.songKey) {
      this.songKey = key;
      // 歌词加载完成后才淡入场景层，避免"背景已显示、歌词还空白"的闪烁
      await this.loadLyrics(track.title);
    } else {
      // 歌词已就绪：保持显示（含暂停状态，暂停时保留歌词停在当前句）
      this.scene.show();
    }
  }

  private async loadLyrics(title: string): Promise<void> {
    try {
      const ly = await this.api.fetchLyrics();
      // 拉取期间可能已切歌/停播
      if (this.songKey === null) return;
      if (!ly.hasLyric || !ly.lrc) {
        this.target.setLines([], title);
      } else {
        const original = parseLrc(ly.lrc);
        if (ly.hasTranslatedLyric && ly.translatedLyric) {
          const translated = parseLrc(ly.translatedLyric);
          this.target.setLines(mergeTranslation(original, translated));
        } else {
          this.target.setLines(original);
        }
      }
    } catch {
      // 歌词拉取失败：显示歌曲信息兜底，不白屏
      this.target.setLines([], title);
    }
    // 歌词已就绪（或兜底文案已显示）：淡入场景层
    this.scene.show();
  }

  private setOnline(v: boolean): void {
    if (this.online === v) return;
    this.online = v;
    const el = document.getElementById("offline");
    if (el) el.hidden = v;
  }
}
