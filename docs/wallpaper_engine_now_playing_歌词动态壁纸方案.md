# Wallpaper Engine + Now Playing Service 动态歌词壁纸方案

## 1. 项目定位

本项目只开发一个 Wallpaper Engine Web Wallpaper，不开发独立桌面客户端、不开发新的后端、不开发音乐检测模块。

数据源直接使用 Widdit/now-playing-service 已提供的本地 HTTP API：

```text
网易云音乐
    ↓
Now Playing Service
    ↓
本地 HTTP API
    ↓
Wallpaper Engine Web Wallpaper
    ↓
歌词时间轴 + 动态背景 + 特效
```

目标是制作一个“音乐播放时自动变成动态歌词壁纸”的 Web Wallpaper：

- 自动读取当前歌曲
- 自动读取播放/暂停状态
- 自动读取播放进度
- 自动读取歌曲封面、歌名、歌手等信息
- 自动读取同步歌词
- 歌曲切换时自动切换封面和歌词
- 拖动网易云进度条后，壁纸歌词同步跳转
- 使用 CSS/Canvas/WebGL 制作平滑歌词动画和背景特效

---

## 2. 可行性结论

### 2.1 Wallpaper Engine 可以使用 Web 技术制作壁纸

Wallpaper Engine 官方提供 Web Wallpaper 类型，使用 HTML、CSS、JavaScript 编写，并在内置 Chromium Embedded Framework（CEF）环境中运行。

官方文档：

- Web Wallpaper 总览：https://docs.wallpaperengine.io/web/overview.html
- 创建 Web Wallpaper：https://docs.wallpaperengine.io/en/web/first/gettingstarted.html
- Web Wallpaper 调试：https://docs.wallpaperengine.io/en/web/debug/debug.html

因此，本项目不需要原生 C++/C# Wallpaper 插件，直接使用前端项目即可。

### 2.2 可以从 Web Wallpaper 请求本机 Now Playing API，但必须验证 CORS

本项目的请求目标不是公网 API，而是 Now Playing Service 在本机提供的 HTTP API，例如：

```text
http://127.0.0.1:<now-playing-port>/...
```

Wallpaper Engine 的 Web Wallpaper 本质上运行在 Chromium/CEF 页面中，因此可以使用标准浏览器的 `fetch()` / XHR 发起 HTTP 请求。

真正的限制点是浏览器同源策略（CORS），而不是 Wallpaper Engine “禁止访问 API”。

因此实施时必须满足：

1. Now Playing Service 的本地 HTTP 服务能够被 `127.0.0.1`/`localhost` 访问。
2. 其 API 响应允许来自 Wallpaper Engine 页面来源的跨域请求；如果当前版本没有正确的 CORS 响应头，则需要在 Now Playing Service 中开启对应跨域能力或改为使用它提供的页面部署/同源方案。
3. 第一阶段不修改 Now Playing Service，先直接验证 `fetch()` 是否能正常拿到 JSON。

**验收依据不是“理论上能不能”，而是第一阶段必须实际在 Wallpaper Engine CEF 中请求成功。**

建议测试：

```js
fetch('http://127.0.0.1:<PORT>/...')
  .then(r => r.json())
  .then(data => console.log(data))
  .catch(err => console.error(err));
```

注意：Now Playing 当前 README 明确说明提供 API，供开发者自行设计前端页面，并通过软件内置服务器进行本地部署；具体接口页面在软件内的 API 页面查看。因此本项目代码不要把未经核实的 endpoint 路径写死，应该将 API 路径集中到一个配置/适配文件中，并以当前安装版本的 API 页面为准。

参考：

- Now Playing Service：https://github.com/Widdit/now-playing-service
- Now Playing Releases：https://github.com/Widdit/now-playing-service/releases

---

## 3. 为什么这个方案成立

Now Playing Service 已经负责最麻烦的一部分：音乐软件状态检测。

官方项目说明已经包含：

