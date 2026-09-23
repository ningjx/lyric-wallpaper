/**
 * Wallpaper Engine 帧率节拍器。
 *
 * 背景：WE 的 Performance → 帧率设置**不会**自动约束 Web 壁纸，它只通过
 * `wallpaperPropertyListener.applyGeneralProperties({ fps })` 把用户的期望值
 * 下发给壁纸，要求壁纸自己在 rAF 回调里按时间累积节流（见 WE 官方
 * “FPS Limiter” 文档）。因此：
 *
 * - 本项目必须自己实现上限，否则 WE 的帧率设置对壁纸完全无效；
 * - 上限只是“天花板”：它无法把帧率抬高到宿主 rAF 节拍之上。
 *
 * fps <= 0 表示不限制，此时 shouldUpdate() 恒为 true，保持与旧行为一致。
 */
export class FramePacer {
  private fps = 0;
  private accumulatedMs = 0;
  private lastMs = Number.NaN;

  /** 设置上限（帧/秒）。非法值或 <= 0 视为不限制。 */
  setFps(fps: number): void {
    const next = Number.isFinite(fps) && fps > 0 ? fps : 0;
    if (next === this.fps) return;
    this.fps = next;
    // 换档后立即放行一帧，避免最长等满一个旧周期才生效。
    this.reset();
  }

  /** 当前上限；0 表示不限制。 */
  getFps(): number {
    return this.fps;
  }

  /** 放弃累积相位，使下一次调用必然放行一帧。 */
  reset(): void {
    this.accumulatedMs = 0;
    this.lastMs = Number.NaN;
  }

  /**
   * 判定本次 rAF 回调是否应当推进动画与渲染。
   * @param nowMs requestAnimationFrame 提供的时间戳（毫秒）
   */
  shouldUpdate(nowMs: number): boolean {
    if (this.fps <= 0) return true;
    if (!Number.isFinite(this.lastMs)) {
      this.lastMs = nowMs;
      return true;
    }
    const stepMs = 1000 / this.fps;
    // 挂起（最小化、暂停、调试断点）后的超长间隔只按 1 秒计，避免恢复瞬间补帧。
    const deltaMs = Math.min(Math.max(0, nowMs - this.lastMs), 1000);
    this.lastMs = nowMs;
    this.accumulatedMs += deltaMs;
    if (this.accumulatedMs < stepMs) return false;
    // 扣掉一整拍而非清零，避免节拍长期漂移；但累积量超过一拍说明刚刚挂起恢复，
    // 这时直接清零，只放行当前这一帧。
    this.accumulatedMs = this.accumulatedMs >= stepMs * 2 ? 0 : this.accumulatedMs - stepMs;
    return true;
  }
}
