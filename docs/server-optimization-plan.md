# server 端架构优化方案

> 版本：v1 · 2026-09-04
> 状态：待评审（未实施，实施前请先阅读「11. 待拍板决策」）
> 适用范围：`server/` 目录（`nowplaying_server.py` / `offset_probe.py` / `console.py` / `netease_nowplaying.py` 及周边）
> 目标：异步 / 稳定性 / 性能 / 扩展性
> 约束：`GET /query`、`GET /api/lyric` 对外契约保持兼容，前端改动尽量收敛

---

## 1. 现状与问题地图

现有文件职责与要解决的问题：

| 现有文件 | 职责 | 主要问题 |
|---|---|---|
| `nowplaying_server.py` | 三合一：HTTP 服务 + 数据源读取 + 歌词 API | 单线程 HTTP；请求热路径做同步网络调用；Apple 初始化失败拖死主路径；多源仲裁在请求路径内做 |
| `offset_probe.py` | 偏移自动探测 + 版本缓存 | 纯 Python 回退扫描巨慢；探测不对外暴露状态 |
| `console.py` | 终端输出 | 只对标 stdout，非 TTY 直接退化；样式靠字符串前缀嗅探，脆弱 |
| `netease_nowplaying.py` | 遗留独立工具 | 与主服务重复实现 Monitor，且标题解析顺序与主服务相反（`artist, song = title.split(" - ", 1)`，bug） |

**核心矛盾一句话**：这是一个「阻塞型 IO 密集 + 需要长期运行」的本地服务，却用了「线程 + 同步阻塞 + 每次请求现算」的模型。

### 已识别的问题明细（按严重度）

**🔴 高（可靠性/阻塞）**
1. **单线程 `HTTPServer` + 请求热路径做同步网络调用**（`nowplaying_server.py:668`）。`handle_query`/`handle_lyric` 每次都调 `search_song()`——含 10 秒超时的同步 `urllib` 请求。首次未命中或网易接口慢时，一个请求就能把整个服务堵住 10 秒；前端 200ms 轮询期间堆积的请求排队吃满超时 → 前端表现为间歇性「离线」。
2. **搜索/歌词失败不缓存「负结果」**（`nowplaying_server.py:486-487` / `500-501`）。网易接口故障期间，每个 `/query` 都触发一次完整 10 秒网络调用，与服务端拥塞互相放大。
3. **Apple Music 初始化失败拖死主服务**（`nowplaying_server.py:411-415`）。`_watch` 里 `request_async()` 抛异常后循环退出且永不重试，`_ready` 永远为 `False` → `is_initializing()` 永远 `True` → `get_status()` 永远返回 `None`，网易云主路径一并被禁用。可选数据源成了单点故障。
4. **前端请求无超时、无「单飞」保护**（`wallpaper/src/api/nowPlaying.ts:22`；`wallpaper/src/player/MusicState.ts:40`）。服务端慢时 200ms 间隔下叠出大量并发 fetch。

**🟡 中（性能/效率）**
5. **轮询 200ms = 5Hz**（`wallpaper/src/api/config.ts:14`），注释自己都写「建议每秒轮询一次」。本架构就是「低频校准 + 本地时钟推进」，1s 足够。
6. **每 0.5s 全桌面 `EnumWindows`**（`nowplaying_server.py:171-199`），且 `attach()` 每 tick 重新 `list_modules()`（`:153-166`）。
7. **歌词无服务端缓存**：壁纸重载会重复拉同一首歌的歌词。
8. **`search_song` 缓存读写在锁内、网络请求在锁外**：并发对同一 (song, author) 会「惊群」重复打网络。
9. **`offset_probe` 纯 Python 回退扫描巨慢**（`offset_probe.py:172-185`）：无 numpy 时按 8 字节步长整镜像遍历，可能数分钟。

**🟢 低（正确性/维护性）**
10. `netease_nowplaying.py` 标题解析顺序与主服务相反（见上表，必有一个错）。
11. `SyncClock`（前端）暂停状态小幅拖进度条不更新（边缘 case）。
12. 字号/行距默认值在 `wallpaper.ts` / `renderer.ts` / `main.css` 三处重复。
13. CORS 全开（`nowplaying_server.py:516`）→ 任何网页可读取本机正在播放的歌名（隐私暴露面）。
14. 多开 `cloudmusic.exe` 未处理（README 已知限制）。

