/**
 * Now Playing 本地 API 配置。
 * 所有端点集中在这里，接口路径变化只改一处。
 * 端点以本机当前安装版本实测为准（见 README.md）。
 */
export const API_CONFIG = {
  /** Now Playing Service 本地服务地址 */
  baseUrl: "http://127.0.0.1:9863",
  /** 播放器 + 歌曲状态 */
  stateEndpoint: "/query",
  /** 歌词（LRC + 翻译 + 卡拉OK） */
  lyricsEndpoint: "/api/lyric",
  /** Server-Sent Events（切歌/暂停/解析完成等事件推送） */
  sseEndpoint: "/sse",

  /**
   * 进度校准轮询间隔（毫秒）。
   *
   * 架构约定：切歌/暂停/歌词解析完成等「不连续事件」走 SSE 即时推送；
   * 「播放进度」是连续量，SSE 故意不推，由前端 SyncClock 本地推进，
   * 只需低频轮询把本地位置拉回真实值（校正时钟漂移）。SSE 掉线时，
   * 此轮询会兼做兜底的状态感知与恢复。
   */
  calibrateIntervalMs: 5000,

  /** 单次 fetch 的超时（毫秒） */
  fetchTimeoutMs: 8000,
} as const;