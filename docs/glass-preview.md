# 液态歌词预览

`wallpaper/glass.html` 是独立的 GPU 视觉原型，内置 mock 歌词，不需要音乐客户端或 Python 服务。它用于确认液态玻璃的光效方向，尚未接回正式的 Now Playing 数据流。

在 `wallpaper/` 中运行：

```powershell
npm run dev -- --host 127.0.0.1 --port 5173
```

然后打开 <http://127.0.0.1:5173/glass.html>。`npm run build` 会同步产出 `dist/glass.html`。

## 渲染路径

- `wallpaper/vendor/liquid-glass-webgl` 是受版本控制的 Apache-2.0 Git 子模块。预览实际创建并调用其中的 `LiquidGlassRenderer`，而不是重复实现一套相似 shader。
- 整个画面由该项目的 WebGL 1 Canvas 绘制，使用 GLSL ES 1.00，作为 Wallpaper Engine CEF 的兼容基线；没有依赖 WebGL2 或 WebGPU。
- 背景纹理先进入 renderer 的离屏 framebuffer。每句歌词都有独立的 `glass-shape` 背板，使用上游的连续曲率 SDF、局部 FBO、折射、可分离模糊、高光和阴影 pass。
- 每句歌词和其玻璃背板共用同一套滚动坐标，按播放进度由临界阻尼弹簧一起滚动；主循环使用浏览器 `requestAnimationFrame`，以显示器刷新率驱动渲染。仅提交当前行前后四行，避免长歌词将屏幕外内容带入渲染循环。
- 背景图位于 `wallpaper/public/backgrounds/wallhaven-vpolwm.jpg`；玻璃背板折射的是 renderer 中加载的真实城市夜景纹理，不是在页面上覆盖一层 CSS 图片。
- 渲染像素比上限为 1.5，优先保障大屏歌词阅读和 Wallpaper Engine 的帧率。
- WebGL 初始化或着色器编译失败时，页面自动降级为 DOM 文字滚动，歌词仍可见。

`?still=1` 可冻结播放时间用于截图；`?time=21` 可指定 mock 歌词开始时间。

字体使用 [寒蝉全圆体 ChillRoundF](https://github.com/Warren2060/ChillRound)，本地文件为 `wallpaper/public/fonts/ChillRoundF.ttf`，授权文件为同目录的 `OFL-ChillRound.txt`。

本实现直接复用 [martin65536/liquid-glass-webgl](https://github.com/martin65536/liquid-glass-webgl) 的 renderer，并在 `THIRD_PARTY_NOTICES.md` 保留 Apache-2.0 来源与本地适配说明。
