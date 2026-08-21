/**
 * Now Playing 本地 API 配置。
 * 所有端点集中在这里，接口路径变化只改一处。
 * 端点以本机当前安装版本实测为准（见 README.md）。
 */
export const API_CONFIG = {
  /** Now Playing Service 本地服务地址 */
  baseUrl: "http://127.0.0.1:9863",
  /** 播放器 + 歌曲状态（建议每秒轮询一次） */
  stateEndpoint: "/query",
  /** 歌词（LRC + 翻译 + 卡拉OK） */
  lyricsEndpoint: "/api/lyric",
  /** 状态轮询间隔 */
  pollIntervalMs: 200,
} as const;
