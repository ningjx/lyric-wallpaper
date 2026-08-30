import type { LyricLine } from "./parser";
import { findCurrentLine } from "./timeline";
import type { LyricsTarget } from "../player/MusicState";

/** 当前行上下各渲染几行 */
const HALF = 3;
/** 窗口行数 */
const WINDOW = HALF * 2 + 1;
/** 默认当前行（最大）字号（px），可在 Wallpaper Engine 属性面板调节 */
const DEFAULT_FONT_SIZE = 72;
/** 默认行距（px），可在 Wallpaper Engine 属性面板调节 */
const DEFAULT_GAP = 130;
/** 各层级字号相对当前行（tier 0）的比例，调节字号时整体按此缩放 */
const FONT_RATIOS = [1, 52 / 72, 38 / 72, 30 / 72];
/** 各层级透明度 */
const OPACITY = [1, 0.5, 0.26, 0.13];
/** 各层级模糊 */
const BLUR = [0, 1.2, 2.4, 3.6];
/** 当前行光晕 */
const GLOW = "0 0 36px rgba(255,255,255,0.5), 0 0 80px rgba(120,160,255,0.35)";

/**
 * 译文占用的额外垂直空间（px）。当某行带译文时，其译文会向下挤压下一行，
 * 因此把"该行到下一行"的间距多留出这段，间距从译文底部算起。
 * 译文为 0.42em、行高 1.3、再加 4px margin，故随字号缩放。
 */
export function transExtraFor(fontSize: number): number {
  return Math.round(0.42 * fontSize * 1.3 + 4);
}

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
 * 当前行索引 cur 变化时，所有元素位置 = (index - cur) * gap 平移一格，
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
  /** 当前行字号（px），由属性面板调节 */
  private fontSize = DEFAULT_FONT_SIZE;
  /** 行距（px），由属性面板调节 */
  private gap = DEFAULT_GAP;
  /** 歌词同步偏移（毫秒），正数提前、负数延后，由属性面板调节 */
  private syncOffset = 0;

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

  /** 每帧调用：按当前播放时间（含同步偏移）推进当前行 */
  update(time: number): void {
    if (this.lines.length === 0) return;
    const next = findCurrentLine(this.lines, time + this.syncOffset / 1000);
    if (next !== this.cur) {
      this.advance(next);
    }
  }

  /** 应用字号/行距设置（Wallpaper Engine 属性面板调节），并平滑重排当前窗口 */
  setLayout(fontSize: number, gap: number): void {
    this.fontSize = fontSize;
    this.gap = gap;
    for (const it of this.pool) {
      if (it.index < 0) continue;
      const offset = it.index - this.cur;
      this.applyItem(it, offset, tierOf(offset), false);
    }
    this.updateFallbackSize();
  }

  /** 设置歌词同步偏移（毫秒），正数提前、负数延后 */
  setSyncOffset(ms: number): void {
    this.syncOffset = ms;
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
    // 译文补偿：该行与当前行之间有多少带译文的行，就向下（上）平移多少，
    // 使译文不挤压下一行，间距从译文底部算起。
    const shift = this.transShift(it.index, this.cur);
    el.style.transform = `translate(-50%, ${offset * this.gap + shift}px)`;
    el.style.fontSize = `${this.fontSizeFor(tier)}px`;
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

  /**
   * 译文垂直补偿：返回某行相对当前行的额外偏移。
   * 一行带译文时，其译文会向下占据与下一行的间距，因此"它到下一行"要多留 transExtra。
   * 等价地，位于当前行之下的行按其间译文数量向下平移，之上的行向上平移。
   */
  private transShift(index: number, cur: number): number {
    if (index === cur) return 0;
    const lo = Math.min(index, cur);
    const hi = Math.max(index, cur);
    let count = 0;
    for (let k = lo; k < hi; k++) {
      if (this.lines[k]?.translated) count++;
    }
    return (index > cur ? 1 : -1) * count * this.transExtra();
  }

  /** 各层级的实际字号（tier 0 = 当前行，最大） */
  private fontSizeFor(tier: number): number {
    return Math.round(this.fontSize * FONT_RATIOS[tier]);
  }

  /** 译文额外占用的垂直空间，随当前字号缩放 */
  private transExtra(): number {
    return transExtraFor(this.fontSize);
  }

  /** 同步 fallback 占位文字的字号（随字号缩放） */
  private updateFallbackSize(): void {
    const el = this.fallbackEl;
    if (!el || el.hidden) return;
    const big = (el.textContent ?? "").length > 0;
    el.style.fontSize = `${Math.round(((big ? 40 : 34) / DEFAULT_FONT_SIZE) * this.fontSize)}px`;
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
      this.updateFallbackSize();
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
