import { API_CONFIG } from "./config";
import type {
  NowPlayingState,
  LyricsResponse,
  PlayerSnapshot,
} from "./types";

/**
 * Now Playing 本地 API 适配器。
 * 项目其他部分只依赖本类，不直接接触 HTTP 细节与端点路径。
 */
export class NowPlayingApi {
  constructor(private readonly baseUrl: string = API_CONFIG.baseUrl) {}

  /** 获取播放器 + 歌曲状态（完整，含 track 元信息） */
  fetchState(): Promise<NowPlayingState> {
    return this.get<NowPlayingState>(API_CONFIG.stateEndpoint);
  }

  /** 获取当前歌曲歌词 */
  fetchLyrics(): Promise<LyricsResponse> {
    return this.get<LyricsResponse>(API_CONFIG.lyricsEndpoint);
  }

  private async get<T>(endpoint: string): Promise<T> {
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), API_CONFIG.fetchTimeoutMs);
    try {
      const res = await fetch(this.baseUrl + endpoint, {
        cache: "no-store",
        signal: ac.signal,
      });
      if (!res.ok) {
        throw new Error(`Now Playing API ${endpoint} 失败: HTTP ${res.status}`);
      }
      return (await res.json()) as T;
    } finally {
      clearTimeout(timer);
    }
  }
}

/**
 * 把 /query 的完整状态归一化成前端统一的 PlayerSnapshot。
 * resolveState / hasLyric 在 /query 里没有，置为空值；
 * 这两个字段由 SSE（解析完成事件）负责提供。
 */
export function toSnapshot(state: NowPlayingState): PlayerSnapshot {
  const { player, track } = state;
  const hasSong = !!player.hasSong && !!track.id;
  return {
    hasSong,
    song: track.title || "",
    author: track.author || "",
    playing: hasSong && !player.isPaused,
    progress: player.seekbarCurrentPosition || 0,
    duration: track.duration || 0,
    resolveState: "idle",
    trackId: track.id || "",
    album: track.album || "",
    cover: track.cover || "",
    hasLyric: false,
  };
}