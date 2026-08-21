/**
 * 场景层显隐控制：播放歌曲时淡入显示歌词场景，不播放时淡出隐藏。
 *
 * 实现方式：切换 #scene 的 active class，由 CSS transition 完成淡入淡出
 * （淡入 0.55s、淡出 0.9s），避免手写逐帧动画，保证流畅。
 */
export class SceneController {
  private visible = false;

  constructor(private readonly el: HTMLElement) {}

  /** 淡入显示 */
  show(): void {
    if (this.visible) return;
    this.visible = true;
    this.el.classList.add("active");
  }

  /** 淡出隐藏 */
  hide(): void {
    if (!this.visible) return;
    this.visible = false;
    this.el.classList.remove("active");
  }
}