- 歌曲名称
- 歌手
- 专辑
- 封面
- 总时长
- 当前播放进度
- Playing / Paused 状态
- 歌词
- 本地 API

它支持网易云音乐等多个平台；v2.1.5 进一步增加了网易云音乐进度条同步，因此网易云拖动进度条可以实时同步歌曲/歌词进度。

因此我们的 Wallpaper 只需要做“展示层”。

---

## 4. 最终技术架构

```text
┌─────────────────────────────┐
│       网易云音乐 Windows     │
└──────────────┬──────────────┘
               │
               │ 播放状态 / 歌曲 / 进度
               ▼
┌─────────────────────────────┐
│      now-playing-service     │
│                             │
│  音乐检测                    │
│  歌曲匹配                    │
│  歌词匹配                    │
│  进度同步                    │
│  Local HTTP API             │
└──────────────┬──────────────┘
               │
               │ HTTP / JSON
               ▼
┌──────────────────────────────────────────┐
│             Wallpaper Engine             │
│              Web Wallpaper               │
│                                          │
│  ┌────────────────────────────────────┐  │
│  │ API Adapter                        │  │
│  │ - 当前歌曲                         │  │
│  │ - 播放状态                         │  │
│  │ - 进度                             │  │
│  │ - 歌词                             │  │
│  │ - 封面                             │  │
│  └──────────────────┬─────────────────┘  │
│                     │                    │
│  ┌──────────────────▼─────────────────┐  │
│  │ Music Timeline                     │  │
│  │ 本地时钟推进歌词，不依赖每帧 API     │  │
│  └──────────────────┬─────────────────┘  │
│                     │                    │
│  ┌──────────────────▼─────────────────┐  │
│  │ Visual Renderer                    │  │
│  │                                    │  │
│  │ Album Cover / Background / Lyrics  │  │
│  │ Glow / Particle / Transition       │  │
│  └────────────────────────────────────┘  │
└──────────────────────────────────────────┘
```

---

## 5. 项目边界

### 本项目需要开发

```text
Wallpaper Engine Web Wallpaper
├── HTML
├── CSS
├── JavaScript / TypeScript
├── Now Playing API Adapter
├── 歌词解析与时间轴
├── 当前歌词定位
├── 歌曲切换动画
├── 动态背景
├── 专辑封面
└── 基础设置项
```

### 本项目明确不开发

```text
× 不开发网易云音乐检测
× 不开发音乐播放器
× 不开发独立 Windows 服务
× 不开发新的 HTTP Server
× 不开发独立客户端 GUI
× 不实现音乐平台抓取
× 不维护歌词数据源
```

---

## 6. 推荐项目结构

第一版不需要复杂框架，推荐 Vite + TypeScript，最后构建成静态文件后导入 Wallpaper Engine。

```text
music-wallpaper/
│
├── index.html
├── package.json
├── tsconfig.json
├── vite.config.ts
│
├── src/
│   ├── main.ts
│   │
│   ├── api/
│   │   ├── config.ts
│   │   ├── nowPlaying.ts
│   │   └── types.ts
│   │
│   ├── player/
│   │   ├── MusicState.ts
│   │   ├── Timeline.ts
│   │   └── SyncClock.ts
│   │
│   ├── lyrics/
│   │   ├── parser.ts
│   │   ├── timeline.ts
│   │   └── renderer.ts
│   │
│   ├── visual/
│   │   ├── background.ts
│   │   ├── particles.ts
│   │   ├── albumColor.ts
│   │   └── transition.ts
│   │
│   └── styles/
│       ├── main.css
│       ├── lyrics.css
│       └── effects.css
│
└── public/
    └── fallback-cover.jpg
```

构建后把 `dist/` 内容作为 Wallpaper Engine Web Wallpaper 的项目内容。

---

## 7. API Adapter 设计

不要让整个项目直接依赖 Now Playing 的具体接口路径。

统一通过一个适配器获取数据：

