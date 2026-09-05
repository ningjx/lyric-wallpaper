/** GET /query 响应 */
export interface NowPlayingState {
  player: {
    hasSong: boolean;
    isPaused: boolean;
    volumePercent: number;
    /** 当前播放进度（秒），拖动进度条时由服务端实时上报 */
    seekbarCurrentPosition: number;
    seekbarCurrentPositionHuman: string;
    statePercent: number;
    likeStatus: string;
    repeatType: string;
  };
  track: {
    id: string;
    title: string;
    author: string;
    album: string;
    cover: string;
    duration: number;
    durationHuman: string;
    url: string;
    isVideo: boolean;
    isAdvertisement: boolean;
    inLibrary: boolean;
  };
}

/** GET /api/lyric 响应 */
export interface LyricsResponse {
  source: string;
  title: string;
  author: string;
  duration: number;
  hasLyric: boolean;
  hasTranslatedLyric: boolean;
  hasKaraokeLyric: boolean;
  /** LRC 格式文本，开头可能有网易云 JSON 元数据行 */
  lrc: string;
  /** 翻译歌词（LRC 格式，可空） */
  translatedLyric: string;
  /** 逐字歌词（可空） */
  karaokeLyric: string;
}

/**
 * SSE 推送到、以及前端统一使用的「精简播放器快照」。
 *
 * 后端 /sse 推的 state 就是此结构（字段名一致）。/query 的 NowPlayingState
 * 会被归一化到这里（见 nowPlaying.ts 的 toSnapshot）。
 *
 * 注意：切歌事件发出瞬间 trackId/album/cover/hasLyric 可能尚未就绪
 * （搜索未返回）；等 resolveState 变为 ok/degraded 的事件到达时才完整。
 */
export interface PlayerSnapshot {
  hasSong: boolean;
  song: string;
  author: string;
  playing: boolean;
  progress: number;
  duration: number;
  /** 歌词解析状态：resolving / ok / degraded / from-cache / idle */
  resolveState: string;
  trackId: string;
  album: string;
  cover: string;
  hasLyric: boolean;
}

/** SSE 事件帧（data: 行解出的 JSON） */
export interface SseEvent {
  type: "snapshot" | "state" | "song" | "ping";
  state?: PlayerSnapshot;
}
