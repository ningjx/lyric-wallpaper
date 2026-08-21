import type { LyricLine } from "./parser";
import { findCurrentLine } from "./timeline";
import type { LyricsTarget } from "../player/MusicState";

/** 当前行上下各渲染几行 */
const HALF = 3;
/** 窗口行数 */
const WINDOW = HALF * 2 + 1;
/** 行间距（px），由 CSS 变量 --gap 同步 */
const GAP = 130;
/** 各层级字号（tier 0 = 当前行，最大） */
const FONT_SIZE = [72, 52, 38, 30];
/** 各层级透明度 */
const OPACITY = [1, 0.5, 0.26, 0.13];
/** 各层级模糊 */
const BLUR = [0, 1.2, 2.4, 3.6];
/** 当前行光晕 */
const GLOW = "0 0 36px rgba(255,255,255,0.5), 0 0 80px rgba(120,160,255,0.35)";

interface PoolItem {
  el: HTMLElement;
  textEl: HTMLElement;
  transEl: HTMLElement;
  /** 该元素当前对应的绝对歌词行索引（-1 = 未分配） */
  index: number;
}

/**
 * 歌词渲染器：窗口行池 + 平滑滚动，当前句永远居中、字号最大。
 *
 * 行池原理：固定 N 个 DOM 元素，每个绑定一个"绝对歌词行索引"。
 * 当前行索引 cur 变化时，所有元素位置 = (index - cur) * GAP 平移一格，
 * 文本不变（同一逻辑行），由 CSS transition 产生平滑滚动；
 * 滑出窗口的元素被回收并分配到窗口另一侧，瞬移到屏外再滑入。
 *
 * update(time) 每帧由 rAF 调用，仅在 cur 变化时触发布局更新（事件驱动），
 * 其余时间零 DOM 操作 —— 保证动画流畅。
 */
export class LyricsRenderer implements LyricsTarget {
  private lines: LyricLine[] = [];
  private pool: PoolItem[] = [];
  private cur = -1;
  private fallbackEl: HTMLElement | null = null;
  private fallbackVisible = false;

  constructor(container: HTMLElement) {
    for (let i = 0; i < WINDOW; i++) {
      const el = document.createElement("div");
      el.className = "line";
      const textEl = document.createElement("span");
      textEl.className = "text";
      const transEl = document.createElement("span");
      transEl.className = "trans";
      el.append(textEl, transEl);
      container.appendChild(el);
      this.pool.push({ el, textEl, transEl, index: -1 });
    }

    // fallback：无歌词时居中显示歌名
    this.fallbackEl = document.createElement("div");
    this.fallbackEl.className = "fallback";
    this.fallbackEl.hidden = true;
    container.appendChild(this.fallbackEl);
  }

  /** 设置歌词行（fallback 用于无歌词/加载失败时的占位文案） */
  setLines(lines: LyricLine[], fallback?: string): void {
    this.lines = lines;
    if (lines.length === 0) {
      this.showFallback(fallback ?? "");
      return;
    }
    this.hideFallback();
    this.cur = -1; // 强制重新定位
    this.resetPool();
  }

  /** 清空歌词（未播放/无歌曲） */
  clear(): void {
    this.lines = [];
    this.cur = -1;
    this.showFallback("");
  }

  /** 每帧调用：按当前播放时间推进当前行 */
  update(time: number): void {
    if (this.lines.length === 0) return;
    const next = findCurrentLine(this.lines, time);
    if (next !== this.cur) {
      this.advance(next);
    }
  }

  private resetPool(): void {
    for (const it of this.pool) {
      it.index = -1;
      it.el.style.transition = "none";
      it.el.style.opacity = "0";
    }
  }

  private advance(nextCur: number): void {
    const prevCur = this.cur;
    // 小幅推进平滑滚动；大步跳转（拖进度条）直接跳，避免整屏滚动动画
    const animate = prevCur >= 0 && Math.abs(nextCur - prevCur) <= 2;
    this.cur = nextCur;

    // 1) 仍在窗口内的元素：更新位置（过渡动画）
    for (const it of this.pool) {
      if (it.index < 0) continue;
      const offset = it.index - this.cur;
      if (offset < -HALF || offset > HALF) {
        it.index = -1; // 滑出窗口，标记回收
      } else {
        this.applyItem(it, offset, tierOf(offset), !animate);
      }
    }

    // 2) 确保窗口 [cur-HALF, cur+HALF] 内每个逻辑行都有池元素
    for (let i = -HALF; i <= HALF; i++) {
      const index = this.cur + i;
      if (index < 0 || index >= this.lines.length) continue;
      if (this.pool.some((p) => p.index === index)) continue;
      const free = this.pool.find((p) => p.index < 0);
      if (free) this.assignItem(free, index, !animate);
    }
  }

  /**
   * 分配元素到新行：
   * - instant：直接定位（切歌/跳转）
   * - 动画：先瞬移到屏外对应侧，更新文本后滑入窗口
   */
  private assignItem(it: PoolItem, index: number, instant: boolean): void {
    it.index = index;
    this.updateText(it, index);
    const offset = index - this.cur;
    if (instant) {
      this.applyItem(it, offset, tierOf(offset), true);
      return;
    }
    // 阶段 1：瞬移到窗口外一侧
    const edge = offset > 0 ? offset + 1 : offset - 1;
    it.el.style.transition = "none";
    this.applyItem(it, edge, tierOf(edge), true);
    void it.el.offsetWidth; // 强制 reflow，确保瞬移生效
    // 阶段 2：滑入窗口
    it.el.style.transition = "";
    this.applyItem(it, offset, tierOf(offset), false);
  }

  private applyItem(it: PoolItem, offset: number, tier: number, instant: boolean): void {
    const { el } = it;
    if (instant) el.style.transition = "none";
    el.classList.toggle("current", offset === 0);
    el.style.transform = `translate(-50%, ${offset * GAP}px)`;
    el.style.fontSize = `${FONT_SIZE[tier]}px`;
    el.style.opacity = `${OPACITY[tier]}`;
    el.style.filter = tier === 0 ? "none" : `blur(${BLUR[tier]}px)`;
    el.style.textShadow = tier === 0 ? GLOW : "none";
    if (instant) {
      void el.offsetWidth;
      el.style.transition = "";
    }
  }

  private updateText(it: PoolItem, index: number): void {
    const line = this.lines[index];
    it.textEl.textContent = line ? line.text : "";
    it.transEl.textContent = line?.translated ? line.translated : "";
    it.transEl.style.display = line?.translated ? "" : "none";
  }

  private showFallback(text: string): void {
    this.fallbackVisible = true;
    this.pool.forEach((it) => {
      it.index = -1;
      it.el.classList.remove("current");
      it.el.style.opacity = "0";
      it.el.style.transition = "none";
    });
    if (this.fallbackEl) {
      this.fallbackEl.textContent = text;
      this.fallbackEl.hidden = false;
      this.fallbackEl.classList.toggle("with-text", text.length > 0);
    }
  }

  private hideFallback(): void {
    if (!this.fallbackVisible) return;
    this.fallbackVisible = false;
    if (this.fallbackEl) {
      this.fallbackEl.hidden = true;
      this.fallbackEl.classList.remove("with-text");
    }
  }
}

function tierOf(offset: number): number {
  return Math.min(Math.abs(offset), HALF);
}
