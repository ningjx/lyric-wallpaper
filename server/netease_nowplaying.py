# -*- coding: utf-8 -*-
"""
网易云音乐播放状态监控 (内存读取方案)
=====================================
通过读取 cloudmusic.dll 进程内存获取实时播放进度，比 UI Automation 更精确可靠。

字段偏移由 offset_probe 自动探测（偏移随网易云版本变化），无需手动配置。

用法:
  python netease_nowplaying.py            # 轮询模式
  python netease_nowplaying.py --json     # 输出 JSON (供其他程序集成)
  python netease_nowplaying.py --serve    # 启动本地 HTTP 服务 (http://localhost:8899/nowplaying)
"""
import argparse
import ctypes
import json
import struct
import sys
import time

import pymem

from offset_probe import OffsetResolver

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# ============ 配置 ============
PROCESS_NAME = "cloudmusic.exe"
MODULE_NAME = "cloudmusic.dll"


class NeteaseMonitor:
    def __init__(self):
        self.pm = None
        self.base = None
        self._window_title = ""
        self.resolver = OffsetResolver()

    def attach(self):
        """附加到网易云进程并定位 cloudmusic.dll"""
        if self.pm is None:
            self.pm = pymem.Pymem(PROCESS_NAME)
        self.base = None
        for module in self.pm.list_modules():
            if module.name.lower() == MODULE_NAME:
                self.base = module.lpBaseOfDll
                break
        return self.base is not None

    def read_float(self, offset):
        return struct.unpack("<d", self.pm.read_bytes(self.base + offset, 8))[0]

    def get_status(self):
        """读取当前播放状态"""
        self.resolver.start()
        offsets = self.resolver.current()
        if offsets is None:
            return None  # 偏移尚未解析（正在探测/未运行）
        if not self.attach():
            return None
        try:
            progress = self.read_float(offsets["progress"])
            duration = self.read_float(offsets["duration"])
            rate = self.read_float(offsets["rate"])
            playing = abs(rate) > 0.5
            return {
                "progress": progress,
                "duration": duration,
                "rate": rate,
                "playing": playing,
                "paused": not playing and progress > 0.01,
            }
        except Exception:
            return None

    def get_window_title(self):
        """通过窗口标题获取 歌手 - 歌名"""
        import win32gui
        import win32process
        title = ""

        def cb(hwnd, _):
            nonlocal title
            if win32gui.IsWindowVisible(hwnd):
                _, pid = win32process.GetWindowThreadProcessId(hwnd)
                if pid == self.pm.process_id:
                    t = win32gui.GetWindowText(hwnd)
                    if " - " in t:
                        title = t
                        return False
            return True

        win32gui.EnumWindows(cb, None)
        return title


def run_poll(json_mode=False):
    mon = NeteaseMonitor()
    print("网易云音乐播放监控已启动 (内存读取方案)")
    print("偏移自动探测中，首次使用请确保正在播放一首歌...")
    print("按 Ctrl+C 退出\n")

    while True:
        try:
            status = mon.get_status()
            if status is None:
                if mon.resolver.current() is None:
                    print("偏移探测中...（请确保网易云正在播放歌曲）")
                else:
                    print("网易云未运行或读取失败...")
                time.sleep(2)
                continue

            title = mon.get_window_title()
            song, artist = ("", "")
            if " - " in title:
                artist, song = title.split(" - ", 1)

            prog_m, prog_s = divmod(status["progress"], 60)
            dur_m, dur_s = divmod(status["duration"], 60)

            if json_mode:
                print(json.dumps({
                    "title": song,
                    "artist": artist,
                    "progress": round(status["progress"], 2),
                    "duration": round(status["duration"], 2),
                    "playing": status["playing"],
                    "progress_str": f"{int(prog_m):02d}:{prog_s:05.2f}",
                    "duration_str": f"{int(dur_m):02d}:{dur_s:05.2f}",
                }, ensure_ascii=False))
            else:
                state = "▶ 播放中" if status["playing"] else "⏸ 已暂停"
                print(f"  {state} | {artist} - {song} | "
                      f"{int(prog_m):02d}:{prog_s:05.2f} / {int(dur_m):02d}:{dur_s:05.2f}")

            time.sleep(0.5)
        except KeyboardInterrupt:
            print("\n已退出。")
            break
        except Exception as e:
            print(f"错误: {e}")
            time.sleep(2)


def run_serve(port=8899):
    """启动本地 HTTP 服务"""
    from http.server import BaseHTTPRequestHandler, HTTPServer

    mon = NeteaseMonitor()

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            status = mon.get_status()
            if status is None:
                self.send_response(503)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.end_headers()
                self.wfile.write(json.dumps({"error": "netease not running"}, ensure_ascii=False).encode())
                return

            title = mon.get_window_title()
            song, artist = ("", "")
            if " - " in title:
                artist, song = title.split(" - ", 1)

            data = {
                "title": song,
                "artist": artist,
                "progress": round(status["progress"], 2) + 0.0,  # 修正延迟
                "duration": round(status["duration"], 2),
                "playing": status["playing"],
            }
            body = json.dumps(data, ensure_ascii=False).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args):
            pass  # 静默

    server = HTTPServer(("127.0.0.1", port), Handler)
    print(f"HTTP 服务已启动: http://127.0.0.1:{port}/nowplaying")
    server.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="网易云音乐播放监控")
    parser.add_argument("--json", action="store_true", help="输出 JSON 格式")
    parser.add_argument("--serve", action="store_true", help="启动 HTTP 服务")
    parser.add_argument("--port", type=int, default=8899, help="HTTP 端口")
    args = parser.parse_args()

    if args.serve:
        run_serve(args.port)
    else:
        run_poll(args.json)
