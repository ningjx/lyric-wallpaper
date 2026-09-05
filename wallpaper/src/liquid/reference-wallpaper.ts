import { LiquidGlassRenderer } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer";
import { springStepCritical } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer/spring";
import type { GlassElementConfig } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer";
import type { LyricLine } from "../lyrics/parser";
import type { LyricsTarget } from "../player/MusicState";
import { BackgroundComposer, type BackgroundLayout } from "./background-composer";

const FONT_RATIO = .050;
const LYRIC_SCROLL_SETTLE_DISTANCE = .75;
const LYRIC_SCROLL_SETTLE_VELOCITY = 3;
const WALLPAPER_SOURCE = `${import.meta.env.BASE_URL}backgrounds/wallhaven-vpolwm.jpg`;
const RENDERER_FONT_FAMILY = '-apple-system, BlinkMacSystemFont, "SF Pro Text", "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';

export interface LiquidSettings {
  lyricFontScale: number; lyricGlassPadding: number; lyricGap: number; lyricVerticalOffset: number; lyricScrollSpeed: number;
  lyricOffsetX: number; lyricOffsetY: number; lyricAlignment: 0 | 1 | 2;
  lyricDepthMinScale: number; lyricDepthScaleFalloff: number; lyricDepthScaleCurve: number;
  lyricDepthAlphaFalloff: number; lyricDepthAlphaCurve: number; lyricDepthGlassFloor: number; lyricDepthCullDistance: number;
  backgroundImage: string; backgroundLayout: BackgroundLayout; backgroundScale: number; backgroundOffsetX: number; backgroundOffsetY: number;
  cornerRadius: number; refractionHeight: number; refractionAmount: number; blurRadius: number; lyricBehindGlass: boolean;
  saturation: number; brightness: number; contrast: number; depthEffect: boolean; chromaticAberration: boolean;
  tintColor: [number, number, number]; tintAlpha: number; surfaceColor: [number, number, number]; surfaceAlpha: number;
  highlight: boolean; highlightMode: 0 | 1 | 2; highlightColor: [number, number, number]; highlightAlpha: number; highlightAngle: number; highlightFalloff: number; highlightWidth: number;
  shadow: boolean; shadowColor: [number, number, number]; shadowAlpha: number; shadowRadius: number; shadowOffsetX: number; shadowOffsetY: number;
  separableBlur: boolean; continuousCorners: boolean; directBackdrop: boolean;
  dpr: number; blurTapCap: number; blurDownsample: number; kawaseBlur: boolean; blurCache: boolean; perElementFbo: boolean;
}

export const DEFAULT_LIQUID_SETTINGS: LiquidSettings = {
  lyricFontScale: .68, lyricGlassPadding: 20, lyricGap: 83, lyricVerticalOffset: 0, lyricScrollSpeed: 5.5,
  lyricOffsetX: 0, lyricOffsetY: -115, lyricAlignment: 0,
  lyricDepthMinScale: .61, lyricDepthScaleFalloff: .55, lyricDepthScaleCurve: 1.63,
  lyricDepthAlphaFalloff: .65, lyricDepthAlphaCurve: 1.12, lyricDepthGlassFloor: .15, lyricDepthCullDistance: 1.5,
  backgroundImage: "", backgroundLayout: 0, backgroundScale: 1, backgroundOffsetX: 0, backgroundOffsetY: 0,
  cornerRadius: 45, refractionHeight: 4, refractionAmount: -34, blurRadius: 0, lyricBehindGlass: false,
  saturation: 1.35, brightness: 0, contrast: 1, depthEffect: true, chromaticAberration: false,
  tintColor: [.18, .52, .72], tintAlpha: 0, surfaceColor: [.80, .94, 1], surfaceAlpha: 0,
  highlight: true, highlightMode: 0, highlightColor: [.72, .92, 1], highlightAlpha: .34, highlightAngle: -1.98, highlightFalloff: 2.1, highlightWidth: 1,
  shadow: true, shadowColor: [.01, .06, .12], shadowAlpha: .18, shadowRadius: 28, shadowOffsetX: 0, shadowOffsetY: 16,
  separableBlur: false, continuousCorners: false, directBackdrop: true,
  dpr: 1, blurTapCap: 9, blurDownsample: 2, kawaseBlur: true, blurCache: true, perElementFbo: true,
};