---

## 2. 目标架构总览

```
┌───────────────────────────── asyncio 事件循环 ─────────────────────────────┐
│                                                                            │
│   aiohttp HTTP Server (/query /api/lyric /sse /healthz /metrics)           │
│        │                                                                   │
│        ▼                                                                   │
│   ┌──────────────────── 状态存储 (StateStore)  ────────────────────┐       │
│   │  唯一写者：状态编排任务；HTTP 读同一 loop，无锁                │       │
│   │  resolved: NowPlayingState + seq + resolve 标记               │       │
│   └───────────────▲──────────────────────────────┬───────────────┘       │
│                   │ 变更事件(切歌/状态)           │ 写回 track 缓存         │
│       ┌───────────┴───────────┐        ┌─────────▼───────────┐            │
│       │  仲裁器 ArbiterTask   │        │  ResolverTask       │            │
│       │  (纯计算，无 IO)      │        │ 搜索/歌词/封面 异步   │            │
│       └───────────▲───────────┘        │ aiohttp 客户端+缓存  │            │
│                   │ 原始快照(线程→事件循环桥)  │ +负缓存+熔断    │            │
└───────────────────┼─────────────────────┴───────────────────────────┘     │
                    │ asyncio.Queue（threadsafe）                             │
     ┌──────────────┴─────────────┐        ┌─────────────────────┐          │
     │  NeteaseReader (工作线程)   │        │  SMTCReader (线程)   │          │
     │  pymem 阻塞读取 + 标题       │        │  WinRT 循环           │          │
     │  版本缓存/自动探测          │        │  失败重试+降级        │          │
     └────────────────────────────┘        └─────────────────────┘          │
```

**设计原则一句话**：阻塞的留给线程，调度都进事件循环；状态单写者跑在事件循环上，HTTP 变成只读快照的分发器；所有外部网络 IO 全部异步 + 缓存 + 熔断。

---

## 3. 异步化改造

### 3.1 异步 HTTP 层

- **替换 `HTTPServer` → `aiohttp`**。理由：
  - 从根上消除「单线程 + 请求路径同步网络调用」的拥塞问题；
  - 原生支持 SSE / WebSocket / 优雅关闭 / 并发；
  - 一个包同时提供 async server + async client，联动方便。
- **备选**：FastAPI + uvicorn。带 pydantic schema 与自动 OpenAPI，扩展性更强，但额外引入 pydantic 等依赖。**默认选 aiohttp**，出现「多端点 + 对外 schema 文档」的强需求再迁移。
- 兼容性：`/query`、`/api/lyric` 路径、方法、JSON 结构保持原样。

### 3.2 状态流：线程 → 事件循环 → 只读分发

阻塞源（pymem、EnumWindows、WinRT SMTC）本质无法真正异步，方案是：

1. 每个 Reader 线程按各自节奏读原始快照（netease 0.2~0.5s、SMTC 0.2s，配置化）；
2. 通过 `asyncio.Queue`（线程安全 put）或 `loop.call_soon_threadsafe` 投递；
3. 事件循环上的**状态编排任务**消费原始快照 → 跑仲裁（纯内存计算，毫秒级）→ 写 `StateStore`（单写者）→ 比对 `seq` 生成变更事件；
4. HTTP handler 读 `StateStore.resolved` —— **同一事件循环，天然无锁**。

收益：请求路径不再做任何仲裁/计算/网络 IO；仲裁从「每次 HTTP 现算」变成「事件驱动、内容不变不重算」。

### 3.3 网易云 API 异步客户端

网易云搜索/歌词请求从 `urllib` 换成 aiohttp 客户端：

- 连接池 + keep-alive 长连复用（一个全局 Session）；
- `timeout=ClientTimeout(total=5, connect=3)`；
- 失败重试：1/2/4s 指数退避，最多 3 次；
- **熔断**：连续失败 N 次进入冷却窗口，冷却期内直接返回缓存/空，避免 200ms 轮询放大故障；
- per-key `asyncio.Lock` 单飞，修「惊群」；
- 响应在 StateStore 内**预序列化为 bytes** 缓存，`/query` 直接吐同一份字节流。

---

## 4. 歌词子系统：可插拔 Provider 体系（重点）

