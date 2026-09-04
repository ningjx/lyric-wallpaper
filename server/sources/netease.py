# -*- coding: utf-8 -*-
"""网易云音乐数据源（内存读取）。

核心做法与旧 nowplaying_server.NeteaseMonitor 一致：
  - 偏移由 OffsetResolver 自动定位（后台线程，含版本缓存与失败重试）；
  - 播放/暂停用「较长采样间隔内进度移动量」判定；
  - 歌名/歌手从窗口标题解析（网易云窗口不含 SMTC）。
优化点 vs 旧版：
  - 合并内存读：progress+rate 一段 16B、duration 一段 8B，替代逐字段 read_bytes；
  - 窗口标题只在「新歌/标题为空/超时兜底」时枚举一次（旧版每 0.5s 全桌面枚举）；
  - 模块列表只在句柄失效时重新枚举。
"""
from __future__ import annotations

import struct
import time

from ..core.state import RawSnapshot
from ..probing.offset_probe import OffsetResolver
from .base import PollingSource

PROCESS_NAME = "cloudmusic.exe"
MODULE_NAME = "cloudmusic.dll"


class NeteaseSource(PollingSource):
    name = "netease"

    def __init__(self, publish, *, interval: float = 0.2,
                 title_recheck: float = 30.0) -> None:
        super().__init__("netease", interval, publish)
        self.title_recheck = title_recheck
        self.resolver = OffsetResolver()
        self._pm = None
        self._base = None
        self._title_cache: tuple[str, float] = ("", 0.0)
        self._last_dur_key: float | None = None
        # 播放/暂停判定用
        self._last_progress: float | None = None
        self._last_progress_at = 0.0
        self._stationary_samples = 0
        self._playing = True

    def start(self) -> None:
        # 偏移探测是独立后台线程；即使版本未知/未探测完成，服务照常可提供 Apple 源
        self.resolver.start()
        super().start()

    # ---- 读取 ----
    def read(self) -> RawSnapshot | None:
        offsets = self.resolver.current()
        if offsets is None:
            return None  # 偏移尚未就绪（探测中/网易云未运行）
        if not self._attach():
            return None
        try:
            progress, duration = self._read(offsets)
            title = self._get_title(duration)
            song = author = ""
            if " - " in title:
                song, author = title.split(" - ", 1)
            playing = self._classify(progress)
            return RawSnapshot(
                source="netease", playing=playing, progress=progress,
                duration=duration, song=song.strip(), author=author.strip(),
                has_song=bool(song.strip()) and duration > 0,
            )
        except Exception:
            # 读取失败（进程退出等）：失效句柄，下次重新枚举模块
            self._pm = None
            self._base = None
            return None

    def _attach(self) -> bool:
        if self._pm is not None and self._base is not None:
            return True
        try:
            if self._pm is None:
                import pymem
                self._pm = pymem.Pymem(PROCESS_NAME)
            self._base = None
            for m in self._pm.list_modules():
                if m.name.lower() == MODULE_NAME:
                    self._base = m.lpBaseOfDll
                    break
            return self._base is not None
        except Exception:
            self._pm = None
            self._base = None
            return False

    def _read(self, offsets: dict) -> tuple[float, float]:
        # progress(+rate) 连续 16 字节一次读完，duration 独立 8 字节
        buf = self._pm.read_bytes(self._base + offsets["progress"], 16)
        progress = struct.unpack("<d", buf[:8])[0]
        duration = struct.unpack(
            "<d", self._pm.read_bytes(self._base + offsets["duration"], 8))[0]
        return progress, duration

    def _classify(self, progress: float) -> bool:
        """进度在较长采样间隔内连续两次不动才判暂停（同旧版判定）。"""
        now = time.time()
        if self._last_progress is None:
            self._last_progress = progress
            self._last_progress_at = now
            return self._playing
        if now - self._last_progress_at < 0.35:
            return self._playing
        elapsed = now - self._last_progress_at
        moved = progress - self._last_progress
        if moved > elapsed * 0.15:
            self._stationary_samples = 0
            self._playing = True
        else:
            self._stationary_samples += 1
            if self._stationary_samples >= 2:
                self._playing = False
        self._last_progress = progress
        self._last_progress_at = now
        return self._playing

    # ---- 窗口标题（歌名 - 歌手） ----
    def _get_title(self, duration: float) -> str:
        now = time.time()
        cached, age = self._title_cache
        dkey = round(duration, 1)
        # 只有「新歌（时长变化）/ 标题为空 / 超过兜底间隔」才重新枚举全桌面
        if cached and now - age < self.title_recheck and dkey == self._last_dur_key:
            return cached
        title = self._enumerate_title()
        self._title_cache = (title, now)
        self._last_dur_key = dkey
        return title

    def _enumerate_title(self) -> str:
        import win32gui
        import win32process

        titles = []

        def cb(hwnd, _):
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid == self._pm.process_id:
                t = win32gui.GetWindowText(hwnd)
                # 标题格式 '歌名 - 歌手'，排除桌面歌词/迷你播放器等干扰窗口
                if (" - " in t and "桌面歌词" not in t
                        and "迷你播放器" not in t and "GDI+" not in t):
                    titles.append(t)
            return True

        try:
            win32gui.EnumWindows(cb, None)
        except Exception:
            return self._title_cache[0]
        return max(titles, key=len) if titles else self._title_cache[0]