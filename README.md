# 网易云音乐 / Apple Music 动态歌词壁纸

基于 [Wallpaper Engine](https://www.wallpaperengine.io/) 的网易云音乐动态歌词壁纸。桌面居中显示当前正在播放的一句歌词，随播放进度逐句滚动，字号最大、沉浸式居中。

项目由**两部分**组成，放在同一个仓库里：

| 目录 | 类型 | 作用 |
|------|------|------|
| `server/` | 本地服务端 (Python) | 读取网易云或 Apple Music 播放状态与歌词，提供 Now Playing API |
| `wallpaper/` | 壁纸前端源码 (Vite + TypeScript) | Wallpaper Engine 网页壁纸，订阅服务端 API 显示同步歌词 |

> 一眼看懂：`wallpaper/` 是壁纸源码，`server/` 是服务端，`docs/` 是文档。壁纸构建产物**不入库**，由 GitHub Actions 打 tag 时自动构建发布（见 [Releases](https://github.com/ningjx/lyric-wallpaper/releases)）。

## 架构

```text
网易云音乐 (cloudmusic.exe) / Apple Music
    │
    ├─ 网易云：进程内存读取 ── 进度 / 时长 / 播放状态（偏移自动探测）
    ├─ Apple Music：Windows SMTC ── 歌名 / 歌手 / 精确进度 / 时长
    └─ 网易云官方 API ─ 歌曲搜索 + 歌词（Apple Music 同样复用）

server/  (Python, aiohttp 异步, http://127.0.0.1:9863)
    ├─ GET /query       播放器 + 歌曲状态（兼容原服务）
    ├─ GET /api/lyric   当前歌曲歌词（兼容原服务，source 为命中 provider）
    ├─ GET /sse         状态/切歌实时推送（可选）
    └─ GET /healthz, /metrics   健康与指标诊断

壁纸前端  (Wallpaper Engine Web 壁纸)
    └─ 轮询 /query 校准进度（+ 可选接 /sse），切歌时请求 /api/lyric
```

## 功能特性

- **双平台读取**：网易云使用内存读取；Microsoft Store 版 Apple Music 使用 Windows SMTC 的精确时间线
- **偏移自动探测**：网易云升级或换电脑后，服务端首次启动自动重定位内存偏移并按版本缓存，无需手动维护
- **当前句居中、字号最大**：沉浸式歌词，当前行最大字号居中，上下行逐级缩小、降低透明度、加模糊
- **平滑进度**：前端用本地单调时钟驱动 rAF 渲染，API 只做低频校准，动画不依赖轮询频率
- **可调参数**：字号、行距、同步偏移、水平/垂直位置、亮度、字体，均可壁纸属性面板即时调节

## 快速开始

### 1. 启动服务端（必需）

`server/` 是 Python 包，需在**仓库根目录**用 `-m` 启动（勿在 `server/` 目录内运行，否则相对导入会失败）；在 `server/` 内也可用 `python run.py`：

```bash
pip install -r server/requirements.txt
python -m server        # 在仓库根目录
```

看到当前播放歌曲即表示已连接：网易云会显示 `✓ 服务就绪`，Apple Music 会显示 `Apple Music`。详见 [`server/README.md`](server/README.md)。

### 2. 导入壁纸

壁纸构建产物**不提交到仓库**，有两种获取方式：

- **下载已发布版本**：到 [Releases](https://github.com/ningjx/lyric-wallpaper/releases) 下载 `wallpaper-vX.Y.Z.zip`，解压后把文件夹拖入 Wallpaper Engine 的「创建壁纸」窗口，或复制到 `wallpaper_engine/projects/myprojects/`。
- **自行构建**（需 Node.js）：

  ```bash
  cd wallpaper
  npm install
  npm run build      # 产出 ../dist/，把 dist/ 拖入 Wallpaper Engine
  ```

导入后播放任意歌曲即可看到同步歌词。

请导入整个 `dist/` 文件夹；正式入口只有 `index.html`，不需要单独添加或打开其他 HTML 文件。

## 属性面板

在 Wallpaper Engine 里右键壁纸 → **自定义**即可修改并保存全部设置。最前方是歌词同步提前/延迟和 API 轮询频率，之后按歌词排版、光学设置、空间层次、高光、阴影、材质和渲染器排列。

每个分类都有“展开”开关；打开后只显示该类参数。Wallpaper Engine 会把这些用户属性写入壁纸预设，因此重启或切换壁纸后仍会保留。

## 目录结构

```text
lyric-wallpaper/
├── README.md            本文件
├── .gitignore
├── wallpaper/           壁纸前端源码（Vite + TypeScript）
│   ├── index.html           Vite 入口
│   ├── package.json         依赖与构建脚本
│   ├── vite.config.ts       构建配置（产物输出到 ../dist/）
│   ├── tsconfig.json        TypeScript 配置
│   ├── public/project.json  Wallpaper Engine 壁纸配置（属性面板定义）
│   └── src/                 前端源码
│       ├── main.ts          入口：组装模块、双循环
│       ├── wallpaper.ts     Wallpaper Engine 属性面板适配
│       ├── scene.ts         场景层淡入淡出
│       ├── api/             Now Playing API 适配（端点/类型/HTTP）
│       ├── player/          状态机 + 本地时钟
│       ├── lyrics/          LRC 解析 + 时间轴 + 渲染器
│       └── styles/          布局与动画
├── server/              服务端（Python，aiohttp 异步包）
│   ├── README.md
│   ├── __main__.py / server.py   入口与生命周期（`python -m server`）
│   ├── config.py                 配置（config.json 可选覆盖）
│   ├── core/                     状态存储/仲裁/负载构造/指标
│   ├── sources/                  数据源抽象 + 网易云内存 + Apple SMTC
│   ├── lyrics/                   可插拔歌词体系（搜索/Provider 链/缓存）
│   ├── web/                      aiohttp 路由（/query /api/lyric /sse /healthz /metrics）
│   ├── probing/                  偏移自动探测 + 版本缓存
│   ├── requirements*.txt         依赖拆分（core / apple 可选 / dev）
│   └── tools/                  21 个历史开发/验证脚本（研究记录，非运行必需）
└── docs/                方案文档 + API 示例
    ├── wallpaper_engine_now_playing_歌词动态壁纸方案.md
    └── 获取歌词示例.txt
```

## 发布新版本

推送一个 `v` 开头的 tag 即可触发 GitHub Actions 自动构建并发布：

```bash
git tag v1.0.0
git push origin v1.0.0
```

发布完成后，[Releases](https://github.com/ningjx/lyric-wallpaper/releases) 会生成两个压缩包：

| 文件 | 内容 | 用途 |
|------|------|------|
| `wallpaper-v1.0.0.zip` | 壁纸构建产物（`index.html` / `project.json` / `assets/`） | 解压后导入 Wallpaper Engine |
| `server-v1.0.0.zip` | 服务端（Python 源码） | 解压后 `pip install -r requirements.txt` 并运行 |

工作流定义见 [`.github/workflows/release.yml`](.github/workflows/release.yml)。

## 服务端工作原理

见 [`server/README.md`](server/README.md)。要点：

- **播放状态**：网易云读取 `cloudmusic.dll` 内存；Apple Music 读取 Windows SMTC 的媒体会话及时间线
- **歌名歌手**：枚举网易云窗口标题 `歌名 - 歌手`（含最小化到托盘的隐藏窗口）
- **歌词**：网易云官方 API（`music.163.com/api/song/lyric`）
- **偏移自动探测**：三个字段的地址随版本变化，`offset_probe.py` 用「3 秒内进度 +3 秒」的启发式自动定位，并按版本号（exe 文件版本）缓存到 `offsets_config.json`

## 已知限制

- 服务端**仅支持 Windows**（读取网易云进程内存依赖 pymem / pywin32）
- 首次在新版本网易云上运行时，需**正在播放一首歌**才能自动定位偏移
- VIP 加密歌曲、本地歌曲的歌词可能为空（属网易云服务端限制）
- 多开网易云时服务端只读取第一个进程
- 壁纸请求 `http://127.0.0.1:9863`，若服务端改了端口需同步修改 `wallpaper/src/api/config.ts`