export class ReferenceLyricsWallpaper implements LyricsTarget {
  private readonly renderer: LiquidGlassRenderer;
  private scrollY = 0;
  private velocity = 0;
  private hasPositioned = false;
  private active = -1;
  private lastFrame = 0;
  private lastLayoutScrollY = Number.NaN;
  private width = 0;
  private height = 0;
  private disposed = false;
  private lyrics: LyricLine[];
  private settings: LiquidSettings = { ...DEFAULT_LIQUID_SETTINGS };
  private readonly textMeasure = document.createElement("canvas").getContext("2d")!;
  private readonly glyphMeasureCanvas = document.createElement("canvas");
  private readonly glyphMeasure = this.glyphMeasureCanvas.getContext("2d", { willReadFrequently: true })!;
  private readonly opticalOffsetRatios = new Map<string, number>();
  private readonly backgroundComposer = new BackgroundComposer();
  private backgroundRequest = 0;

  constructor(
    canvas: HTMLCanvasElement,
    initialLyrics: readonly LyricLine[],
  ) {
    this.lyrics = [...initialLyrics];
    this.renderer = new LiquidGlassRenderer(canvas);
    this.renderer.dpr = Math.min(devicePixelRatio || 1, this.settings.dpr);
    this.renderer.usePerElementFbo = true;
    this.renderer.quickToggles.perElementFbo = true;
    this.renderer.quickToggles.isolateBackdrop = true;
    this.applyRendererSettings();
  }

  async start(): Promise<void> {
    this.resize();
    await this.refreshBackground();
    this.rebuild(0);
  }

  resize(): void {
    this.width = innerWidth;
    this.height = innerHeight;
    this.renderer.resize(this.width, this.height);
    void this.refreshBackground();
    this.rebuild(this.active < 0 ? 0 : this.active);
  }

  draw(seconds: number, active: number): void {
    if (this.disposed) return;
    if (this.lyrics.length === 0) {
      this.renderer.render();
      return;
    }
    const delta = this.lastFrame ? Math.min(.08, Math.max(0, seconds - this.lastFrame)) : .016;
    this.lastFrame = seconds;
    const rowGap = this.rowGap();
    const nextActive = Math.max(0, Math.min(active, this.lyrics.length - 1));
    const target = nextActive * rowGap;
    if (!this.hasPositioned) {
      this.scrollY = target;
      this.velocity = 0;
      this.hasPositioned = true;
    } else {
      const next = springStepCritical(this.scrollY, this.velocity, target, delta, this.settings.lyricScrollSpeed);
      const shouldSettle =
        Math.abs(next.current - target) < LYRIC_SCROLL_SETTLE_DISTANCE &&
        Math.abs(next.velocity) < LYRIC_SCROLL_SETTLE_VELOCITY;
      this.scrollY = shouldSettle ? target : next.current;
      this.velocity = shouldSettle ? 0 : next.velocity;
    }
    this.renderer.setScrollY(this.scrollY);
    if (nextActive !== this.active || Math.abs(this.scrollY - this.lastLayoutScrollY) > .05) {
      this.rebuild(nextActive, this.scrollY / rowGap);
      this.lastLayoutScrollY = this.scrollY;
    }

    this.renderer.render();
  }

  dispose(): void {
    this.disposed = true;
    this.backgroundComposer.dispose();
    this.renderer.dispose();
  }

  getSettings(): LiquidSettings { return { ...this.settings }; }

  /**
   * 将当前行的视觉焦点提前切换，使临界阻尼滚动在歌词时间点到来前收敛。
   * 误差阈值与 draw() 的实际停靠阈值保持一致，避免使用固定的猜测时长。
   */
  getScrollLeadSeconds(): number {
    const rowGap = this.rowGap();
    const omega = Math.max(2, this.settings.lyricScrollSpeed);
    let normalizedTime = 0;
    while (
      normalizedTime < 12 &&
      (
        rowGap * Math.exp(-normalizedTime) * (1 + normalizedTime) > LYRIC_SCROLL_SETTLE_DISTANCE ||
        rowGap * omega * normalizedTime * Math.exp(-normalizedTime) > LYRIC_SCROLL_SETTLE_VELOCITY
      )
    ) {
      normalizedTime += .05;
    }
    const duration = normalizedTime / omega;
    return Math.min(2.5, Math.max(.2, duration + .03));
  }

  getLines(): readonly LyricLine[] { return this.lyrics; }

  setLines(lines: LyricLine[], fallback = ""): void {
    this.lyrics = lines.length > 0 ? [...lines] : fallback ? [{ time: 0, text: fallback }] : [];
    this.active = -1;
    this.scrollY = 0;
    this.velocity = 0;
    this.hasPositioned = false;
    this.rebuild(0);
    this.renderer.markAllDirty();
    this.renderer.requestRender();
  }

  clear(): void {
    this.setLines([]);
  }

