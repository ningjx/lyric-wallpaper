/**
 * 无播放内容层。
 *
 * 它刻意与歌词/Canvas 渲染器解耦：以后替换成日历、待办或任意小组件时，
 * MusicState 的自动切换逻辑无需改动。
 */
export interface IdleContentRenderer {
  setIdleClock(time: string | null, date?: string): void;
}

export class IdleContent {
  private timer: number | null = null;
  private visible = false;

  constructor(private readonly renderer: IdleContentRenderer) {}

  setVisible(visible: boolean): void {
    if (this.visible === visible) return;
    this.visible = visible;
    if (visible) {
      this.updateClock();
      this.timer = window.setInterval(() => this.updateClock(), 1_000);
    } else if (this.timer !== null) {
      window.clearInterval(this.timer);
      this.timer = null;
      this.renderer.setIdleClock(null);
    }
  }

  dispose(): void {
    if (this.timer !== null) window.clearInterval(this.timer);
    this.timer = null;
    this.renderer.setIdleClock(null);
  }

  private updateClock(): void {
    const now = new Date();
    const time = new Intl.DateTimeFormat("zh-CN", {
      hour: "2-digit", minute: "2-digit", hour12: false,
    }).format(now);
    const date = new Intl.DateTimeFormat("zh-CN", {
      weekday: "long", month: "long", day: "numeric",
    }).format(now);
    this.renderer.setIdleClock(time, date);
  }
}
