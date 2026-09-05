import { LiquidGlassRenderer } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer";
import { springStepCritical } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer/spring";
import type { GlassElementConfig } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer";
import type { LyricLine } from "../lyrics/parser";

const FONT_RATIO = .050;
const WALLPAPER_SOURCE = `${import.meta.env.BASE_URL}backgrounds/wallhaven-vpolwm.jpg`;
const RENDERER_FONT_FAMILY = '-apple-system, BlinkMacSystemFont, "SF Pro Text", "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';

export interface LiquidSettings {
  lyricFontScale: number; lyricGap: number; lyricVerticalOffset: number; lyricScrollSpeed: number;
  lyricOffsetX: number; lyricOffsetY: number; lyricAlignment: 0 | 1 | 2;
  lyricDepthMinScale: number; lyricDepthScaleFalloff: number; lyricDepthScaleCurve: number;
  lyricDepthAlphaFalloff: number; lyricDepthAlphaCurve: number; lyricDepthGlassFloor: number; lyricDepthCullDistance: number;
  cornerRadius: number; refractionHeight: number; refractionAmount: number; blurRadius: number;
  saturation: number; brightness: number; contrast: number; depthEffect: boolean; chromaticAberration: boolean;
  tintColor: [number, number, number]; tintAlpha: number; surfaceColor: [number, number, number]; surfaceAlpha: number;
  highlight: boolean; highlightMode: 0 | 1 | 2; highlightColor: [number, number, number]; highlightAlpha: number; highlightAngle: number; highlightFalloff: number; highlightWidth: number;
  shadow: boolean; shadowColor: [number, number, number]; shadowAlpha: number; shadowRadius: number; shadowOffsetX: number; shadowOffsetY: number;
  separableBlur: boolean; continuousCorners: boolean; directBackdrop: boolean;
  dpr: number; blurTapCap: number; blurDownsample: number; kawaseBlur: boolean; blurCache: boolean; perElementFbo: boolean;
}

export const DEFAULT_LIQUID_SETTINGS: LiquidSettings = {
  lyricFontScale: .77, lyricGap: 120, lyricVerticalOffset: 0, lyricScrollSpeed: 5.5,
  lyricOffsetX: 0, lyricOffsetY: 0, lyricAlignment: 0,
  lyricDepthMinScale: .58, lyricDepthScaleFalloff: .55, lyricDepthScaleCurve: 1.7,
  lyricDepthAlphaFalloff: .65, lyricDepthAlphaCurve: 1.35, lyricDepthGlassFloor: .15, lyricDepthCullDistance: 3.5,
  cornerRadius: 29, refractionHeight: 22, refractionAmount: -34, blurRadius: 2,
  saturation: 1.42, brightness: 0, contrast: 1, depthEffect: true, chromaticAberration: true,
  tintColor: [.18, .52, .72], tintAlpha: .03, surfaceColor: [.80, .94, 1], surfaceAlpha: .04,
  highlight: true, highlightMode: 0, highlightColor: [.72, .92, 1], highlightAlpha: .34, highlightAngle: -1.05, highlightFalloff: 2.1, highlightWidth: 1,
  shadow: true, shadowColor: [.01, .06, .12], shadowAlpha: .18, shadowRadius: 28, shadowOffsetX: 0, shadowOffsetY: 16,
  separableBlur: true, continuousCorners: true, directBackdrop: true,
  dpr: 1.5, blurTapCap: 9, blurDownsample: 2, kawaseBlur: true, blurCache: true, perElementFbo: true,
};

export class ReferenceLyricsWallpaper {
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
  private settings: LiquidSettings = { ...DEFAULT_LIQUID_SETTINGS };
  private readonly textMeasure = document.createElement("canvas").getContext("2d")!;
  private readonly glyphMeasureCanvas = document.createElement("canvas");
  private readonly glyphMeasure = this.glyphMeasureCanvas.getContext("2d", { willReadFrequently: true })!;
  private readonly opticalOffsetRatios = new Map<string, number>();

  constructor(
    canvas: HTMLCanvasElement,
    private readonly lyrics: readonly LyricLine[],
  ) {
    this.renderer = new LiquidGlassRenderer(canvas);
    this.renderer.dpr = Math.min(devicePixelRatio || 1, this.settings.dpr);
    this.renderer.usePerElementFbo = true;
    this.renderer.quickToggles.perElementFbo = true;
    this.renderer.quickToggles.isolateBackdrop = true;
    this.applyRendererSettings();
  }

  async start(): Promise<void> {
    this.resize();
    await this.renderer.loadWallpaper(WALLPAPER_SOURCE);
    this.rebuild(0);
  }

  resize(): void {
    this.width = innerWidth;
    this.height = innerHeight;
    this.renderer.resize(this.width, this.height);
    this.rebuild(this.active < 0 ? 0 : this.active);
  }

  draw(seconds: number, active: number): void {
    if (this.disposed) return;
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
      this.scrollY = Math.abs(next.current - target) < .1 && Math.abs(next.velocity) < .1 ? target : next.current;
      this.velocity = next.velocity;
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
    this.renderer.dispose();
  }