  setSettings(patch: Partial<LiquidSettings>): void {
    // 使用变更前的行距保留当前滚动位置对应的歌词序号。否则调节行距时会
    // 用新行距除旧 scrollY，造成焦点跳行，掩盖了行距本身的视觉变化。
    const previousRowGap = this.rowGap();
    const previousFocus = previousRowGap > 0 ? this.scrollY / previousRowGap : this.active;
    const previousDpr = this.settings.dpr;
    const previousDownsample = this.settings.blurDownsample;
    const backgroundChanged =
      patch.backgroundImage !== undefined || patch.backgroundLayout !== undefined ||
      patch.backgroundScale !== undefined || patch.backgroundOffsetX !== undefined ||
      patch.backgroundOffsetY !== undefined ||
      (patch.dpr !== undefined && previousDpr !== patch.dpr);
    this.settings = { ...this.settings, ...patch };
    this.applyRendererSettings();
    if (previousDpr !== this.settings.dpr || previousDownsample !== this.settings.blurDownsample) {
      this.renderer.resize(this.width, this.height);
    }
    if (backgroundChanged) void this.refreshBackground();
    this.rebuild(this.active, previousFocus);
    this.lastLayoutScrollY = this.scrollY;
    this.renderer.markAllDirty();
    this.renderer.requestRender();
  }

  private rebuild(active: number, focus = active): void {
    if (!this.width || !this.height) return;
    if (this.lyrics.length === 0) {
      this.renderer.setElements([]);
      this.renderer.setContentHeight(this.height);
      return;
    }
    this.active = Math.max(0, Math.min(active, this.lyrics.length - 1));
    const rowGap = this.rowGap();
    const centerY = this.height / 2;
    const rows: GlassElementConfig[] = [];
    const renderWindow = Math.ceil(this.settings.lyricDepthCullDistance);
    const first = Math.max(0, Math.floor(focus - renderWindow));
    const last = Math.min(this.lyrics.length - 1, Math.ceil(focus + renderWindow));
    for (let index = first; index <= last; index++) {
      const distance = Math.abs(index - focus);
      const scale = this.depthScale(distance);
      const alpha = this.depthAlpha(distance);
      const glassStrength = this.depthGlassStrength(alpha);
      const typography = this.fitTypography(this.lyrics[index].text, 700, scale);
      const rect = this.lyricRect(index, centerY, rowGap, typography);
      const textRect = { ...rect, y: rect.y + typography.opticalOffset + this.settings.lyricVerticalOffset };
      const textRow: GlassElementConfig = {
        ...this.base(`lyric-${index}`, "text", textRect),
        scroll: true,
        text: {
          content: this.lyrics[index].text,
          color: [0.94, 0.985, 1, alpha],
          fontSizePx: typography.fontSize,
          fontWeight: 700,
          align: "center",
          halo: "dark",
        },
      };
      const glassRow = this.glassRow(index, rect, glassStrength);
      // 背面模式先绘制文字，再让玻璃从场景纹理采样：文字因此参与折射。
      // 玻璃卡片会禁用模糊，避免把印在背面的字也模糊掉。
      if (this.settings.lyricBehindGlass) rows.push(textRow, glassRow);
      else rows.push(glassRow, textRow);
    }
    this.renderer.setElements(rows);
    this.renderer.setContentHeight(this.height + Math.max(0, this.lyrics.length - 1) * rowGap);
    this.renderer.setScrollY(this.scrollY);
  }

  private async refreshBackground(): Promise<void> {
    if (!this.width || !this.height || this.disposed) return;
    const request = ++this.backgroundRequest;
    const s = this.settings;
    const source = this.backgroundSource(s.backgroundImage);
    try {
      const texture = await this.backgroundComposer.compose({
        source,
        layout: s.backgroundLayout,
        scale: s.backgroundScale,
        offsetX: s.backgroundOffsetX,
        offsetY: s.backgroundOffsetY,
        pixelRatio: Math.min(devicePixelRatio || 1, s.dpr),
      }, this.width, this.height);
      if (request !== this.backgroundRequest || this.disposed) {
        this.backgroundComposer.discard(texture);
        return;
      }
      await this.renderer.loadWallpaper(texture);
      if (request !== this.backgroundRequest || this.disposed) {
        this.backgroundComposer.discard(texture);
        if (!this.disposed) void this.refreshBackground();
        return;
      }
      this.backgroundComposer.activate(texture);
    } catch (error) {
      // 部分 Wallpaper Engine Chromium 对 file:/// → Canvas 的回写有限制。
      // 此时直接上传原图，至少保证自定义背景不会静默回退为默认图。
      if (request === this.backgroundRequest && !this.disposed && source !== WALLPAPER_SOURCE) {
        try {
          await this.backgroundComposer.uploadSource(this.renderer, source);
        } catch {
          await this.renderer.loadWallpaper(WALLPAPER_SOURCE);
        }
      }
    }
  }