```ts
export interface MusicState {
  status: 'playing' | 'paused' | 'stopped';

  track: {
    id: string;
    title: string;
    artist: string;
    album: string;
    cover: string;
    duration: number;
  };

  progress: number;

  lyrics: LyricLine[];
}

export interface LyricLine {
  start: number;
  end?: number;
  text: string;
}
```

然后统一：

```ts
class NowPlayingApi {
  async getMusicState(): Promise<MusicState> {
    // 这里根据当前 Now Playing 版本的 API 页面实现
  }
}
```

### API 地址配置

建议集中配置：

```ts
export const API_CONFIG = {
  baseUrl: 'http://127.0.0.1:<PORT>',
  stateEndpoint: '<按当前版本 API 页面填写>',
  lyricsEndpoint: '<按当前版本 API 页面填写>',
};
```

这样以后 Now Playing API 路径变化，只需要修改一处。

---

## 8. 数据获取策略

不要让 Wallpaper Engine 每一帧请求 API。

推荐：

```text
API 请求：1 秒 1 次左右
       ↓
发现歌曲变化 / 状态变化 / 进度突变
       ↓
重新校准本地时间轴
       ↓
requestAnimationFrame() 负责视觉动画
```

原因：

```text
API 数据频率：低频
动画渲染频率：高频
```

两者必须解耦。

---

## 9. 歌词同步核心算法

假设 API 返回：

```text
position = 83.42s
```

歌词：

```text
80.2s  夜空中最亮的星
83.5s  能否听清
87.2s  那仰望的人
```

壁纸端查找：

```text
80.2 <= currentTime < 83.5
```

则当前行：

```text
夜空中最亮的星
```

### 关键原则

API 不需要每 16ms 提供一次位置。

获得：

```text
serverPosition = 83.42
syncTimestamp = performance.now()
```

之后本地计算：

```text
localPosition = serverPosition + (performance.now() - syncTimestamp) / 1000
```

直到下一次 API 校准。

如果播放器暂停：

```text
localPosition = pausedPosition
```

如果检测到：

```text
abs(serverPosition - localPosition) > threshold
```

立即重新校准。

这样可以兼顾精度和性能。

---

## 10. 歌词渲染方式

建议只维护当前歌词附近的 5~7 行，而不是整个歌词列表都参与复杂动画。

例如：

```text
上一句
上一句

当前句

下一句
下一句
```

当前句：

```css
transform: scale(1.12);
opacity: 1;
filter: blur(0);
```

普通句：

```css
transform: scale(0.92);
opacity: 0.25;
filter: blur(2px);
```

切歌或句子变化时：

```text
淡入
↓
位移
↓
缩放
↓
Glow
```

目标不是传统桌面歌词，而是“居中的沉浸式动态歌词”。

---

## 11. 第一版视觉设计

推荐默认布局：

```text
┌───────────────────────────────────────────────┐
│                                               │
│                                               │
│                 Album Cover                   │
│                                               │
│                                               │
│              夜空中最亮的星                   │
│                                               │
│                 能否听清                     │
│                                               │
│              【那仰望的人】                   │
│                                               │
│                 心底的孤独                   │
│                                               │
│             逃跑计划 · 世界                   │
│                                               │
└───────────────────────────────────────────────┘
```

视觉原则：

- 当前歌词视觉中心
- 非当前歌词降低透明度
- 背景颜色来自专辑封面
- 专辑封面使用圆角 + 阴影 + 轻微浮动
- 背景使用渐变 + 模糊光晕
- 少量粒子，不做高负载 3D 场景
- 切歌时进行过渡动画

---

## 12. 背景动态

专辑封面加载后可以提取主色：

```text
Cover
 ↓
颜色提取
 ↓
Primary / Secondary / Accent
 ↓
CSS Gradient
 ↓
Blur / Glow / Particle
```

例如：

```css
background:
  radial-gradient(
    circle at 50% 40%,
    var(--accent),
    #050505 75%
  );
```

