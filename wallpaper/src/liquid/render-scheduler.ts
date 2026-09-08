/**
 * 项目层的帧提交门控。
 *
 * 上游 renderer 的 setScrollY() 是命令式 API：同值重复提交也会请求一次
 * render。本类把“是否有新的视觉状态需要提交”固定在本地适配层，避免把
 * Wallpaper 的 rAF 调度细节扩散到歌词布局与上游渲染器中。
 */
export class ScrollRenderGate {
  /**
   * 远低于一个设备像素的位移不会带来可见差异，却会让 renderer 重新合成一帧。
   * 该值必须与歌词布局重建使用的阈值保持一致。
   */
  static readonly VISUAL_THRESHOLD_CSS_PX = .05;

  private lastValue = Number.NaN;

  shouldCommit(value: number): boolean {
    if (Math.abs(value - this.lastValue) <= ScrollRenderGate.VISUAL_THRESHOLD_CSS_PX) return false;
    this.lastValue = value;
    return true;
  }

  reset(): void {
    this.lastValue = Number.NaN;
  }
}
