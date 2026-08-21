# Music Lyrics Wallpaper

Wallpaper Engine 动态歌词壁纸（Web Wallpaper）。读取本机 **Now Playing Service** 的播放进度与同步歌词，以"当前句居中、字号最大"的沉浸式歌词为唯一视觉主体。

**播放/停止行为**：播放歌曲时，歌词场景层淡入显示（0.55s）；停止播放时淡出隐藏（0.9s），露出底层（Wallpaper Engine 中的原壁纸）。暂停视为"在播放"，保留歌词停在当前句。

## 架构

```text
网易云音乐 → Now Playing Service(本地) → /query + /api/lyric → 本壁纸
```

```text
src/
├── main.ts                 # 入口：组装各模块，双循环（低频轮询校准 + 高频 rAF 渲染）
├── scene.ts                # 场景层显隐：播放淡入、停止淡出（CSS transition）
├── wallpaper.ts            # Wallpaper Engine 环境适配（wallpaperPropertyListener 骨架）
├── api/
│   ├── config.ts           # API 端点集中配置（改路径只动这里）
│   ├── types.ts            # Now Playing API 响应类型
│   └── nowPlaying.ts       # HTTP 适配器
├── player/
│   ├── SyncClock.ts        # 本地时钟：播放推进/暂停停住/平滑校准
│   └── MusicState.ts       # 状态机：轮询、歌曲变化检测、歌词拉取
├── lyrics/
│   ├── parser.ts           # LRC 解析（跳过网易云 JSON 元数据行）+ 翻译合并
│   ├── timeline.ts         # 播放时间 → 当前行索引（二分）
│   └── renderer.ts         # 窗口行池渲染：当前句居中、字号最大、平滑滚动
└── styles/
    └── main.css            # 布局 + 歌词动画过渡 + 离线遮罩
```

## 核心设计

**双循环解耦**（动画不依赖 API 轮询）：
- API：200ms 轮询 `/query`，只做"校准"
- 渲染：rAF 驱动 `SyncClock.now()`，本地单调时钟平滑推进
- 暂停 → 时钟停住；恢复 → 继续；拖进度条大跳变 → 直接跳转

**渲染器**：固定 7 行 DOM 池，每个元素绑定一个绝对歌词行索引；当前行索引变化时整组平移一格（CSS transition 平滑滚动），滑出窗口的行回收后从另一侧滑入。当前行 `font-size: 56px` 居中，上下行逐级缩小（40/31/26px）并降低透明度、加模糊。

## 构建与使用

```bash
npm install
npm run dev        # 开发调试（浏览器打开 http://localhost:5173）
npm run build      # 产出 dist/
```

**导入 Wallpaper Engine**：将 `dist/` 文件夹（含 `index.html`、`assets/`、`project.json`）拖入 Wallpaper Engine 编辑器的 Create Wallpaper，或复制到 `wallpaper_engine\projects\myprojects\`。资源全部本地打包，`base: "./"` 保证 `file://` 加载。

**使用前提**：本机运行 Now Playing Service（端口 9863），并播放网易云音乐。

### 安装到 Wallpaper Engine（另一台机器）

**方式一：直接导入 dist（最简单，无需 Node.js）**

仓库中已包含构建产物 `dist/`。在目标机器上：

1. 克隆或下载本仓库（GitHub 页面 → Code → Download ZIP）
2. 解压后进入 `dist/` 文件夹，确认里面有 `index.html`、`assets/`、`project.json`
3. 打开 Wallpaper Engine → 右下角 **创建壁纸（Create Wallpaper）** → 把 `dist/index.html` 拖入弹出的窗口
4. 壁纸出现在"已安装"标签 → 点击启用
5. 在目标机器上安装并启动 **Now Playing Service**（端口 9863），播放网易云音乐即可

**方式二：自己构建（需 Node.js）**

```bash
npm install
npm run build      # 产出 dist/，之后同上导入
```

> 提示：如果 `dist/` 与源码不同步（比如改过代码没重新构建），请重新 `npm run build` 后再导入。

## 已验证（自动化测试）

- 真实 API + `file://` 加载：资源路径正确、CORS 无错误、歌词渲染、`wallpaperPropertyListener` 就位
- 当前句居中（offset=0）且字号最大（56px）
- 播放推进 → 歌词行逐句平滑滚动
- 暂停 → 时钟停住不漂移
- 拖进度条 → 歌词直接跳到正确位置
- 无歌曲 / API 离线 → 显示 fallback 或离线遮罩，不白屏

## API 端点（本机实测，见根目录方案文档）

| 端点 | 内容 |
|---|---|
| `GET /query` | `player.{hasSong,isPaused,seekbarCurrentPosition}` + `track.{id,title,author,...}` |
| `GET /api/lyric` | `lrc`(LRC文本) + `translatedLyric` + `karaokeLyric` |

CORS 全开放（含 `Origin: null`），Wallpaper Engine 可直接 fetch。

## 下一步

- [ ] `wallpaperPropertyListener` 用户属性：歌词字号 / 位置 / 亮度（在 `wallpaper.ts` + `project.json` 的 `general.properties` 扩展）
- [ ] 16:9 / 21:9 / 4K 分辨率适配验证
- [ ] 多显示器 / Wallpaper Engine FPS 限制适配（`applyGeneralProperties`）
