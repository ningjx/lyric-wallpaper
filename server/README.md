# lyric-wallpaper server — 网易云音乐 / Apple Music Now Playing 服务

为同仓库的壁纸前端（`wallpaper/src/`，Wallpaper Engine 歌词壁纸）提供网易云音乐或 Microsoft Store 版 Apple Music 的播放状态与歌词的本地服务，替代 [Widdit/now-playing-service](https://github.com/Widdit/now-playing-service)。

**核心价值**：原 now-playing-service 通过 UI Automation 读取播放进度，最小化/桌面歌词模式下经常读不到。本项目改用**进程内存读取**，精度毫秒级、任何窗口状态都可用。

## 架构

```
网易云音乐 (cloudmusic.exe)
    │
    ├─ [内存读取] cloudmusic.dll + 自动探测偏移
    │     ├─ 播放进度 (float64 秒)     dll+0x1D808F8
    │     ├─ 歌曲时长 (float64 秒)     dll+0x1DE1038
    │     └─ 播放速率 (float64)        dll+0x1D80900  (1.0=播放, 0.0=暂停)
    │     └─ offset_probe.py：版本不匹配时后台自动重定位并缓存
    │
    ├─ [Win32 API] 窗口标题枚举
    │     └─ "歌名 - 歌手"  (含隐藏窗口)
    │
    └─ [HTTP API] music.163.com
          ├─ 搜索: /api/search/get/web  → 歌曲 ID/专辑/封面
          └─ 歌词: /api/song/lyric      → LRC 歌词

Apple Music (Microsoft Store)
    │
    └─ [Windows SMTC] 系统媒体会话
          ├─ 歌名 / 歌手
          ├─ 播放 / 暂停
          └─ Timeline：当前进度 / 总时长（支持拖动进度条同步）

server 包 (aiohttp 异步服务, 127.0.0.1:9863)
    │
    ├─ GET /query      → 播放器+歌曲状态（兼容原服务格式）
    ├─ GET /api/lyric  → LRC 歌词（兼容原服务格式，source 为实际 provider）
    ├─ GET /sse        → 状态/切歌实时推送（可选，配合前端降频）
    └─ GET /healthz, /metrics
```

## 快速开始

```bash
pip install -r requirements.txt
python -m server                       # 默认端口 9863
```

可选：`--port` / `--host` / `--config config.json` / `--token`。缺省时与旧服务行为一致，壁纸前端无需任何改动（端口和 `/query`、`/api/lyric` 响应格式完全兼容）。

## 文件说明

### 核心模块（生产使用）

`server/` 是 Python 包，入口 `python -m server`：

| 模块 | 作用 |
|------|------|
| `__main__.py` / `server.py` | 入口与生命周期：装配组件、启动 aiohttp、优雅关闭 |
| `config.py` | dataclass 默认配置 + 可选 `config.json` 覆盖（命令行 > 配置文件 > 默认值） |
| `core/` | 状态存储（单写者，工作线程投递 → 事件循环仲裁）、多源仲裁、/query 负载、指标 |
| `sources/` | 数据源抽象（PlayerSource）+ 网易云内存读取 + Apple SMTC，工作线程轮询，故障自隔离 |
| `lyrics/` | **可插拔歌词体系**：搜索器（TrackSearcher）+ Provider 链（LocalFile/Netease）+ 熔断/冷却/负缓存 + 内存/磁盘缓存 + 切歌解析编排 |
| `web/` | aiohttp 路由：`/query` `/api/lyric`（兼容）`/sse` `/healthz` `/metrics`，CORS/Token |
| `probing/offset_probe.py` | **偏移自动探测**。按版本缓存偏移，失效时自动扫描内存重定位，暴露 `offset_state` 供诊断 |
| `offsets_config.json` | 偏移缓存（自动生成）。记录各版本探测出的偏移，换电脑/升级后首次启动自动重建 |
| `requirements*.txt` | 依赖拆分：`requirements-core.txt`（必需）/ `requirements-apple.txt`（可选 winrt）/ `requirements-dev.txt`（测试） |
| `tools/mem_scan.py` | 手动重定位工具（历史脚本，仅调试用）。自动探测失败时可用它手工定位 |

**新增播放器数据源** = 实现 `PlayerSource` 子类，在 `sources/registry.py` 注册一行。
**新增歌词源** = 实现 `LyricsProvider` 子类（如用 QQ/Kugou API），在 `config.lyrics.provider_order` 里加入即可，HTTP 层与前端不动。

### 研究/实验脚本（`tools/` 目录，仅供理解原理）

这些是开发过程中逐步探索留下的脚本，**不需要运行**，但展示了每一步的验证方法。下次改代码时，如果主程序出现问题，可以从对应脚本找到验证思路：

| 文件 | 当时的实验目的 | 结论 |
|------|---------------|------|
| `scan_smtc.py` | 验证网易云是否注册 Windows SMTC 媒体会话 | ❌ 没有注册。Win+G 显示的媒体信息不来自 SMTC，此路不通 |
| `mem_scan.py` | 扫描 cloudmusic.dll 找播放进度偏移 | ✅ 用"3 秒内值增加约 3 秒"的启发式找到 `0x1D808F8` |
| `verify_candidate.py` / `verify_full.py` | 验证候选地址确实是播放进度 | ✅ 每秒精确 +1.0，暂停冻结，切歌重置 |
| `find_duration.py` / `find_duration2.py` / `find_duration3.py` | 找歌曲总时长偏移 | ✅ 用 API 查到的已知时长反查内存，找到 `0x1DE1038` |
| `analyze_duration.py` / `analyze_structure.py` | 分析时长/进度值的内存结构，尝试找静态指针链 | 未找到静态指针（数据在堆上），放弃指针链方案 |
| `monitor_songchange.py` | 切歌验证：进度/时长/速率字段是否原地更新 | ✅ 切歌瞬间进度重置、时长原地更新 |
| `check_lyrics_sources.py` | 检查本地歌词缓存和桌面歌词窗口 | 桌面歌词窗口存在但内容是渲染后的，本地缓存无歌词文件 |
| `scan_lyrics_mem.py` / `scan_lyrics_mem2.py` / `scan_lyrics_utf8.py` | 扫描进程内存找歌词 | ✅ 内存中有完整 LRC（UTF-8，渲染进程缓存），但扫描全内存太慢，改用 API |
| `verify_lyrics_switch.py` | 验证内存歌词是否随切歌更新 | 未完成验证（用户未切歌），已放弃此路线 |
| `debug_regions.py` / `debug_status.py` | 调试内存区域枚举和窗口标题读取 | 发现窗口标题要包含隐藏窗口（最小化时窗口不可见） |
| `probe_port.py` | 探测网易云本地端口 20017 是否有 HTTP/WS API | ❌ 无 HTTP API（只回 400 Bad Request） |
| `test_netease_api.py` / `test_lyric_endpoints.py` / `test_lyric_full.py` | 测试网易云歌词 API 各种端点和参数 | ✅ 需要 `Cookie: appver=2.10.6; os=pc;` 头，`lv=-1&kv=-1&tv=-1` 参数 |

### 已删除内容

早期用 C# (.NET) 探测 SMTC 的实验代码已删除。结论已记录在 `tools/scan_smtc.py` 的注释中：网易云不支持 SMTC，此路不通。

## 核心逻辑详解

### 1. 内存偏移（cloudmusic.dll）

针对**网易云音乐 3.1.28（About 显示 Build:205135，exe FileVersion 3.1.28.8527）** 验证的偏移（现由 `offset_probe.py` 自动探测，无需手改）：

```python
OFFSET_PROGRESS = 0x1D808F8  # 播放进度, float64, 单位秒
OFFSET_DURATION = 0x1DE1038  # 歌曲时长, float64, 单位秒
OFFSET_RATE     = 0x1D80900  # 播放速度, float64, 1.0x（暂停时仍为 1.0，非暂停标志）
```

- 进度值**实时跳变**（含拖动进度条），比 UI Automation 读文本快且准
- 暂停时进度冻结 → 据「进度是否随墙钟时间推进」判断播放/暂停（`rate` 字段暂停时仍为 1.0，不能用于判暂停）
- 切歌瞬间进度重置为 0、时长原地更新为新歌 → 据此判断切歌
- 三个字段都是 cloudmusic.dll 数据段里的全局变量，**偏移随版本变化**（DLL 重编译后挪位）。`offset_probe.py` 用「3 秒内进度 +3 秒」的启发式自动重定位：进度紧邻速率（+0x8）、时长在进度 +~0x60000 附近

### 2. 窗口标题（歌曲名/歌手）

- 网易云主窗口标题格式：`歌名 - 歌手`
- **必须枚举所有窗口**（含隐藏窗口），因为最小化到托盘时主窗口不可见，`IsWindowVisible` 过滤会读不到
- 排除干扰窗口：`桌面歌词`、`迷你播放器`、`GDI+ Window`
- 缓存 0.5 秒（200ms 轮询下避免每轮都 EnumWindows）

### 3. 歌词 API

```python
# 关键：必须带 cookie，否则返回空 lrc
headers = {
    "Cookie": "appver=2.10.6; os=pc;",
    "Referer": "https://music.163.com",
}
url = f"https://music.163.com/api/song/lyric?id={song_id}&lv=-1&kv=-1&tv=-1"
```

- 不带 cookie → HTTP 200 但 lrc 为空字符串（坑！）
- 响应字段：`lrc.lyric`（原文）、`tlyric.lyric`（翻译）、`klyric.lyric`（逐字）
- 歌曲 ID 通过搜索 API 获得：`/api/search/get/web?s=<歌名 歌手>&type=1`
- 搜索结果有缓存（按"歌名|歌手"键），避免每 200ms 请求搜索 API

### 4. HTTP 响应格式（与原服务兼容）

`GET /query` 响应结构（壁纸前端依赖此格式）：

```json
{
  "player": {
    "hasSong": true,
    "isPaused": false,
    "seekbarCurrentPosition": 123.45,
    "seekbarCurrentPositionHuman": "02:03",
    "statePercent": 50.9,
    ...
  },
  "track": {
    "id": "554191378",
    "title": "我乐意",
    "author": "许嵩",
    "album": "我乐意",
    "cover": "",
    "duration": 242.436,
    ...
  }
}
```

前端逻辑（`wallpaper/src/player/MusicState.ts` 等）：
- 每 200ms 轮询 `/query`，用 `seekbarCurrentPosition` 校准本地 SyncClock
- `track.id:track.title` 组合键判断切歌 → 切歌时请求 `/api/lyric`
- `player.hasSong == false` → 清空歌词隐藏场景
- CORS 必须全开放（壁纸在 `file://` 环境加载）

## 维护指南

### 网易云升级/换电脑后偏移失效怎么办

**无需手动处理。** 服务启动时：

1. 读取 cloudmusic.dll 版本号，查 `offsets_config.json` 缓存
2. 版本匹配且偏移合法 → 直接使用
3. 版本不匹配 / 偏移失效 → 后台线程自动探测：
   - 扫整个 DLL 镜像，取两帧快照（间隔 3 秒），找 Δ≈3s 且紧邻速率∈{0,1} 的 float64 → 进度偏移
   - 在进度 +~0x60000 附近找「>进度 且两帧不变」的稳定 float64 → 时长偏移
   - 速率 = 进度 +0x8
4. 探测成功 → 写回 `offsets_config.json`，下次直接命中

唯一要求：**首次在新版本上运行时，需正在播放一首歌**（进度 0~3600 秒），否则探测会后台重试，直到开始播放。

> 若自动探测始终失败，可退回手动方式：播放任意歌曲，运行 `python tools/mem_scan.py`（找进度）+ `python tools/find_duration3.py`（找时长），再把结果写进 `offsets_config.json`。

### 历史偏移规律（供参考）

从 GitHub 开源项目收集的旧版偏移显示：进度和时长偏移随版本稳步递增，两个字段间距约 0x60000 字节。找到进度偏移后，时长偏移大概率在进度偏移 +0x60000 附近，可以缩小扫描范围。

### 已知限制

- **首次探测需在播放中**：换电脑/升级后第一次启动时需正在播放一首歌，自动探测才能定位偏移（之后走缓存无需再播放）
- **本地歌曲/私人 FM**：窗口标题可能不含 " - "，或搜索 API 查不到（歌词空，播放状态仍正常）
- **VIP 加密歌曲**：歌词 API 可能返回空，属网易云服务端限制
- **多开网易云**：`pymem.Pymem("cloudmusic.exe")` 取第一个进程，多开场景未处理

## 依赖

```
Python >= 3.10
aiohttp      # 异步 HTTP 服务与客户端（必需）
pymem        # 进程内存读取（必需）
pywin32      # 窗口枚举 (win32gui) + exe 版本读取（必需）
psutil       # 读取 exe 路径识别版本（必需）
numpy        # 偏移探测的向量化扫描（必需）
winrt-Windows.Media.Control  # Apple Music 的 Windows SMTC（可选，缺失时 Apple 源自动停用）

核心安装：pip install -r requirements-core.txt
含 Apple：pip install -r requirements.txt
```
