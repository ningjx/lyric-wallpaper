# 动态歌词壁纸

> 本项目由 AI 驱动开发，并基于以下上游开源项目进行设计、适配与实现：
>
> - [Widdit/now-playing-service](https://github.com/Widdit/now-playing-service)：Now Playing API、歌曲播放状态检测与歌词服务设计的参考来源（MIT License）。
> - [martin65536/liquid-glass-webgl](https://github.com/martin65536/liquid-glass-webgl)：液态玻璃 WebGL 渲染器的上游实现（Apache License 2.0）；本项目使用并维护其 fork 中的适配版本。
>
> 本项目以 [Apache License 2.0](LICENSE) 发布；上游项目的版权与许可证声明仍分别适用于其对应代码。

面向 [Wallpaper Engine](https://www.wallpaperengine.io/) 的本地动态歌词壁纸：WebGL 壁纸读取本机 Now Playing API 的播放状态与歌词，将歌词、玻璃折射和随机背景合成为桌面效果。仓库自带的 Python 服务可读取网易云音乐或 Microsoft Store 版 Apple Music；也可直接使用 [Widdit/now-playing-service](https://github.com/Widdit/now-playing-service)。

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
[本仓库 server] 或 [Now Playing Service]
                    │
                    ▼
      http://127.0.0.1:9863  ── /query、/api/lyric
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

## 服务选择与定位

壁纸可接入本仓库的 `server/`，也兼容 [Widdit/now-playing-service](https://github.com/Widdit/now-playing-service) 的 `/query`、`/api/lyric` 接口。两者定位不同：

| 对比项 | 本仓库 server | Now Playing Service |
|---|---|---|
| 设计目标 | 专门为本歌词壁纸提供稳定、低延迟的数据源 | 通用“正在播放”展示工具，可服务直播、桌面组件和自定义前端 |
| 支持播放器 | 目前聚焦网易云音乐与 Microsoft Store 版 Apple Music | 支持 20+ 音乐平台及多类本地播放器 |
| API 能力 | 保持壁纸所需的 `/query`、`/api/lyric`；并提供 `/sse`、`/healthz`、`/metrics` | 提供更丰富的歌曲信息、展示组件和通用 API 能力，覆盖面明显更广 |
| 播放进度 | 网易云使用进程内存读取，并对版本变化自动探测偏移；进度以毫秒级精度供壁纸本地时钟校准，减少最小化、桌面歌词等场景的漂移 | 以广泛兼容为优先，采用通用的窗口标题、SMTC、UI Automation 等识别方式 |
| 状态推送 | 支持 SSE：切歌、暂停和歌词解析完成可即时通知，壁纸只需低频轮询校准连续进度 | 以其自身的通用服务能力为主；本壁纸不依赖 SSE，缺失时自动回退到轮询 |

因此：想要更多播放器和通用 API 能力时，直接使用 Now Playing Service；主要使用网易云音乐或 Microsoft Store 版 Apple Music，且希望歌词壁纸获得更精确、稳定的播放时间与更顺滑的切歌响应时，使用本仓库的 `server/`。本仓库服务不是通用播放器聚合器，而是为本壁纸优化的数据源。

## 快速开始

### 1. 选择并启动播放状态服务

壁纸兼容 Now Playing API 的 `GET /query` 与 `GET /api/lyric`，默认访问 `http://127.0.0.1:9863`。两种服务任选其一即可，**不需要同时运行**：

- **本仓库服务（推荐用于网易云音乐）**：使用进程内存读取进度，适合最小化或桌面歌词模式下仍需准确同步的场景。
- **[Widdit/now-playing-service](https://github.com/Widdit/now-playing-service)**：可直接安装、启动并作为壁纸的数据源；壁纸会自动降级为轮询模式，核心播放状态和歌词功能不受影响。

#### 使用本仓库服务

在仓库根目录执行：

```powershell
pip install -r server/requirements.txt
python -m server
```

默认监听 `127.0.0.1:9863`。如果提示端口已被占用，通常说明已有服务实例在运行，无需重复启动。

#### 使用 Now Playing Service

安装并启动上游项目后，只要它的本地 API 保持默认的 `127.0.0.1:9863`，即可直接导入并使用本壁纸，无需安装或启动本仓库的 `server/`。如果你修改了上游服务端口，需要同步修改壁纸源码中的 `wallpaper/src/api/config.ts` 后重新构建。

### 2. 获取或构建壁纸

- 推荐：从 [Releases](https://github.com/ningjx/lyric-wallpaper/releases) 下载 `wallpaper-vX.Y.Z.zip`，解压后导入 Wallpaper Engine。
- 源码构建：

  ```powershell
  cd wallpaper
  npm install
  npm run build
  ```

构建产物在仓库根目录 `dist/`；将整个目录导入 Wallpaper Engine。
