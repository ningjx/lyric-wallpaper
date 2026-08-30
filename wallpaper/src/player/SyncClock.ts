/**
 * 本地时钟：播放进度的平滑推进与校准。
 *
 * 架构原则：API 只做"校准"（低频），动画渲染由本地单调时钟驱动（高频）。
 * 播放中：now() = serverPos + (performance.now() - syncAt) / 1000 平滑推进；
 * 暂停：   now() 固定返回暂停时刻的位置；
 * 校准：   若与服务端位置偏差小于阈值，调整锚点保持连续，避免回跳；
 *          偏差较大（切歌/拖进度条/暂停恢复）则硬校准。
 */
export class SyncClock {
  /** 平滑校准阈值（秒）：偏差小于该值时采用平滑衔接 */
  private static readonly SMOOTH_THRESHOLD = 1.5;

  private serverPos = 0;
  private syncAt = 0;
  private playing = false;
  private pausedPos = 0;

  /** 由 API 状态校准本地时钟 */
  calibrate(position: number, playing: boolean): void {
    if (this.playing === playing && this.syncAt > 0) {
      const localNow = this.now();
      if (Math.abs(localNow - position) < SyncClock.SMOOTH_THRESHOLD) {
        // 轻微漂移：调整锚点使校准后 now() 仍在 localNow 处连续（不回跳），
        // 同时以 position 为基线缓慢收敛到服务端位置
        const t0 = performance.now();
        this.serverPos = position;
        this.syncAt = t0 - (localNow - position) * 1000;
        return;
      }
    }
    // 状态变化或大跳变：硬校准
    this.serverPos = position;
    this.syncAt = performance.now();
    this.playing = playing;
    if (!playing) {
      this.pausedPos = position;
    }
  }

  /** 当前本地播放位置（秒） */
  now(): number {
    if (!this.playing) return this.pausedPos;
    return this.serverPos + (performance.now() - this.syncAt) / 1000;
  }

  /** 重置为"未播放"初始状态（无歌曲/音乐软件关闭时调用） */
  reset(): void {
    this.serverPos = 0;
    this.syncAt = 0;
    this.playing = false;
    this.pausedPos = 0;
  }
}
