# -*- coding: utf-8 -*-
"""
网易云音乐 Now Playing API 替代服务
====================================
替代 Widdit/now-playing-service 的本地 API，供 lyric-wallpaper 使用。

端点 (与 Now Playing Service 保持一致):
  GET /query      -> 播放器 + 歌曲状态 (NowPlayingState)
  GET /api/lyric  -> 当前歌曲歌词 (LyricsResponse)

数据来源:
  - 歌曲名/歌手/播放进度/时长/播放状态: cloudmusic.dll 进程内存 (偏移自动探测)
  - 歌词: 网易云音乐官方 API (music.163.com/api/song/lyric)

用法:
  python nowplaying_server.py [--port 9863]
"""
import argparse
import json
import re
import struct
import sys
import time
import urllib.parse
import urllib.request
import zlib
from http.server import BaseHTTPRequestHandler, HTTPServer
from threading import Lock

import pymem

from offset_probe import OffsetResolver

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# ============ 配置 ============
PROCESS_NAME = "cloudmusic.exe"
MODULE_NAME = "cloudmusic.dll"

# 播放状态偏移由 offset_probe.OffsetResolver 自动定位（偏移随版本变化，运行时探测）

# 正常运行提示：周期性心跳间隔（秒）。服务端在每次 /query 轮询中检测，命中即打印一行当前状态
HEARTBEAT_INTERVAL = 20.0

API_HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
    "Referer": "https://music.163.com",
    "Cookie": "appver=2.10.6; os=pc;",
}

RE_TIME_TAG = re.compile(r"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]")


# ============ 工具函数 ============
def sec_to_human(sec: float) -> str:
    """秒 -> MM:SS 字符串"""
    sec = max(0, int(sec))
    return f"{sec // 60:02d}:{sec % 60:02d}"


def human_to_sec(text: str) -> float:
    """MM:SS -> 秒"""
    try:
        m, s = text.strip().split(":")
        return int(m) * 60 + float(s)
    except Exception:
        return 0.0


def lrc_to_seconds(tag: str) -> float:
    """LRC 时间标签 [mm:ss.xx] -> 秒"""
    try:
        inner = tag.strip("[]")
        parts = inner.split(":")
        if len(parts) == 2:
            return int(parts[0]) * 60 + float(parts[1])
        if len(parts) == 3:
            return int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2])
    except Exception:
        pass
    return 0.0