切歌时不要直接替换背景，而是：

```text
旧背景
  ↓
opacity 1 → 0
  ↓
新背景
  ↓
opacity 0 → 1
```

过渡时间建议 800~1500ms。

---

## 13. Wallpaper Engine 设置项

第一版建议只提供少量真正有用的设置。

### 设置 1：歌词位置

```text
顶部
中心
底部
```

### 设置 2：歌词大小

```text
Slider
```

### 设置 3：背景亮度

```text
Slider
```

### 设置 4：显示专辑封面

```text
Checkbox
```

### 设置 5：粒子效果

```text
Checkbox
```

Wallpaper Engine 官方支持 Web Wallpaper 用户属性，例如 Color、Slider、Checkbox、Combo、Text 等，并通过 `wallpaperPropertyListener` 获取属性变化。

参考：

https://docs.wallpaperengine.io/en/web/customization/properties.html
https://docs.wallpaperengine.io/en/web/api/propertylistener.html

---

## 14. Wallpaper Engine FPS / 性能策略

动态壁纸不是游戏，不能无脑 60 FPS + 高强度 WebGL。

推荐：

```text
UI / CSS：跟随 requestAnimationFrame
API：低频轮询
背景：低频缓慢变化
粒子：固定上限
歌词：只更新可见区域
```

同时读取 Wallpaper Engine 用户设置的 FPS 限制，不自行强制高 FPS。

官方文档明确建议 Web Wallpaper 使用 `requestAnimationFrame` 并遵循用户配置的 FPS 限制。

参考：

https://docs.wallpaperengine.io/en/web/performance/fps.html

---

## 15. 第一阶段最重要的功能验收

完成下面这些就算 MVP 完成。

### A. API 连接

- [ ] Now Playing Service 正常运行
- [ ] Wallpaper Engine 页面能够访问 localhost / 127.0.0.1 API
- [ ] 浏览器控制台没有 CORS 错误
- [ ] 能拿到歌曲信息
- [ ] 能拿到播放状态
- [ ] 能拿到播放进度
- [ ] 能拿到歌词

### B. 播放控制同步

- [ ] 网易云播放后壁纸自动出现歌曲
- [ ] 网易云暂停后歌词停止
- [ ] 网易云继续播放后歌词继续
- [ ] 网易云切歌后壁纸立即更新
- [ ] 网易云拖进度条后歌词跳到正确位置

### C. 视觉效果

- [ ] 当前歌词始终位于视觉中心
- [ ] 上下歌词平滑移动
- [ ] 当前歌词有放大/Glow 效果
- [ ] 封面切换动画正常
- [ ] 背景颜色随歌曲切换
- [ ] 16:9 正常
- [ ] 21:9 正常
- [ ] 4K 正常
- [ ] 多显示器正常

### D. 异常情况

- [ ] Now Playing 未启动时壁纸不崩溃
- [ ] 没有正在播放时显示 fallback
- [ ] API 暂时不可用时保留上一次 UI 或显示离线状态
- [ ] 歌词不存在时显示歌曲信息而不是空白
- [ ] 封面加载失败时使用默认背景

---

## 16. Now Playing 未运行时的行为

壁纸绝对不能因为 API 不可用而报错白屏。

状态机建议：

```text
STARTING
   │
   ▼
CONNECTING
   │
   ├── success ──> PLAYING / PAUSED
   │
   └── failure ──> OFFLINE
                    │
                    └── 定时重试
```

OFFLINE 页面建议：

```text
Waiting for Now Playing...
```

或者保持纯动态背景。

---

## 17. 调试方法

Wallpaper Engine 官方提供 CEF DevTools。

在 Wallpaper Engine：

```text
Settings
→ General
→ CEF DevTools Port
```

设置一个端口后，可以从 Chrome 打开对应 localhost 端口，调试 Web Wallpaper 页面。

官方文档：