> 目标：未来换用任何歌词 API / 本地歌词文件，都只新增一个 Provider 类，**不动 HTTP 层、数据模型、前端契约**。

### 4.1 先拆两个概念（关键解耦）

「**搜索**」和「**取歌词**」解耦成两套接口 —— 因为「能搜歌的 API」和「能给歌词的 API」可能是两个不同服务：

- **TrackSearcher**：按 歌名/歌手 解析出某个体系内的歌曲 ID（如 netease_id）；
- **LyricsProvider**：给定「已尽量补齐的 identifiers」，返回规范化歌词。

```
api/
├── identities.py            # TrackIdentifiers —— 多 provider 共用的歌曲身份
├── search/
│   ├── base.py              # TrackSearcher ABC：按 歌名/歌手 解析出某体系的 ID
│   ├── netease_searcher.py  # 现 search_song 逻辑迁进（返回 netease_id + album/cover）
│   └── registry.py          # 搜索器列表 + 优先级（可再加 QQ/Kugou/Bilibili…）
├── lyrics/
│   ├── base.py              # LyricsProvider ABC：给定 identifiers 返回规范化歌词
│   ├── types.py             # LyricsResult（规范化 schema，不绑定任何 provider）
│   ├── netease_provider.py  # 现 get_lyrics 逻辑迁进
│   ├── file_provider.py     # 本地 .lrc 目录（离线/自建歌词，无网络）
│   ├── chain.py             # 兜底链 + 熔断/冷却/负缓存 + 单飞锁
│   ├── cache.py             # 内存 + 磁盘缓存（按规范化 key，跨 provider 去重）
│   └── registry.py          # provider 列表 + 优先级
└── resolver.py              # 切歌事件 → Searcher → 补全 id → LyricsChain → 缓存 → 推送
```

### 4.2 核心接口（规约）

```python
from dataclasses import dataclass, field
from abc import ABC, abstractmethod

# ---- 歌曲身份：任何 provider 都能看懂的“通行证” ----
@dataclass
class TrackIdentifiers:
    title: str
    artist: str
    album: str = ""
    duration: float = 0
    netease_id: str | None = None     # 未来顺延: qq_id / kugou_id / isrc / bili_id …
    extra: dict = field(default_factory=dict)

# ---- 搜索器：把“歌名/歌手”解析成某个体系内的 ID ----
class TrackSearcher(ABC):
    vendor: str = ""                   # "netease" | "qq" | …

    @abstractmethod
    async def search(self, t: TrackIdentifiers) -> dict | None:
        """返回该体系 id 及可用信息（album/cover/duration…），无法解析返回 None"""

    def cooldown(self) -> float:
        """失败冷却秒；默认 60"""
        return 60.0

# ---- 歌词提供者：给我身份，还我统一 schema ----
class LyricsProvider(ABC):
    name: str = ""                     # "netease" / "qq" / "local-file"

    @abstractmethod
    async def fetch(self, ids: TrackIdentifiers) -> "LyricsResult | None":
        """无歌词 / 无法解析返回 None"""

    def requires(self) -> tuple[str, ...]:
        """依赖哪个 id 才能参与：如 ("netease_id",)；本地文件源返回 () 表示只靠 title/artist"""
        return ()

    def cooldown(self) -> float:
        return 60.0

# ---- 统一返回（即 /api/lyric 的字段来源） ----
@dataclass
class LyricsResult:
    provider: str                  # 实际命中者：netease / qq / local-file …
    has_lyric: bool
    lrc: str = ""
    translated_lyric: str = ""
    karaoke_lyric: str = ""
    meta: dict = field(default_factory=dict)   # 来源 URL、耗时、是否降级、错误
```

要点：

- **新增一个歌词源 = 实现一个 `LyricsProvider` + 注册一行**，HTTP 层、数据模型、前端一律不动；
- 有的源只认某个 ID（网易歌词要 `netease_id`），有的源不需要 ID（本地文件按文件名匹配）—— 由 `requires()` 告知 chain 能否参与；
- Searcher 同样可插拔：网易搜索失败时（VIP / 歌曲不在网易曲库），后续 searcher 可补出其他体系的 id，再由认得该 id 的 provider 出歌词。

### 4.3 Provider 链的稳定性/性能策略（`lyrics/chain.py`）

保证「加进一个坏 provider 不拖垮整体」：

