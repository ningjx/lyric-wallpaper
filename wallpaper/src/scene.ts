/**
 * 场景层显隐控制。液态壁纸场景启动后保持可见；播放状态只控制歌词内容，
 * 不控制整张 Canvas 的显隐，避免切歌/无歌瞬间露出页面底色。
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