  private backgroundSource(value: string): string {
    if (!value) return WALLPAPER_SOURCE;
    // Windows 盘符（C:\\...）不能被当作 URL scheme；Wallpaper Engine 的
    // file 属性正是以这种本地路径形式返回值。
    if (/^[a-z]:[\\/]/i.test(value)) return `file:///${value.replace(/\\/g, "/")}`;
    if (/^[a-z][a-z0-9+.-]*:/i.test(value)) return value;
    return `file:///${value.replace(/\\/g, "/")}`;
  }

  private rowGap(): number {
    // lyricGap 是用户可见的额外间距，不能再被总行距上限吞掉。
    return Math.max(96, this.height * .135 + this.settings.lyricGap);
  }

  private depthScale(distance: number): number {
    const s = this.settings;
    // (1 + d)^curve - 1 保持主歌词 d=0 为 0，同时避免 d=1 在所有曲线下
    // 都恒定为 1；相邻歌词也会随曲线参数自然变化。
    const shapedDistance = (1 + distance) ** s.lyricDepthScaleCurve - 1;
    return s.lyricDepthMinScale + (1 - s.lyricDepthMinScale) /
      (1 + s.lyricDepthScaleFalloff * shapedDistance);
  }

  private depthAlpha(distance: number): number {
    const s = this.settings;
    const shapedDistance = (1 + distance) ** s.lyricDepthAlphaCurve - 1;
    return Math.exp(-s.lyricDepthAlphaFalloff * shapedDistance);
  }

  private depthGlassStrength(alpha: number): number {
    const floor = this.settings.lyricDepthGlassFloor;
    return floor + (1 - floor) * alpha;
  }

  private fitTypography(text: string, fontWeight: number, scale: number): { fontSize: number; width: number; height: number; padding: number; opticalOffset: number } {
    // 字号完全由“字体大小”和空间深度决定。长歌词保留同样的字号，
    // 让背板自然变宽，绝不再根据字符串长度反算缩小。
    const fontSize = Math.max(18, this.width * FONT_RATIO * scale * this.settings.lyricFontScale);
    const metrics = this.measureText(text, fontWeight, fontSize);
    return { fontSize, ...metrics, padding: this.lyricGlassPadding() };
  }

  private lyricGlassPadding(): number {
    return Math.max(0, Math.round(this.settings.lyricGlassPadding));
  }

  private measureText(text: string, fontWeight: number, fontSize: number): { width: number; height: number; opticalOffset: number } {
    this.textMeasure.font = `${fontWeight} ${fontSize}px ${RENDERER_FONT_FAMILY}`;
    const metrics = this.textMeasure.measureText(text);
    const ascent = metrics.actualBoundingBoxAscent || fontSize * .8;
    const descent = metrics.actualBoundingBoxDescent || fontSize * .2;
    const height = Math.max(fontSize, ascent + descent);
    const key = `${fontWeight}:${text}`;
    let opticalOffsetRatio = this.opticalOffsetRatios.get(key);
    if (opticalOffsetRatio == null) {
      opticalOffsetRatio = this.measureOpticalOffset(text, fontWeight, fontSize, height) / fontSize;
      this.opticalOffsetRatios.set(key, opticalOffsetRatio);
    }
    return { width: Math.ceil(metrics.width), height: Math.ceil(height), opticalOffset: opticalOffsetRatio * fontSize };
  }

