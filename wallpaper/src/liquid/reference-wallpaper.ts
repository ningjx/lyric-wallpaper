import { LiquidGlassRenderer } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer";
import { springStepCritical } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer/spring";
import type { GlassElementConfig } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer";
import type { LyricLine } from "../lyrics/parser";

const FONT_RATIO = .050;
const ROW_WINDOW = 4;
const LYRIC_SPRING = 15;
const WALLPAPER_SOURCE = `${import.meta.env.BASE_URL}backgrounds/wallhaven-vpolwm.jpg`;

export interface LiquidSettings {
  cornerRadius: number; refractionHeight: number; refractionAmount: number; blurRadius: number;
  saturation: number; brightness: number; contrast: number; depthEffect: boolean; chromaticAberration: boolean;
  tintColor: [number, number, number]; tintAlpha: number; surfaceColor: [number, number, number]; surfaceAlpha: number;
  highlight: boolean; highlightMode: 0 | 1 | 2; highlightColor: [number, number, number]; highlightAlpha: number; highlightAngle: number; highlightFalloff: number; highlightWidth: number;
  shadow: boolean; shadowColor: [number, number, number]; shadowAlpha: number; shadowRadius: number; shadowOffsetX: number; shadowOffsetY: number;
  separableBlur: boolean; continuousCorners: boolean; directBackdrop: boolean;
  dpr: number; blurTapCap: number; blurDownsample: number; kawaseBlur: boolean; blurCache: boolean; perElementFbo: boolean;
}

export const DEFAULT_LIQUID_SETTINGS: LiquidSettings = {
  cornerRadius: 72, refractionHeight: 22, refractionAmount: -34, blurRadius: 18,
  saturation: 1.42, brightness: 0, contrast: 1, depthEffect: true, chromaticAberration: true,
  tintColor: [.18, .52, .72], tintAlpha: .025, surfaceColor: [.80, .94, 1], surfaceAlpha: .035,
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
  private width = 0;
  private height = 0;
  private disposed = false;
  private settings: LiquidSettings = { ...DEFAULT_LIQUID_SETTINGS };

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
    if (active !== this.active) this.rebuild(active);
    const rowGap = this.rowGap();
    const target = Math.max(0, active) * rowGap;
    if (!this.hasPositioned) {
      this.scrollY = target;
      this.velocity = 0;
      this.hasPositioned = true;
    } else {
      const next = springStepCritical(this.scrollY, this.velocity, target, delta, LYRIC_SPRING);
      this.scrollY = Math.abs(next.current - target) < .1 && Math.abs(next.velocity) < .1 ? target : next.current;
      this.velocity = next.velocity;
    }
    this.renderer.setScrollY(this.scrollY);

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
    this.rebuild(this.active);
    this.renderer.markAllDirty();
    this.renderer.requestRender();
  }

  private rebuild(active: number): void {
    if (!this.width || !this.height) return;
    this.active = Math.max(0, Math.min(active, this.lyrics.length - 1));
    const panel = this.panel();
    const rowGap = this.rowGap();
    const rowHeight = Math.max(68, rowGap * .78);
    const centerY = panel.y + panel.h / 2;
    const rows: GlassElementConfig[] = [this.glassPanel(panel)];
    const first = Math.max(0, this.active - ROW_WINDOW);
    const last = Math.min(this.lyrics.length - 1, this.active + ROW_WINDOW);
    for (let index = first; index <= last; index++) {
      const distance = Math.abs(index - this.active);
      const fontSize = Math.max(28, this.width * FONT_RATIO * (distance === 0 ? 1 : .72));
      const alpha = [1, .58, .28, .12, .04][Math.min(distance, 4)];
      rows.push({
        ...this.base(`lyric-${index}`, "text", {
          x: panel.x + 26,
          y: centerY - rowHeight / 2 + index * rowGap,
          w: panel.w - 52,
          h: rowHeight,
        }),
        scroll: true,
        clipRect: { x: panel.x + 18, y: panel.y + 18, w: panel.w - 36, h: panel.h - 36 },
        text: {
          content: this.lyrics[index].text,
          color: [0.94, 0.985, 1, alpha],
          fontSizePx: fontSize,
          fontWeight: distance === 0 ? 700 : 600,
          align: "center",
          halo: distance === 0 ? "dark" : "none",
        },
      });
    }
    this.renderer.setElements(rows);
    this.renderer.setContentHeight(this.height + Math.max(0, this.lyrics.length - 1) * rowGap);
    this.renderer.setScrollY(this.scrollY);
  }

  private panel(): { x: number; y: number; w: number; h: number } {
    const w = Math.min(this.width * .72, 1300);
    const h = Math.min(this.height * .64, 620);
    return { x: (this.width - w) / 2, y: (this.height - h) / 2, w, h };
  }

  private rowGap(): number { return Math.min(154, Math.max(92, this.height * .142)); }

  private glassPanel(rect: { x: number; y: number; w: number; h: number }): GlassElementConfig {
    return {
      ...this.base("lyrics-glass-panel", "glass-shape", rect),
      cornerRadius: Math.min(this.settings.cornerRadius, rect.h * .45),
      refractionHeight: this.settings.refractionHeight,
      refractionAmount: this.settings.refractionAmount,
      depthEffect: this.settings.depthEffect,
      chromaticAberration: this.settings.chromaticAberration,
      blurRadius: this.settings.blurRadius,
      saturation: this.settings.saturation,
      brightness: this.settings.brightness,
      contrast: this.settings.contrast,
      surfaceColor: [...this.settings.surfaceColor, this.settings.surfaceAlpha],
      tintColor: [...this.settings.tintColor, this.settings.tintAlpha],
      highlight: this.settings.highlight ? { mode: this.settings.highlightMode, color: this.settings.highlightColor, angle: this.settings.highlightAngle, falloff: this.settings.highlightFalloff, alpha: this.settings.highlightAlpha, widthDp: this.settings.highlightWidth } : null,
      outerShadow: this.settings.shadow ? { radius: this.settings.shadowRadius, alpha: this.settings.shadowAlpha, offsetX: this.settings.shadowOffsetX, offsetY: this.settings.shadowOffsetY, color: this.settings.shadowColor } : null,
      independentBackdrop: true,
      directBackdropSample: this.settings.directBackdrop,
      useSeparableBlur: this.settings.separableBlur,
      useContinuousSdf: this.settings.continuousCorners,
      scroll: false,
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