def lrc_to_human(sec: float) -> str:
    """秒 -> 网易云 LRC 格式时间标签 [mm:ss.xx]"""
    m = int(sec // 60)
    s = sec - m * 60
    return f"[{m:02d}:{s:05.2f}]"


# ============ 网易云内存监控 ============
class NeteaseMonitor:
    """读取 cloudmusic.dll 内存中的播放状态"""

    def __init__(self):
        self.pm = None
        self.base = None
        self._title_cache = ("", 0.0)  # (标题, 获取时间戳)
        self._title_ttl = 0.5  # 窗口标题缓存 0.5 秒，避免 200ms 轮询时频繁枚举窗口
        self.resolver = OffsetResolver()
        self._resolver_started = False
        self._ready_logged = False
        self._last_heartbeat = 0.0   # 上次心跳打印时间戳
        self._last_song_key = None   # 上次歌曲标识 (song|author)，用于切歌检测

    def start(self):
        """启动后台偏移解析线程（幂等，可在 main 中提前调用）"""
        if not self._resolver_started:
            self._resolver_started = True
            self.resolver.start()

    def attach(self) -> bool:
        try:
            if self.pm is None or self.pm.process_id == 0:
                self.pm = pymem.Pymem(PROCESS_NAME)
            self.base = None
            for module in self.pm.list_modules():
                if module.name.lower() == MODULE_NAME:
                    self.base = module.lpBaseOfDll
                    break
            return self.base is not None
        except Exception:
            self.pm = None
            self.base = None
            return False

    def read_float(self, offset: int) -> float:
        return struct.unpack("<d", self.pm.read_bytes(self.base + offset, 8))[0]

    def get_window_title(self) -> str:
        """通过窗口标题获取 '歌手 - 歌名'（包含最小化/隐藏窗口，带缓存）"""
        now = time.time()
        if now - self._title_cache[1] < self._title_ttl:
            return self._title_cache[0]

        import win32gui
        import win32process

        titles = []

        def cb(hwnd, _):
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid == self.pm.process_id:
                t = win32gui.GetWindowText(hwnd)
                # 歌曲标题格式: '歌名 - 歌手'，排除桌面歌词/迷你播放器等
                if (" - " in t and "桌面歌词" not in t
                        and "迷你播放器" not in t and "GDI+" not in t):
                    titles.append(t)
            return True

        try:
            win32gui.EnumWindows(cb, None)
        except Exception:
            pass

        result = max(titles, key=len) if titles else ""
        self._title_cache = (result, time.time())
        return result

    def get_status(self) -> dict | None:
        """获取当前播放状态"""
        self.start()  # 确保后台探测线程已启动（幂等）
        offsets = self.resolver.current()
        if offsets is None:
            return None  # 偏移尚未解析（正在探测/未运行）
        if not self.attach():
            return None
        try:
            progress = self.read_float(offsets["progress"])
            duration = self.read_float(offsets["duration"])
            rate = self.read_float(offsets["rate"])
            title = self.get_window_title()
            song = author = ""
            if " - " in title:
                # 网易云窗口标题格式: '歌名 - 歌手'
                song, author = title.split(" - ", 1)

            playing = abs(rate) > 0.5
            status = {
                "progress": progress,
                "duration": duration,
                "playing": playing,
                "song": song.strip(),
                "author": author.strip(),
                "has_song": bool(song) and duration > 0,
            }
            if status["has_song"]:
                self._note_playing(status)
            return status
        except Exception:
            return None

    def _note_playing(self, status):
        """正常运行提示：首次就绪 / 切歌 / 周期性心跳"""
        key = f"{status['song']}|{status['author']}"
        now = time.time()

        # 1) 首次就绪
        if not self._ready_logged:
            self._ready_logged = True
            self._last_song_key = key
            self._last_heartbeat = now
            m, s = divmod(int(status["duration"]), 60)
            print(f"✓ 服务就绪：正在播放「{status['song']} - {status['author']}」"
                  f" ({m:02d}:{s:02d})", flush=True)
            return

        # 2) 切歌
        if key != self._last_song_key:
            self._last_song_key = key
            self._last_heartbeat = now
            m, s = divmod(int(status["duration"]), 60)
            print(f"▶ 切歌：正在播放「{status['song']} - {status['author']}」"
                  f" ({m:02d}:{s:02d})", flush=True)
            return

        # 3) 周期性心跳
        if now - self._last_heartbeat >= HEARTBEAT_INTERVAL:
            self._last_heartbeat = now
            pm, ps = divmod(int(status["progress"]), 60)
            dm, ds = divmod(int(status["duration"]), 60)
            state = "播放中" if status["playing"] else "已暂停"
            print(f"[nowplaying] {state}  {status['song']} - {status['author']}  "
                  f"{pm:02d}:{ps:02d}/{dm:02d}:{ds:02d}", flush=True)


# ============ 网易云 API ============
class NeteaseApi:
    """网易云音乐官方 API (搜索 + 歌词)"""

    def __init__(self):
        self.cache: dict[str, dict] = {}  # (song, author) -> 歌曲信息
        self.lock = Lock()

    def _http_get(self, url: str) -> dict:
        req = urllib.request.Request(url, headers=API_HEADERS)
        resp = urllib.request.urlopen(req, timeout=10)
        return json.loads(resp.read())

    def search_song(self, song: str, author: str) -> dict | None:
        """搜索歌曲，返回 {id, title, author, album, cover, duration}"""
        key = f"{song}|{author}"
        with self.lock:
            if key in self.cache:
                return self.cache[key]

        query = f"{song} {author}".strip()
        url = ("https://music.163.com/api/search/get/web?s="
               + urllib.parse.quote(query) + "&type=1&limit=5")
        try:
            data = self._http_get(url)
            songs = data.get("result", {}).get("songs", [])
            if not songs:
                return None
            # 选最佳匹配：优先歌名完全相同
            best = songs[0]
            for s in songs:
                if s.get("name", "").strip() == song.strip():
                    best = s
                    break
            album = best.get("album") or {}
            info = {
                "id": str(best.get("id", "")),
                "title": best.get("name", ""),
                "author": (best.get("artists") or [{}])[0].get("name", ""),
                "album": album.get("name", ""),
                "cover": album.get("picUrl", "") or "",
                "duration": round((best.get("duration", 0) or 0) / 1000, 3),
            }
            with self.lock:
                self.cache[key] = info
            return info
        except Exception:
            return None

    def get_lyrics(self, song_id: str) -> dict:
        """获取歌词: {lrc, translatedLyric, karaokeLyric}"""
        url = (f"https://music.163.com/api/song/lyric?id={song_id}"
               f"&lv=-1&kv=-1&tv=-1")
        try:
            data = self._http_get(url)
            return {
                "lrc": (data.get("lrc") or {}).get("lyric", "") or "",
                "translatedLyric": (data.get("tlyric") or {}).get("lyric", "") or "",
                "karaokeLyric": (data.get("klyric") or {}).get("lyric", "") or "",
            }
        except Exception:
            return {"lrc": "", "translatedLyric": "", "karaokeLyric": ""}


# ============ HTTP 服务 ============
class NowPlayingHandler(BaseHTTPRequestHandler):
    monitor = NeteaseMonitor()
    api = NeteaseApi()

    def log_message(self, *args):
        pass  # 静默

    def _json(self, code: int, data: dict):
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        """CORS 预检"""
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Access-Control-Max-Age", "86400")
        self.end_headers()

    def do_GET(self):
        path = urllib.parse.urlparse(self.path).path
        if path == "/query":
            self.handle_query()
        elif path == "/api/lyric":
            self.handle_lyric()
        else:
            self._json(404, {"error": "not found"})

    def handle_query(self):
        """GET /query -> NowPlayingState"""
        status = self.monitor.get_status()
        if status is None or not status["has_song"]:
            # 网易云未运行或无歌曲
            self._json(200, {
                "player": {
                    "hasSong": False,
                    "isPaused": False,
                    "volumePercent": 0,
                    "seekbarCurrentPosition": 0,
                    "seekbarCurrentPositionHuman": "00:00",
                    "statePercent": 0,
                    "likeStatus": "false",
                    "repeatType": "list",
                },
                "track": {
                    "id": "",
                    "title": "",
                    "author": "",
                    "album": "",
                    "cover": "",
                    "duration": 0,
                    "durationHuman": "00:00",
                    "url": "",
                    "isVideo": False,
                    "isAdvertisement": False,
                    "inLibrary": False,
                },
            })
            return

        progress = status["progress"]
        duration = status["duration"]

        # 通过 API 补全歌曲信息 (id/album/cover)
        info = self.api.search_song(status["song"], status["author"])
        if info:
            track_id = info["id"]
            album = info["album"]
            cover = info["cover"]
        else:
            # 搜索失败（离线等）：用标题哈希生成稳定伪 ID，保证切歌检测仍可用
            track_id = "local-" + str(zlib.crc32(f"{status['song']}|{status['author']}".encode("utf-8")))
            album = ""
            cover = ""

        self._json(200, {
            "player": {
                "hasSong": True,
                "isPaused": not status["playing"],
                "volumePercent": 100,
                "seekbarCurrentPosition": round(progress, 3),
                "seekbarCurrentPositionHuman": sec_to_human(progress),
                "statePercent": round(progress / duration * 100, 1) if duration > 0 else 0,
                "likeStatus": "false",
                "repeatType": "list",
            },
            "track": {
                "id": track_id,
                "title": status["song"],
                "author": status["author"],
                "album": album,
                "cover": cover,
                "duration": round(duration, 3),
                "durationHuman": sec_to_human(duration),
                "url": f"https://music.163.com/#/song?id={track_id}" if track_id else "",
                "isVideo": False,
                "isAdvertisement": False,
                "inLibrary": False,
            },
        })

    def handle_lyric(self):
        """GET /api/lyric -> LyricsResponse"""
        status = self.monitor.get_status()
        if status is None or not status["has_song"]:
            self._json(200, {
                "source": "netease",
                "title": "",
                "author": "",
                "duration": 0,
                "hasLyric": False,
                "hasTranslatedLyric": False,
                "hasKaraokeLyric": False,
                "lrc": "",
                "translatedLyric": "",
                "karaokeLyric": "",
            })
            return

        info = self.api.search_song(status["song"], status["author"])
        if not info:
            self._json(200, {
                "source": "netease",
                "title": status["song"],
                "author": status["author"],
                "duration": round(status["duration"], 3),
                "hasLyric": False,
                "hasTranslatedLyric": False,
                "hasKaraokeLyric": False,
                "lrc": "",
                "translatedLyric": "",
                "karaokeLyric": "",
            })
            return

        lyrics = self.api.get_lyrics(info["id"])
        self._json(200, {
            "source": "netease",
            "title": status["song"],
            "author": status["author"],
            "duration": round(status["duration"], 3),
            "hasLyric": bool(lyrics["lrc"]),
            "hasTranslatedLyric": bool(lyrics["translatedLyric"]),
            "hasKaraokeLyric": bool(lyrics["karaokeLyric"]),
            "lrc": lyrics["lrc"],
            "translatedLyric": lyrics["translatedLyric"],
            "karaokeLyric": lyrics["karaokeLyric"],
        })


def main():
    parser = argparse.ArgumentParser(description="网易云 Now Playing API 替代服务")
    parser.add_argument("--port", type=int, default=9863, help="监听端口 (默认 9863)")
    args = parser.parse_args()

    server = HTTPServer(("127.0.0.1", args.port), NowPlayingHandler)
    NowPlayingHandler.monitor.start()  # 提前启动偏移探测（后台线程）
    print(f"Now Playing API 替代服务已启动: http://127.0.0.1:{args.port}")
    print(f"  端点: /query (状态)  /api/lyric (歌词)")
    print(f"  数据源: 内存读取 (进度/状态) + 网易云 API (歌词)")
    print("按 Ctrl+C 停止")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止。")


if __name__ == "__main__":
    main()
