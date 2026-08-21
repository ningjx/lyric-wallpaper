/**
 * Wallpaper Engine 环境适配层。
 *
 * 官方要求：Web Wallpaper 可定义全局 `wallpaperPropertyListener` 来接收
 * 用户属性（applyUserProperties）与全局属性如 FPS（applyGeneralProperties）。
 * 本项目第一阶段只做歌词，暂无用户属性，但保留监听器骨架，
 * 后续加设置项（字号/位置/亮度等）时在此扩展。
 */

interface WallpaperProperties {
  [key: string]: unknown;
}

export function setupWallpaperEnvironment(): void {
  const w = window as unknown as Record<string, unknown>;

  // Wallpaper Engine 的 Web Wallpaper 加载器约定：直接定义该全局对象
  if (w.wallpaperPropertyListener) return;

  w.wallpaperPropertyListener = {
    applyUserProperties(properties: WallpaperProperties): void {
      // 预留：用户属性接入点（如歌词字号、位置）
      void properties;
    },
    applyGeneralProperties(properties: WallpaperProperties): void {
      // 预留：全局属性接入点（如 FPS 限制）
      void properties;
    },
  };
}
