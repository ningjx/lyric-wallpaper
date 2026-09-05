import { API_CONFIG } from "./config";
import type { PlayerSnapshot, SseEvent } from "./types";

/**
 * SSE 客户端：订阅后端 /sse 事件流，产出 PlayerSnapshot。
 *
 * - 优先用原生 EventSource；若环境不支持或连接异常，回退到
 *   fetch + ReadableStream 手动解析（兼容旧 CEF）。
 * - 断线后指数退避重连；恢复时后端会先推一条 snapshot，前端可据此全量同步。
 * - 心跳（ping）事件无 state，直接忽略。
 */
export class SseClient {
  /**
   * fetch 回退路径的读停顿阈值（毫秒）：连续无数据超过该值视为连接已死，
   * 主动断开交给退避重连。服务端每 25s 推一个 ping，60s ≈ 2.4 个心跳。
   */
  private static readonly READ_STALL_MS = 60000;

  /** 收到一条状态快照 */
  public onState: ((state: PlayerSnapshot) => void) | null = null;
  /** 连接状态变化（true=在线） */
  public onStatus: ((online: boolean) => void) | null = null;

  private readonly url: string;
  private es: EventSource | null = null;
  private ac: AbortController | null = null;
  private retry = 0;
  private closed = false;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(url: string = API_CONFIG.baseUrl + API_CONFIG.sseEndpoint) {
    this.url = url;
  }

  start(): void {
    this.closed = false;
    this.#connect();
  }

  stop(): void {
    this.closed = true;
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.#teardown();
  }

  // ---- 内部 ----
  #connect(): void {
    if (this.closed) return;

    // 现代环境走 EventSource；老 CEF 无 EventSource 则直接用 fetch 回退。
    if (typeof EventSource !== "undefined") {
      this.es = new EventSource(this.url);
      this.es.onmessage = (e) => this.#handle(e.data);
      this.es.onopen = () => this.#onOpen();
      this.es.onerror = () => this.#onError();
    } else {
      void this.#fetchLoop();
    }
  }

  #teardown(): void {
    if (this.es) {
      this.es.close();
      this.es = null;
    }
    if (this.ac) {
      this.ac.abort();
      this.ac = null;
    }
  }

  #onOpen(): void {
    this.retry = 0;
    this.onStatus?.(true);
  }

  #onError(): void {
    this.#teardown();
    this.#scheduleReconnect();
  }

  #scheduleReconnect(): void {
    if (this.closed) return;
    this.onStatus?.(false);
    // 指数退避：1s、2s、4s… 上限 15s
    const delay = Math.min(1000 * 2 ** this.retry, 15000);
    this.retry += 1;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.#connect();
    }, delay);
  }

  /** fetch + ReadableStream 手动解析 SSE（EventSource 不可用时的兜底）。 */
  async #fetchLoop(): Promise<void> {
    if (this.closed) return;
    this.ac = new AbortController();
    // 锁定本循环自己的 AbortController：停顿定时器只 abort 它，
    // 避免重连后误杀新连接
    const ac = this.ac;
    // 读停顿保护：连续 READ_STALL_MS 无数据（含心跳）视为连接已死，
    // 主动断开触发退避重连；EventSource 路径由浏览器自身心跳负责
    let stallTimer: ReturnType<typeof setTimeout> | null = null;
    const clearStall = (): void => {
      if (stallTimer !== null) {
        clearTimeout(stallTimer);
        stallTimer = null;
      }
    };
    const resetStall = (): void => {
      clearStall();
      stallTimer = setTimeout(() => ac.abort(), SseClient.READ_STALL_MS);
    };
    try {
      resetStall();
      const resp = await fetch(this.url, {
        cache: "no-store",
        signal: ac.signal,
      });
      if (!resp.ok || !resp.body) throw new Error(`SSE HTTP ${resp.status}`);
      this.#onOpen();

      const reader = resp.body.getReader();
      const decoder = new TextDecoder();
      let buf = "";
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        resetStall(); // 收到数据（ping 或状态帧）即重置停顿计时
        buf += decoder.decode(value, { stream: true });
        // 按空行分帧，逐帧解析 data: 行
        let idx;
        while ((idx = buf.indexOf("\n\n")) >= 0) {
          const frame = buf.slice(0, idx);
          buf = buf.slice(idx + 2);
          this.#parseFrame(frame);
        }
      }
      clearStall();
      // 正常结束（服务端主动断开）：若未关闭则重连
      if (!this.closed) this.#onError();
    } catch {
      clearStall();
      if (!this.closed) this.#onError();
    }
  }

  #parseFrame(frame: string): void {
    for (const line of frame.split("\n")) {
      if (line.startsWith("data:")) {
        this.#handle(line.slice(5).trim());
      }
    }
  }

  #handle(raw: string): void {
    if (!raw) return;
    let evt: SseEvent;
    try {
      evt = JSON.parse(raw) as SseEvent;
    } catch {
      return;
    }
    // 按帧类型路由：ping 心跳显式忽略；其余类型带 state 才回传。
    // 未知类型（后端协议演进时）只要带 state 仍按快照处理，避免静默丢弃。
    if (evt.type === "ping") return;
    if (evt.state) this.onState?.(evt.state);
  }
}