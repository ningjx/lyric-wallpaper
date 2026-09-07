/**
 * 项目层的帧提交门控。
 *
 * 上游 renderer 的 setScrollY() 是命令式 API：同值重复提交也会请求一次
 * render。本类把“是否有新的视觉状态需要提交”固定在本地适配层，避免把
 * Wallpaper 的 rAF 调度细节扩散到歌词布局与上游渲染器中。
 */
export class ScrollRenderGate {
  private lastValue = Number.NaN;

  shouldCommit(value: number): boolean {
    if (Math.abs(value - this.lastValue) <= Number.EPSILON) return false;
    this.lastValue = value;
    return true;
  }

  reset(): void {
    this.lastValue = Number.NaN;
  }
}
