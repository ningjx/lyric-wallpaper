import { API_CONFIG } from "./config";
import type { LyricsResponse, NowPlayingState } from "./types";

/**
 * Now Playing 本地 API 适配器。
 * 项目其他部分只依赖本类，不直接接触 HTTP 细节与端点路径。
 */
export class NowPlayingApi {
  constructor(private readonly baseUrl: string = API_CONFIG.baseUrl) {}

  /** 获取播放器 + 歌曲状态 */
  fetchState(): Promise<NowPlayingState> {
    return this.get<NowPlayingState>(API_CONFIG.stateEndpoint);
  }

  /** 获取当前歌曲歌词 */
  fetchLyrics(): Promise<LyricsResponse> {
    return this.get<LyricsResponse>(API_CONFIG.lyricsEndpoint);
  }

  private async get<T>(endpoint: string): Promise<T> {
    const res = await fetch(this.baseUrl + endpoint, { cache: "no-store" });
    if (!res.ok) {
      throw new Error(`Now Playing API ${endpoint} 失败: HTTP ${res.status}`);
    }
    return res.json() as Promise<T>;
  }
}