- 按优先级顺序逐步尝试，第一个非 `None` 命中者胜；
- 每个 provider 独立**熔断 / 冷却**：连续失败 N 次 → 冷却窗口内直接跳过，冷却期满单次试探成功后恢复；
- **负缓存 + 单飞锁**：某 (provider, 歌曲) 刚失败过 → 短窗内跳过重试；同歌曲并发请求只发一次网络；
- **并行竞速模式**（可配置，默认关）：多个 provider 同时发，最快命中者胜，牺牲确定性换延迟；
- **总超时上限**（如 8s），防止多源串行把请求拖长；
- **降级路径**：全部失败 → 返回空 + `resolve.state="degraded"`，前端显示歌名 fallback（已有）。

### 4.4 「下载歌词」→ 磁盘缓存层

`lyrics/cache.py` 两层：

- **内存 LRU**：热歌零延迟；
- **磁盘目录**（`<data_dir>/lyrics/<hash>.lrc`）：第一次从任何 provider 拿到歌词就落盘 —— 等价于「把歌词下载到本地」，**同一首歌以后换任何源 / 断网都能直接读**，也方便用户手动放进/导出 `.lrc`。

缓存 key 用**规范化**的 `normalize(title, artist)`（与 provider 无关），跨 provider 去重成一条。

### 4.5 `source` 字段语义变化（兼容）

- 现在 `/api/lyric` 的 `source` 写死 `"netease"`；
- 改造后改为**实际命中的 provider 名**，其余字段与现有契约完全一致 —— 前端不感知，仅作诊断/展示；
- `resolve.state` 暴露：`resolving / ok / degraded / from-cache`。

### 4.6 本地文件 Provider（推荐第一期就带）

`file_provider` 只需要一个 `lyrics_dir` 配置，按 `{title} - {artist}.lrc` / `{netease_id}.lrc` 匹配。零网络、天然离线、用户可自建 —— 是不依赖在线 API 时的保底，也是新 API 接入前最容易试水的第一个样例 provider。

### 4.7 示例实现骨架（供评审参考，非最终代码）

```python
# api/lyrics/netease_provider.py
class NeteaseLyricsProvider(LyricsProvider):
    name = "netease"

    def requires(self) -> tuple[str, ...]:
        return ("netease_id",)

    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        if not ids.netease_id:
            return None
        url = (f"https://music.163.com/api/song/lyric?id={ids.netease_id}"
               f"&lv=-1&kv=-1&tv=-1")
        data = await self.session.get_json(url, headers=API_HEADERS)  # aiohttp + 熔断
        lrc = (data.get("lrc") or {}).get("lyric", "") or ""
        return LyricsResult(
            provider=self.name,
            has_lyric=bool(lrc),
            lrc=lrc,
            translated_lyric=(data.get("tlyric") or {}).get("lyric", "") or "",
            karaoke_lyric=(data.get("klyric") or {}).get("lyric", "") or "",
            meta={"url": url},
        )

# api/lyrics/file_provider.py
class FileLyricsProvider(LyricsProvider):
    name = "local-file"

    def requires(self) -> tuple[str, ...]:
        return ()  # 只靠 title/artist 匹配文件名

    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        for pattern in (f"{ids.title} - {ids.artist}.lrc", f"{ids.netease_id}.lrc"):
            path = self.lyrics_dir / pattern
            if path.is_file():
                return LyricsResult(
                    provider=self.name,
                    has_lyric=True,
                    lrc=(await asyncio.to_thread(path.read_text, "utf-8")),
                    meta={"path": str(path)},
                )
        return None
```

---

## 5. 稳定性设计

| 威胁 | 现状 | 方案 |
|---|---|---|
| Apple SMTC 初始化失败 → 整个服务返回 None | 高危 | 初始化设 3s 上限；失败标记 `available=False` 并指数退避重试；**永不阻塞网易云主路径** |
| `winrt` 环境缺失 → import 直接崩 | 高危 | 可选导入 + 运行时检查；requirements 拆分（见 §10） |
| 网易接口故障 → 每请求 10s 超时 → 服务拥塞 | 高 | §3.3 熔断 + 负缓存；`/query` 永不做网络 |
| 网易云升级/换版本 → 偏移失效 | 中 | 保留 `OffsetResolver`，升级为可观测：对外暴露 `offset_state`（not-ready/probing/ready/failed）；探测独立任务 + 指数退避，不阻塞服务启动 |
| 源线程崩（进程重启、SMTC 会话断） | 中 | 每个源一个 supervisor：异常自动重启 + 退避；单源故障不影响其他源 |
| Ctrl+C / 进程被杀 → 残留状态行/光标 | 低 | aiohttp 优雅关闭 → 停 Reader → `console.stop()`；atexit 兜底已有 |

