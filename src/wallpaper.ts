/**
 * Wallpaper Engine 环境适配层。
 *
 * 官方约定：Web Wallpaper 定义全局 `wallpaperPropertyListener` 接收用户属性。
 * - applyUserProperties(properties)：用户改动属性时触发，且只包含"发生变化"的属性，
 *   值通过 `properties.<key>.value` 读取。
 * - applyGeneralProperties(properties)：全局属性（如 FPS 限制）。
 *
 * 本项目把"歌词字号 / 行距 / 垂直位置 / 亮度 / 字体"接到这里，回调给渲染器与 CSS 变量。
 */

/** 可调节的歌词布局参数 */
export interface WallpaperSettings {
  /** 当前行（最大）字号，px */
  fontSize: number;
  /** 行距，px */
  lineGap: number;
  /** 歌词块垂直偏移（px），正值下移、负值上移 */
  offsetY: number;
  /** 亮度（百分比，100 = 原始） */
  brightness: number;
  /** 字体（CSS font-family 值） */
  fontFamily: string;
}

/** 默认字体栈，与 main.css :root 的 --font-family 一致 */
const DEFAULT_FONT_FAMILY =
  '"Microsoft YaHei", "PingFang SC", "Segoe UI", system-ui, sans-serif';

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
  fontSize: 72,
  lineGap: 130,
  offsetY: 0,
  brightness: 100,
  fontFamily: DEFAULT_FONT_FAMILY,
};

type SettingsListener = (settings: WallpaperSettings) => void;

export function setupWallpaperEnvironment(onChange: SettingsListener): void {
  const w = window as unknown as Record<string, unknown>;

  // Wallpaper Engine 的加载器约定：直接定义该全局对象
  if (w.wallpaperPropertyListener) return;

  // applyUserProperties 每次只传变化的属性，需在本地累积最新值
  const current: WallpaperSettings = { ...DEFAULT_SETTINGS };

  w.wallpaperPropertyListener = {
    applyUserProperties(properties: Record<string, unknown>): void {
      const fontSize = readPositiveNumber(propertyValue(properties.fontsize));
      const lineGap = readPositiveNumber(propertyValue(properties.linegap));
      const offsetY = readNumber(propertyValue(properties.offsety));
      const brightness = readPositiveNumber(propertyValue(properties.brightness));
      const font = propertyValue(properties.font);

      if (fontSize !== null) current.fontSize = fontSize;
      if (lineGap !== null) current.lineGap = lineGap;
      if (offsetY !== null) current.offsetY = offsetY;
      if (brightness !== null) current.brightness = brightness;
      if (typeof font === "string" && font in FONTS) current.fontFamily = FONTS[font];

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