https://docs.wallpaperengine.io/en/web/debug/debug.html

重点观察：

```text
Console
Network
Elements
Performance
```

首先验证：

```text
fetch Now Playing API
```

其次验证：

```text
歌词 JSON
```

最后再查动画性能。

---

## 18. 开发顺序

### Step 1 - 建立最小 Web Wallpaper

只做：

```text
index.html
main.js
style.css
```

页面先显示：

```text
Now Playing Connection: OK / ERROR
Song: xxx
Artist: xxx
Progress: xx / xx
```

不做任何特效。

### Step 2 - 接入当前歌曲

实现：

```text
歌曲
歌手
专辑
封面
播放状态
播放进度
```

### Step 3 - 接入歌词

实现：

```text
LRC / 时间轴歌词
↓
LyricLine[]
```

### Step 4 - 完成本地时钟同步

实现：

```text
API position
     ↓
SyncClock
     ↓
requestAnimationFrame
```

### Step 5 - 完成歌词动画

实现：

```text
current line
previous lines
next lines
opacity
scale
translate
blur
```

### Step 6 - 完成封面 / 背景

实现：

```text
cover
↓
color extraction
↓
gradient + glow
```

### Step 7 - 加入 Wallpaper Engine 设置

实现：

```text
歌词位置
字号
背景亮度
封面开关
粒子开关
```

### Step 8 - 性能优化

最后再处理：

```text
FPS
粒子数量
DOM 更新范围
图片缓存
API 轮询
```

---

## 19. 一个非常重要的实现原则

不要让动画依赖 API 轮询。

错误方式：

```text
每 100ms API
 ↓
更新 DOM
 ↓
歌词动画
```

正确方式：

```text
API
 ↓
获得时间锚点
 ↓
本地时间轴
 ↓
requestAnimationFrame
 ↓
60FPS/用户配置 FPS 动画
```

API 的作用是“校准”。

浏览器动画的作用是“连续渲染”。

这是整个项目最重要的架构原则。

---

## 20. 关于 CORS 的实际处理策略

### 优先方案

直接让 Wallpaper Engine 请求：

```text
http://127.0.0.1:<PORT>
```

如果 Now Playing API 响应允许跨域，则直接使用。

### 如果出现 CORS 错误

错误典型表现：

```text
Access to fetch at ... has been blocked by CORS policy
```

此时不要首先修改 Wallpaper Engine。

优先检查 Now Playing Server 是否提供：

```text
Access-Control-Allow-Origin
```

以及允许的方法/Headers。

### 不推荐

不要为了规避 CORS 引入新的 Node/Python Proxy。

项目目标已经明确“不开发其他模块”，所以第一版应尽量保持：

```text
Now Playing
     ↓
Wallpaper Engine
```

只有确认当前 Now Playing 版本无法让 Wallpaper Engine 正常访问时，才重新评估是否利用 Now Playing 自带的“页面部署/内置服务器”方案让页面与 API 尽可能处于同源环境。

---

## 21. 为什么不要把前端做成完全在线网站

Wallpaper Engine 官方建议 Web Wallpaper 的重要资源尽量随壁纸一起打包，而不是依赖外部网站，否则离线或远端服务不可用时壁纸可能失效。

因此：

```text
HTML       → 本地
CSS        → 本地
JS         → 本地
字体       → 本地（需要时）
图片素材   → 本地
```

只有“当前音乐数据”来自本机 Now Playing API。

这样即使没有互联网，正在播放的歌曲仍然可以由本机服务提供数据；只有需要在线获取歌词/封面的新歌曲时才可能受到 Now Playing 上游数据源影响。

参考：https://docs.wallpaperengine.io/en/web/first/gettingstarted.html

---

## 22. 最终交付物

项目最终只需要一个 Wallpaper Engine 项目：

```text
MusicLyricsWallpaper/
├── index.html
├── assets/
├── js/
├── css/
└── project.json
```

用户安装后：