补充：

- **日志**：`console.py` 字符串前缀嗅探样式改为 `logging` 分层记录。TTY 用现有状态行作为 formatter/handler；非 TTY 写 `RotatingFileHandler` 按 level 过滤。切歌/降级/熔断打 `WARNING`，偏移探测打 `INFO`，帧级状态不打日志。
- **降级显式化**：搜索失败保留 `local-crc32` 伪 ID（`nowplaying_server.py:584`）；歌词不可用返回空 + 显式 `resolve.state`，前端已有 fallback。

---

## 6. 性能设计

#### 请求路径
- `/query` 只读 StateStore 缓存字节，(理想) p99 < 5ms，无 10s 网络尾巴；
- 前端 `pollIntervalMs` 200 → 1000（一行配置，`wallpaper/src/api/config.ts:14`），随后可接 SSE 全事件驱动。

#### 源读取
- **合并内存读**：`progress`/`rate` 相邻（+0x8）、`duration` 在 +0x60000，一次 `read_bytes` 读连续段，替代每字段多次 8B 调用；
- **标题只在该变时变**：用内存侧切歌检测（duration 变化 && progress 归零）触发 `EnumWindows`，从每 0.5s 一次降到每次切歌一次（`nowplaying_server.py:171-199`）；
- **模块列表常驻**：`attach()` 的 `list_modules()` 只在失败时重做（`nowplaying_server.py:153-166`）。

#### 偏移探测
- `numpy` 从可选改默认必需（探测提速两个数量级）；
- 纯 Python 回退改为粗筛+精扫（64B 步长定位候选段 → 候选段 8B 精扫）；
- 只读 dll 已初始化数据段（解析 PE 节表跳过不可读页），缩小快照体积。

#### 状态帧
- 状态编排任务按 `seq` 去重，内容未变不重算 JSON、不推 SSE；
- 各轮询频率内聚到 config：`netease_poll_interval` / `smtc_poll_interval` / `status_publish_min_interval`（默认 0.2s），统一调参。

---

## 7. 模块拆分（扩展性）

把 681 行单文件按稳定边界拆开，每块可单测：

```
server/
├── __main__.py           # python -m server 入口
├── config.py             # 端口/开关/优先级/cookie/token 默认值（dataclass）
├── server.py             # 装配与生命周期：启动各组件、优雅关闭
├── console.py            # 保留，但只处理 TTY 状态行；日志交给 logging
├── core/
│   ├── state.py          # NowPlayingState 数据类 + 字典格式（唯一 schema）
│   ├── store.py          # StateStore：单写者快照 + seq + 变更订阅
│   ├── arbiter.py        # 多源仲裁策略（从现 get_status 里抽出来，`nowplaying_server.py:426-438`）
│   └── stats.py          # 计数器/耗时分位数，/metrics 数据源
├── sources/
│   ├── base.py           # PlayerSource 抽象基类
│   ├── netease.py        # pymem 读取 + 窗口标题 + 切歌检测优化
│   ├── apple.py          # SMTC 读取 + 失败重试/降级
│   └── registry.py       # 源注册表 + 优先级配置
├── api/                  # 见 §4（歌词体系）+ 下述
│   ├── routes.py         # aiohttp 路由 + CORS + Token
│   └── sse.py            # SSE 推送
├── probing/
│   └── offset_probe.py   # 从 standalone 改造成可注入服务，暴露 offset_state
└── tests/                # pytest：仲裁/缓存/降级/API 契约
```

`netease_nowplaying.py`：**删除**或重写为「一行命令调主服务的调试前端」（`--poll` / `--json`），不再维护第二套 Monitor。

---

## 8. 扩展性设计（播放源层）

除歌词体系外，播放源同样抽象，把「再加一个播放器」降成「新增一个类」：

