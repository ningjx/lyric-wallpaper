/**
 * 全局默认值单一来源。
 * renderer.ts / wallpaper.ts 的默认值都从这里取；
 * main.css :root 里的引导值只是 JS 执行前首帧的兜底，也须与此保持一致。
 * 改默认字号 / 行距 / 字体，只动这里。
 */

/** 当前行（最大）字号（px），可在 Wallpaper Engine 属性面板调节 */
export const DEFAULT_FONT_SIZE = 72;

/** 行距（px），可在 Wallpaper Engine 属性面板调节 */
export const DEFAULT_GAP = 130;

/** 默认字体栈，与 main.css :root 的 --font-family 一致 */
export const DEFAULT_FONT_FAMILY =
  '"Microsoft YaHei", "PingFang SC", "Segoe UI", system-ui, sans-serif';