```text
1. 启动 Now Playing Service
2. 启动网易云音乐
3. 播放歌曲
4. Wallpaper Engine 启用该壁纸
5. 壁纸自动显示歌曲 / 封面 / 同步歌词
```

不需要额外安装：

```text
× Python
× Node.js
× .NET
× Java
× 新的 Windows Service
```

因为所有业务逻辑都在 Wallpaper Engine Web Wallpaper 内部运行；Now Playing Service 作为已有的外部数据源存在。

---

## 23. 最终推荐技术选型

```text
Wallpaper Engine Web Wallpaper
        │
        ├── HTML
        ├── CSS
        ├── TypeScript
        │
        ├── DOM
        │    └── 歌词动画
        │
        └── Canvas
             └── 粒子 / 背景动态

数据源：
Widdit/now-playing-service

通信：
HTTP JSON API

同步：
本地 monotonic clock + API 定期校准

构建：
Vite

最终运行环境：
Wallpaper Engine CEF
```

不建议第一版加入 React、Vue、Next.js、Electron、Tauri 等框架。这个项目的页面结构很简单，直接 TypeScript + DOM/CSS 更轻量，也更符合动态壁纸对性能和部署简单性的要求。

---

## 24. 第一版完成标准

如果以下流程可以连续稳定运行，就认为方案验证成功：

```text
网易云播放《歌曲 A》
        ↓
Now Playing 识别歌曲 A
        ↓
Wallpaper 获取歌曲 A
        ↓
显示 A 的封面
        ↓
显示 A 的歌词
        ↓
歌词根据当前进度移动
        ↓
暂停
        ↓
歌词停止
        ↓
继续播放
        ↓
歌词继续
        ↓
网易云拖到 01:32
        ↓
Wallpaper 歌词跳到 01:32
        ↓
切换歌曲 B
        ↓
封面 / 背景 / 歌词全部切换
```

这条链路跑通后，再做视觉打磨，而不要先花大量时间做特效。

---

## 25. 参考资料

### Wallpaper Engine

- Web Wallpaper：https://docs.wallpaperengine.io/web/overview.html
- 创建 Web Wallpaper：https://docs.wallpaperengine.io/en/web/first/gettingstarted.html
- Web 调试：https://docs.wallpaperengine.io/en/web/debug/debug.html
- 用户属性：https://docs.wallpaperengine.io/en/web/customization/properties.html
- 属性监听：https://docs.wallpaperengine.io/en/web/api/propertylistener.html
- FPS：https://docs.wallpaperengine.io/en/web/performance/fps.html
- 音频可视化：https://docs.wallpaperengine.io/en/web/audio/visualizer.html

### Now Playing Service

- GitHub：https://github.com/Widdit/now-playing-service
- Releases：https://github.com/Widdit/now-playing-service/releases

根据项目当前公开说明，Now Playing Service 已提供本地 API、歌词、播放状态、歌曲进度、封面等能力，并在 v2.1.5 增加网易云音乐进度条同步。

---

# 结论

本项目不需要开发任何新的桌面后台程序。

最简洁、最合理的架构就是：

```text
             网易云音乐
                  │
                  ▼
      ┌─────────────────────┐
      │ Now Playing Service │
      │  本地 API / 歌词     │
      └──────────┬──────────┘
                 │
                 │ HTTP JSON
                 ▼
      ┌─────────────────────┐
      │ Wallpaper Engine    │
      │ Web Wallpaper       │
      │                     │
      │ HTML/CSS/TypeScript │
      │                     │
      │ 歌词 + 封面 + 特效   │
      └─────────────────────┘
```

第一阶段真正需要验证的唯一外部依赖问题，就是：**Wallpaper Engine 的 CEF 页面能否直接访问你当前安装版本的 Now Playing 本地 API，以及该 API 是否返回允许跨域的响应头。**

这一步一旦验证通过，后面的项目就是标准 Web Wallpaper 开发，不需要再造后端。