```python
class PlayerSource(ABC):
    name: str                       # "netease" | "apple" | …
    def start(self) -> None: ...    # 启动自身线程/任务
    def stop(self) -> None: ...
    async def initial_snapshot(self) -> RawSnapshot | None: ...
    def poll_interval(self) -> float: ...
    def health(self) -> SourceHealth: ...    # ok / degraded / dead
```

- 注册表 `registry.register(NetEaseSource())`；仲裁器按「正在播放 > 最近状态变化 > 配置优先级」多源仲裁（现 `nowplaying_server.py:426-438` 的策略抽成可替换 policy）；
- 新闻源建设：`config.json`（可选）支持启停源、优先级、网易 cookie、端口、token、轮询频率 —— 默认值与现在完全一致，向后兼容；
- 新端点（兼容 `/query`、`/api/lyric`）：`/sse`（状态变更即时推送）、`/api/v2/state`（富状态：`resolve.state` / `source.health`）、`/healthz`、`/metrics`；
- 测试：pytest 覆盖仲裁、缓存 TTL/负缓存、降级+熔断、API 契约（mock monitor）、probe 合理性 —— 当前 server 侧零测试，这是扩展性地基。

---

## 9. 分阶段实施计划（每阶段可独立交付）

| 阶段 | 内容 | 风险 | 建议周期 |
|---|---|---|---|
| **Stage 0 · 止血**（不改架构，纯修 bug + 降本） | ① `ThreadingHTTPServer`；② 搜索/歌词负缓存；③ Apple 初始化时限+失败重试；④ 标题按切歌刷新 + 合并内存读；⑤ 前端轮询 200→1000ms | 低 | 0.5~1 天 |
| **Stage 1 · 异步内核 + 歌词 Provider 骨架** | 接入 aiohttp；状态流改为线程投递→事件循环编排→StateStore；网易云 API 换 async client + 连接池/重试/熔断/单飞；落 **Provider 接口 + 规范化 schema + netease_provider + file_provider + 磁盘缓存**（网易行为与现状一致，只是套了层皮） | 中 | 3~4 天 |
| **Stage 2 · 模块化 + 配置化 + 链策略** | 按 §7 拆包；PlayerSource/YricsProvider 注册表 + policy + 熔断/冷却/负缓存/单飞 + 竞速开关；`config.json`；`/sse`、`/healthz`、`/metrics`；logging 替换 console 嗅探 | 中 | 2~3 天 |
| **Stage 3 · 打磨** | probe 提速（numpy 必需 + PE 节表 + 粗精扫）；预序列化缓存；优雅关闭；pytest + CI | 低 | 1~2 天 |

说明：
- Stage 0 纯净收益、可独立上线；Stage 1 是主框架；每阶段结束时 `/query`、`/api/lyric` 契约保持不变；
- 前端只需在 Stage 1 后把轮询降频/接 SSE（独立于服务端，可后置）。

---

## 10. 工程化

- `requirements.txt` 拆分并锁版本：
  - `requirements-core.txt`：`aiohttp`、`pymem`、`pywin32`、`psutil`、`numpy`
  - `requirements-apple.txt`：`winrt-Windows.Media.Control`（缺了也能跑，只少 Apple 源）
- 入口改为 `python -m server`；
- CI 补 `python -m compileall` + 单测。

---

## 11. 待拍板决策

1. **异步框架**：aiohttp（轻、够用，默认推荐）vs FastAPI（pydantic + OpenAPI，重，未来多端点更爽）。
2. **SSE 是否纳入本期**：纳入 → 前端 `MusicState` 从轮询改事件驱动，双端联动，工作量 +0.5~1 天；不纳入 → 先降频到 1s 轮询。
3. **歌词 Provider 优先级顺序**：本地文件在前（零成本磁盘 IO，放前面几乎零代价）→ 在线 provider 按配置；还是在线优先、本地文件做最后兜底。**默认建议本地文件在前**。
4. **网易云多开进程是否本期处理**：已有 README 已知限制，默认延后。

---

## 12. 评审清单

实施前请确认：

- [ ] §11 四项决策已定
- [ ] `GET /query`、`GET /api/lyric` 结构逐字段比对，完全兼容
- [ ] Stage 1 网易行为与现状一致（灰度验证：旧/新服务并行跑对比）
- [ ] `winrt` 缺失环境下服务可正常启动（只提示 Apple 不可用）
- [ ] 前后端联调：轮询降频 / SSE 接入分别测试