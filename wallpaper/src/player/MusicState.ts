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
      await this.loadLyrics(track.title, track.author);
    } else {
      // 歌词已就绪：保持显示（含暂停状态，暂停时保留歌词停在当前句）
      this.scene.show();
    }
  }

  private async loadLyrics(title: string, author?: string): Promise<void> {
    try {
      const ly = await this.api.fetchLyrics();
      // 拉取期间可能已切歌/停播
      if (this.songKey === null) return;
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
