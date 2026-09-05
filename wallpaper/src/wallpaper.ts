/**
 * Wallpaper Engine 环境适配层。
 *
 * 官方约定：Web Wallpaper 定义全局 `wallpaperPropertyListener` 接收用户属性。
 * - applyUserProperties(properties)：用户改动属性时触发，且只包含"发生变化"的属性，
 *   值通过 `properties.<key>.value` 读取。
 * - applyGeneralProperties(properties)：全局属性（如 FPS 限制）。
 *
 * 本项目把"歌词字号 / 行距 / 同步偏移 / 水平偏移 / 垂直偏移 / 亮度 / 字体"
 * 接到这里，回调给渲染器与 CSS 变量。
 */
import { DEFAULT_FONT_FAMILY, DEFAULT_FONT_SIZE, DEFAULT_GAP } from "./defaults";
import { DEFAULT_LIQUID_SETTINGS, type LiquidSettings } from "./liquid/reference-wallpaper";

/** 可调节的歌词布局参数 */
export interface WallpaperSettings {
  /** 当前行（最大）字号，px */
  fontSize: number;
  /** 行距，px */
  lineGap: number;
  /** 歌词块垂直偏移（px），正值下移、负值上移 */
  offsetY: number;
  /** 歌词块水平偏移（px），正值右移、负值左移 */
  offsetX: number;
  /** 歌词同步偏移（毫秒），正数提前、负数延后 */
  syncOffset: number;
  /** 亮度（百分比，100 = 原始） */
  brightness: number;
  /** 字体（CSS font-family 值） */
  fontFamily: string;
  /** 是否显示壁纸内部的液态玻璃调参面板 */
  showControls: boolean;
  /** 正值让歌词提前显示，单位毫秒。 */
  lyricLeadMs: number;
  /** /query 的校准轮询频率，单位毫秒。 */
  pollIntervalMs: number;
  /** 液态玻璃渲染器的全部持久化设置。 */
  liquid: LiquidSettings;
}

/** 字体选项：combo 的 value → CSS font-family 值 */
const FONTS: Record<string, string> = {
  default: DEFAULT_FONT_FAMILY,
  yahei: '"Microsoft YaHei", "微软雅黑", sans-serif',
  simhei: '"SimHei", "黑体", sans-serif',
  simsun: '"SimSun", "宋体", serif',
  kaiti: '"KaiTi", "楷体", serif',
};

/** 默认值，与 renderer.ts / main.css 的默认保持一致 */
export const DEFAULT_SETTINGS: WallpaperSettings = {
  fontSize: DEFAULT_FONT_SIZE,
  lineGap: DEFAULT_GAP,
  offsetY: 0,
  offsetX: 0,
  syncOffset: 0,
  brightness: 100,
  fontFamily: DEFAULT_FONT_FAMILY,
  showControls: false,
  lyricLeadMs: 100,
  pollIntervalMs: 200,
  liquid: { ...DEFAULT_LIQUID_SETTINGS },
};

type SettingsListener = (settings: WallpaperSettings) => void;

export function setupWallpaperEnvironment(onChange: SettingsListener): void {
  const w = window as unknown as Record<string, unknown>;

  // Wallpaper Engine 的加载器约定：直接定义该全局对象
  if (w.wallpaperPropertyListener) return;

  // applyUserProperties 每次只传变化的属性，需在本地累积最新值
  const current: WallpaperSettings = { ...DEFAULT_SETTINGS, liquid: { ...DEFAULT_SETTINGS.liquid } };

  w.wallpaperPropertyListener = {
    applyUserProperties(properties: Record<string, unknown>): void {
      const fontSize = readPositiveNumber(propertyValue(properties.fontsize));
      const lineGap = readPositiveNumber(propertyValue(properties.linegap));
      const offsetY = readNumber(propertyValue(properties.offsety));
      const offsetX = readNumber(propertyValue(properties.offsetx));
      const syncOffset = readNumber(propertyValue(properties.syncoffset));
      const brightness = readPositiveNumber(propertyValue(properties.brightness));
      const font = propertyValue(properties.font);
      const showControls = propertyValue(properties.showcontrols);
      const lyricLeadMs = readNumber(propertyValue(properties.lyricleadms));
      const pollIntervalMs = readPositiveNumber(propertyValue(properties.pollintervalms));
      const backgroundImage = propertyValue(properties.backgroundimage);

      if (fontSize !== null) current.fontSize = fontSize;
      if (lineGap !== null) current.lineGap = lineGap;
      if (offsetY !== null) current.offsetY = offsetY;
      if (offsetX !== null) current.offsetX = offsetX;
      if (syncOffset !== null) current.syncOffset = syncOffset;
      if (brightness !== null) current.brightness = brightness;
      if (typeof font === "string" && font in FONTS) current.fontFamily = FONTS[font];
      const visible = readBoolean(showControls);
      if (visible !== null) current.showControls = visible;
      if (lyricLeadMs !== null) current.lyricLeadMs = lyricLeadMs;
      if (pollIntervalMs !== null) current.pollIntervalMs = pollIntervalMs;
      if (typeof backgroundImage === "string") current.liquid.backgroundImage = backgroundImage;

      for (const [property, key] of Object.entries(NUMBER_PROPERTIES)) {
        const value = readNumber(propertyValue(properties[property]));
        if (value !== null) (current.liquid as Record<string, unknown>)[key] = value;
      }
      for (const [property, key] of Object.entries(BOOLEAN_PROPERTIES)) {
        const value = readBoolean(propertyValue(properties[property]));
        if (value !== null) (current.liquid as Record<string, unknown>)[key] = value;
      }
      for (const [property, key] of Object.entries(COLOR_PROPERTIES)) {
        const value = readColor(propertyValue(properties[property]));
        if (value !== null) (current.liquid as Record<string, unknown>)[key] = value;
      }

      onChange({ ...current });
    },
    applyGeneralProperties(): void {
      // 预留：全局属性接入点（如 FPS 限制）
    },
  };
}

