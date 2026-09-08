# 动态歌词壁纸

面向 [Wallpaper Engine](https://www.wallpaperengine.io/) 的本地动态歌词壁纸：Python 服务读取网易云音乐或 Microsoft Store 版 Apple Music 的播放状态与歌词，WebGL 壁纸将歌词、玻璃折射和随机背景合成为桌面效果。

![演示图片](https://raw.githubusercontent.com/ningjx/lyric-wallpaper/refs/heads/main/docs/Snipaste_2026-09-06_11-00-27.png)

## 当前能力

- 网易云音乐：进程内存读取播放时间、时长和状态；首次适配新版本时自动探测偏移。
- Apple Music：通过 Windows SMTC 读取曲目和时间线。
- 歌词：按歌曲异步解析、缓存；无歌词时壁纸继续显示，只隐藏歌词。
- 歌词滚动：本地时钟平滑推进，主歌词居中；切歌时旧歌词不会清屏，新歌信息行和歌词从视窗下方依次追赶进入。
- 单曲循环：下一轮歌词接在当前队列末尾继续滚动，不回跳到开头。
- 液态玻璃：折射、高光、材质、景深、阴影、普通/高质量模糊，以及歌词置于玻璃背后模式。
- 背景：内置背景或随机目录轮播；支持裁切填充、完整显示、拉伸、平铺、缩放和偏移。
- 性能：静止时 renderer 跳过无效绘制；背景切换直接上传 Canvas 纹理，避免 PNG 编解码中转。

## 架构

```text
网易云音乐 / Apple Music
        │
        ▼
server/  ── http://127.0.0.1:9863 ── /query、/api/lyric
        │
        ▼
wallpaper/（Wallpaper Engine Web 壁纸）
  本地时钟 + 歌词视觉队列 + WebGL 液态玻璃渲染器
```

| 目录 | 用途 |
|---|---|
| `server/` | Windows 本地播放状态和歌词服务；详见 [server/README.md](server/README.md) |
| `wallpaper/` | Vite + TypeScript 壁纸源码 |
| `wallpaper/vendor/liquid-glass-webgl` | 液态玻璃 renderer 子模块（维护 fork） |
| `docs/` | 性能、架构与失败实验记录 |

## 快速开始

### 1. 启动本地服务

在仓库根目录执行：

```powershell
pip install -r server/requirements.txt
python -m server
```

默认监听 `127.0.0.1:9863`。如果提示端口已被占用，通常说明已有服务实例在运行，无需重复启动。

### 2. 获取或构建壁纸

- 推荐：从 [Releases](https://github.com/ningjx/lyric-wallpaper/releases) 下载 `wallpaper-vX.Y.Z.zip`，解压后导入 Wallpaper Engine。
- 源码构建：

  ```powershell
  cd wallpaper
  npm install
  npm run build
  ```

构建产物在仓库根目录 `dist/`；将整个目录导入 Wallpaper Engine。

## Wallpaper Engine 属性

在 Wallpaper Engine 中右键壁纸 → **自定义**。属性面板使用原生折叠分组：

- 基础：歌词提前量、API 轮询频率。
- 背景与轮播：目录、切换间隔、布局、缩放、位置。
- 歌词排版与空间层次：字号、行距、位置、深度缩放/淡出、渲染距离。
- 玻璃外观、材质、高光、阴影、渲染器（高级）。

高光和阴影的细节仅在各自启用后出现。高质量全场模糊仅切换模糊算法，实际是否模糊始终由“背景模糊”半径决定；半径为 `0` 时保持清晰。

“渲染距离（行）”为当前歌词前后各侧的离散渲染窗口，数值越大可提前看到更多歌词，也会增加渲染负载。

## 开发与验证

```powershell
cd wallpaper
npm test -- --run
npm run build
```

需要观察渲染数据时，在壁纸开发者工具中执行：

```js
window.lyricWallpaperPerformance.setEnabled(true)
window.lyricWallpaperPerformance.snapshot()
```

## 发布

推送 `v` 开头的标签会触发 GitHub Actions，构建并创建 Release：

```powershell
git tag -a v0.0.4 -m "v0.0.4"
git push origin v0.0.4
```

发布工作流会初始化 `wallpaper/vendor/liquid-glass-webgl` 子模块；该子模块指向本账号维护的 fork，修改 renderer 时请先确保对应提交已推送到 fork。

## 限制与排错

- 服务端仅支持 Windows。
- 网易云首次偏移探测需要正在播放歌曲；多开时只读取一个进程。
- VIP 加密或本地歌曲可能没有可用歌词。
- 歌词缓存位于 `data/lyrics/`，属于运行时数据，不应提交。
- 壁纸始终请求 `127.0.0.1:9863`；修改服务端端口时需同步修改前端 API 配置。

服务端端点、配置和诊断方式请阅读 [server/README.md](server/README.md)。

## 鸣谢

- [Widdit/now-playing-service](https://github.com/Widdit/now-playing-service)：歌曲播放状态检测、歌词服务设计的参考来源；其代码以 MIT License 发布。
- [martin65536/liquid-glass-webgl](https://github.com/martin65536/liquid-glass-webgl)：本项目液态玻璃 WebGL 渲染器的上游实现；其代码以 Apache License 2.0 发布。本项目使用并维护其 fork 中的适配版本。

本项目以 [Apache License 2.0](LICENSE) 发布；上游项目的版权与许可证声明仍分别适用于其对应代码。
