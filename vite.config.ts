import { defineConfig } from "vite";

// base: "./" —— 构建产物用相对路径引用资源，
// 保证 Wallpaper Engine 通过 file:// 加载时资源能找到。
export default defineConfig({
  base: "./",
  build: {
    // Wallpaper Engine 内置 CEF 版本较旧，降低 target 保证兼容
    target: "chrome90",
    outDir: "dist",
    assetsInlineLimit: 0,
    minify: "esbuild",
    sourcemap: false,
  },
});