  private measureOpticalOffset(text: string, fontWeight: number, fontSize: number, glyphHeight: number): number {
    const padding = Math.ceil(fontSize * .45);
    const width = Math.ceil(this.textMeasure.measureText(text).width + padding * 2);
    const height = Math.ceil(glyphHeight + padding * 2);
    const canvas = this.glyphMeasureCanvas;
    canvas.width = width;
    canvas.height = height;
    const ctx = this.glyphMeasure;
    ctx.clearRect(0, 0, width, height);
    ctx.font = `${fontWeight} ${fontSize}px ${RENDERER_FONT_FAMILY}`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#fff";
    ctx.fillText(text, width / 2, height / 2 + .5);
    const pixels = ctx.getImageData(0, 0, width, height).data;
    let top = height;
    let bottom = -1;
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        if (pixels[(y * width + x) * 4 + 3] >= 128) {
          top = Math.min(top, y);
          bottom = Math.max(bottom, y);
        }
      }
    }
    return bottom < top ? 0 : height / 2 - (top + bottom + 1) / 2;
  }

  private lyricRect(index: number, centerY: number, rowGap: number, typography: { width: number; height: number; padding: number }): { x: number; y: number; w: number; h: number } {
    const w = typography.width + typography.padding * 2;
    const h = typography.height + typography.padding * 2;
    const inset = Math.max(42, this.width * .08);
    const alignedX = this.settings.lyricAlignment === 1
      ? inset
      : this.settings.lyricAlignment === 2
        ? this.width - inset - w
        : (this.width - w) / 2;
    return {
      x: alignedX + this.settings.lyricOffsetX,
      y: centerY + this.settings.lyricOffsetY - h / 2 + index * rowGap,
      w,
      h,
    };
  }

  private glassRow(index: number, rect: { x: number; y: number; w: number; h: number }, strength: number): GlassElementConfig {
    const lyricBehindGlass = this.settings.lyricBehindGlass;
    return {
      ...this.base(`lyric-glass-${index}`, "glass-shape", rect),
      cornerRadius: Math.min(this.settings.cornerRadius, rect.h * .45),
      refractionHeight: this.settings.refractionHeight * strength,
      refractionAmount: this.settings.refractionAmount * strength,
      depthEffect: this.settings.depthEffect,
      chromaticAberration: this.settings.chromaticAberration,
      blurRadius: lyricBehindGlass ? 0 : this.settings.blurRadius * strength,
      saturation: this.settings.saturation,
      brightness: this.settings.brightness,
      contrast: this.settings.contrast,
      surfaceColor: [...this.settings.surfaceColor, this.settings.surfaceAlpha * strength],
      tintColor: [...this.settings.tintColor, this.settings.tintAlpha * strength],
      highlight: this.settings.highlight ? { mode: this.settings.highlightMode, color: this.settings.highlightColor, angle: this.settings.highlightAngle, falloff: this.settings.highlightFalloff, alpha: this.settings.highlightAlpha * strength, widthDp: this.settings.highlightWidth } : null,
      outerShadow: this.settings.shadow ? { radius: this.settings.shadowRadius, alpha: this.settings.shadowAlpha * strength, offsetX: this.settings.shadowOffsetX, offsetY: this.settings.shadowOffsetY, color: this.settings.shadowColor } : null,
      // 高质量模糊以原始壁纸为统一背板：上游渲染器会缓存同半径的全场
      // 高斯结果，滚动时也不会被场景模糊的每帧限流回退为清晰纹理。
      independentBackdrop: this.settings.separableBlur && !lyricBehindGlass,
      directBackdropSample: false,
      // The inline wallpaper path is bounded to this card's pixels and keeps
      // scroll coordinates exact. The optional high-quality setting switches
      // back to the renderer's full-scene separable blur.
      sampleWallpaper: !this.settings.separableBlur && !lyricBehindGlass,
      useSeparableBlur: this.settings.separableBlur && !lyricBehindGlass,
      useContinuousSdf: this.settings.continuousCorners,
      scroll: true,
    };
  }

  private base(id: string, kind: GlassElementConfig["kind"], rect: { x: number; y: number; w: number; h: number }): GlassElementConfig {
    return {
      id, kind, rect, cornerRadius: 0,
      refractionHeight: 0, refractionAmount: 0, depthEffect: false, chromaticAberration: false,
      blurRadius: 0, saturation: 1, brightness: 0, contrast: 1,
      tintColor: [0, 0, 0, 0], surfaceColor: [0, 0, 0, 0], highlight: null, outerShadow: null,
      label: "", labelColor: [1, 1, 1, 1], showChevron: false, isInteractive: false,
    };
  }

  private applyRendererSettings(): void {
    const s = this.settings;
    this.renderer.dpr = Math.min(devicePixelRatio || 1, s.dpr);
    this.renderer.blurTapCap = Math.round(s.blurTapCap);
    this.renderer.blurDownsample = Math.round(s.blurDownsample);
    this.renderer.useKawaseBlur = s.kawaseBlur;
    this.renderer.useBlurCache = s.blurCache;
    this.renderer.usePerElementFbo = s.perElementFbo;
    this.renderer.directBackdropSample = s.directBackdrop;
    this.renderer.quickToggles.highlight = s.highlight;
    this.renderer.quickToggles.backdropBlur = s.separableBlur;
    this.renderer.quickToggles.chromatic = s.chromaticAberration;
    this.renderer.quickToggles.refraction = s.refractionHeight > 0 || s.refractionAmount !== 0;
    this.renderer.quickToggles.outerShadow = s.shadow;
    this.renderer.quickToggles.perElementFbo = s.perElementFbo;
  }
}