/** 属性值：官方约定为 `{ value }` 结构，这里兼容"直接传值"的历史形态 */
function propertyValue(prop: unknown): unknown {
  if (prop !== null && typeof prop === "object" && "value" in (prop as object)) {
    return (prop as { value: unknown }).value;
  }
  return prop;
}

function readNumber(value: unknown): number | null {
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

function readPositiveNumber(value: unknown): number | null {
  const n = readNumber(value);
  return n !== null && n > 0 ? n : null;
}

const NUMBER_PROPERTIES: Record<string, keyof LiquidSettings> = {
  backgroundlayout: "backgroundLayout", backgroundscale: "backgroundScale", backgroundoffsetx: "backgroundOffsetX", backgroundoffsety: "backgroundOffsetY",
  lyricfontscale: "lyricFontScale", lyricglasspadding: "lyricGlassPadding", lyricgap: "lyricGap",
  lyricverticaloffset: "lyricVerticalOffset", lyricscrollspeed: "lyricScrollSpeed", lyricoffsetx: "lyricOffsetX",
  lyricoffsety: "lyricOffsetY", lyricalignment: "lyricAlignment",
  lyricdepthminscale: "lyricDepthMinScale", lyricdepthscalefalloff: "lyricDepthScaleFalloff", lyricdepthscalecurve: "lyricDepthScaleCurve",
  lyricdepthalphafalloff: "lyricDepthAlphaFalloff", lyricdepthalphacurve: "lyricDepthAlphaCurve", lyricdepthglassfloor: "lyricDepthGlassFloor", lyricdepthculldistance: "lyricDepthCullDistance",
  corneradius: "cornerRadius", refractionheight: "refractionHeight", refractionamount: "refractionAmount", blurradius: "blurRadius",
  saturation: "saturation", liquidbrightness: "brightness", contrast: "contrast",
  tintalpha: "tintAlpha", surfacealpha: "surfaceAlpha",
  highlightmode: "highlightMode", highlightalpha: "highlightAlpha", highlightangle: "highlightAngle", highlightfalloff: "highlightFalloff", highlightwidth: "highlightWidth",
  shadowalpha: "shadowAlpha", shadowradius: "shadowRadius", shadowoffsetx: "shadowOffsetX", shadowoffsety: "shadowOffsetY",
  dpr: "dpr", blurtapcap: "blurTapCap", blurdownsample: "blurDownsample",
};

const BOOLEAN_PROPERTIES: Record<string, keyof LiquidSettings> = {
  deptheffect: "depthEffect", chromaticaberration: "chromaticAberration", highlight: "highlight", shadow: "shadow",
  lyricbehindglass: "lyricBehindGlass",
  separableblur: "separableBlur", continuouscorners: "continuousCorners", directbackdrop: "directBackdrop",
  kawaseblur: "kawaseBlur", blurcache: "blurCache", perelementfbo: "perElementFbo",
};

const COLOR_PROPERTIES: Record<string, keyof LiquidSettings> = {
  tintcolor: "tintColor", surfacecolor: "surfaceColor", highlightcolor: "highlightColor", shadowcolor: "shadowColor",
};

function readBoolean(value: unknown): boolean | null {
  if (typeof value === "boolean") return value;
  if (value === 1 || value === "1" || value === "true") return true;
  if (value === 0 || value === "0" || value === "false") return false;
  return null;
}

function readColor(value: unknown): [number, number, number] | null {
  if (Array.isArray(value) && value.length >= 3) {
    const channels = value.slice(0, 3).map(Number);
    return channels.every(Number.isFinite) ? [channels[0], channels[1], channels[2]] : null;
  }
  if (typeof value !== "string") return null;
  const channels = value.trim().split(/\s+/).slice(0, 3).map(Number);
  return channels.length === 3 && channels.every(Number.isFinite)
    ? [channels[0], channels[1], channels[2]]
    : null;
}
