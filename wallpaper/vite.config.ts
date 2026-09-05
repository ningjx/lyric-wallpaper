import { defineConfig } from "vite";

// base: "./" —— 构建产物用相对路径引用资源，
// 保证 Wallpaper Engine 通过 file:// 加载时资源能找到。
export default defineConfig({
  base: "./",
  build: {
    // Wallpaper Engine 内置 CEF 版本较旧，降低 target 保证兼容
    target: "chrome90",
    // 产物输出到仓库根 dist/（vite 根在 wallpaper/，故用 ../dist；
    // 输出目录在根之外，需显式 emptyOutDir 才能清空旧产物）
    outDir: "../dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    rollupOptions: {
      input: { wallpaper: "index.html", glass: "glass.html" },
    },
    minify: "esbuild",
    sourcemap: false,
  },
});
