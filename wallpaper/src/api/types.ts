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