  getSettings(): LiquidSettings { return { ...this.settings }; }

  setSettings(patch: Partial<LiquidSettings>): void {
    const previousDpr = this.settings.dpr;
    const previousDownsample = this.settings.blurDownsample;
    this.settings = { ...this.settings, ...patch };
    this.applyRendererSettings();
    if (previousDpr !== this.settings.dpr || previousDownsample !== this.settings.blurDownsample) {
      this.renderer.resize(this.width, this.height);
    }
    this.rebuild(this.active, this.scrollY / this.rowGap());
    this.lastLayoutScrollY = this.scrollY;
    this.renderer.markAllDirty();
    this.renderer.requestRender();
  }

  private rebuild(active: number, focus = active): void {
    if (!this.width || !this.height) return;
    this.active = Math.max(0, Math.min(active, this.lyrics.length - 1));
    const rowGap = this.rowGap();
    const centerY = this.height / 2;
    const rows: GlassElementConfig[] = [];
    const renderWindow = Math.ceil(this.settings.lyricDepthCullDistance) + 1;
    const first = Math.max(0, Math.floor(focus - renderWindow));
    const last = Math.min(this.lyrics.length - 1, Math.ceil(focus + renderWindow));
    for (let index = first; index <= last; index++) {
      const distance = Math.abs(index - focus);
      const scale = this.depthScale(distance);
      const alpha = this.depthAlpha(distance);
      const glassStrength = this.depthGlassStrength(alpha);
      const typography = this.fitTypography(this.lyrics[index].text, 700, scale);
      const rect = this.lyricRect(index, centerY, rowGap, typography);
      rows.push(this.glassRow(index, rect, glassStrength));
      const textRect = { ...rect, y: rect.y + typography.opticalOffset + this.settings.lyricVerticalOffset };
      rows.push({
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
      });
    }
    this.renderer.setElements(rows);
    this.renderer.setContentHeight(this.height + Math.max(0, this.lyrics.length - 1) * rowGap);
    this.renderer.setScrollY(this.scrollY);
  }

  private rowGap(): number { return Math.min(240, Math.max(96, this.height * .135) + this.settings.lyricGap); }

  private depthScale(distance: number): number {
    const s = this.settings;
    return s.lyricDepthMinScale + (1 - s.lyricDepthMinScale) /
      (1 + s.lyricDepthScaleFalloff * distance ** s.lyricDepthScaleCurve);
  }

  private depthAlpha(distance: number): number {
    const s = this.settings;
    return Math.exp(-s.lyricDepthAlphaFalloff * distance ** s.lyricDepthAlphaCurve);
  }

  private depthGlassStrength(alpha: number): number {
    const floor = this.settings.lyricDepthGlassFloor;
    return floor + (1 - floor) * alpha;
  }

  private fitTypography(text: string, fontWeight: number, scale: number): { fontSize: number; width: number; height: number; padding: number; opticalOffset: number } {
    let fontSize = Math.max(28, this.width * FONT_RATIO * scale * this.settings.lyricFontScale);
    const maxWidth = Math.min(this.width * .82, 1480);
    for (let pass = 0; pass < 3; pass++) {
      const metrics = this.measureText(text, fontWeight, fontSize);
      const padding = Math.max(18, Math.round(fontSize * .26));
      const available = maxWidth - padding * 2;
      if (metrics.width <= available) return { fontSize, ...metrics, padding };
      fontSize = Math.max(28, fontSize * available / metrics.width);
    }
    const metrics = this.measureText(text, fontWeight, fontSize);
    return { fontSize, ...metrics, padding: Math.max(18, Math.round(fontSize * .26)) };
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
    return {
      ...this.base(`lyric-glass-${index}`, "glass-shape", rect),
      cornerRadius: Math.min(this.settings.cornerRadius, rect.h * .45),
      refractionHeight: this.settings.refractionHeight * strength,
      refractionAmount: this.settings.refractionAmount * strength,
      depthEffect: this.settings.depthEffect,
      chromaticAberration: this.settings.chromaticAberration,
      blurRadius: this.settings.blurRadius * strength,
      saturation: this.settings.saturation,
      brightness: this.settings.brightness,
      contrast: this.settings.contrast,
      surfaceColor: [...this.settings.surfaceColor, this.settings.surfaceAlpha * strength],
      tintColor: [...this.settings.tintColor, this.settings.tintAlpha * strength],
      highlight: this.settings.highlight ? { mode: this.settings.highlightMode, color: this.settings.highlightColor, angle: this.settings.highlightAngle, falloff: this.settings.highlightFalloff, alpha: this.settings.highlightAlpha * strength, widthDp: this.settings.highlightWidth } : null,
      outerShadow: this.settings.shadow ? { radius: this.settings.shadowRadius, alpha: this.settings.shadowAlpha * strength, offsetX: this.settings.shadowOffsetX, offsetY: this.settings.shadowOffsetY, color: this.settings.shadowColor } : null,
      independentBackdrop: true,
      directBackdropSample: this.settings.directBackdrop,
      useSeparableBlur: this.settings.separableBlur,
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
