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
import re

from ..console import console
from ..core.state import RawSnapshot
from ..probing.offset_probe import OffsetResolver
from .base import PollingSource

PROCESS_NAME = "cloudmusic.exe"
MODULE_NAME = "cloudmusic.dll"
_SMTC_WINDOW_MARKER = "mediaplayer smtc window"
_GUID_ONLY = re.compile(r"^\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}$", re.IGNORECASE)


def parse_track_window_title(value: str) -> tuple[str, str] | None:
    """从网易云主窗口标题提取 ``歌名 - 歌手``，拒绝内部宿主窗口。

    新版网易云会在同一 ``cloudmusic.exe`` 进程中创建名为
    ``MediaPlayer SMTC window - {GUID}`` 的内部窗口。它恰好满足旧版的
    分隔符规则，却不是歌曲元数据；必须在进入缓存和状态仲裁前过滤。
    """
    title = " ".join(value.split())
    if " - " not in title or _SMTC_WINDOW_MARKER in title.lower():
        return None
    song, author = (part.strip() for part in title.split(" - ", 1))
    if not song or not author or _GUID_ONLY.fullmatch(author):
        return None
    return song, author


def select_track_window_title(candidates: list[tuple[bool, str]]) -> str:
    """从同进程窗口中选择一个合法歌曲标题，优先可见主窗口。"""
    valid = [(visible, title) for visible, title in candidates
             if parse_track_window_title(title) is not None]
    return max(valid, key=lambda item: (item[0], len(item[1])))[1] if valid else ""


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
        # 一次性提示：就绪/切歌各说一句（目标进程缺席时保持安静）
        self._announced = False
        self._last_song_key: tuple | None = None

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
            track = parse_track_window_title(title)
            song, author = track if track is not None else ("", "")
            playing = self._classify(progress)
            if song.strip() and duration > 0:
                self._note_playing(song.strip(), author.strip(), duration)
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

    def _note_playing(self, song: str, author: str, duration: float) -> None:
        """就绪/切歌各打一条（一次性）。目标进程缺席时本方法根本不触发。"""
        key = (song, author)
        if key == self._last_song_key:
            return
        self._last_song_key = key
        m, s = divmod(int(duration), 60)
        if self._announced:
            console.log(f"▶ 网易云切歌：正在播放「{song} - {author}」 ({m:02d}:{s:02d})")
        else:
            self._announced = True
            console.log(f"✓ 网易云就绪：正在播放「{song} - {author}」 ({m:02d}:{s:02d})")

    # ---- 窗口标题（歌名 - 歌手） ----
    def _get_title(self, duration: float) -> str:
        now = time.time()
        cached, age = self._title_cache
        dkey = round(duration, 1)
        # 有效标题按正常间隔复查；无标题时缩短重试，既不会将内部窗口缓存为
        # 歌曲，也能在主窗口稍后创建/恢复可见时尽快补齐元数据。
        recheck = self.title_recheck if cached else min(2.0, self.title_recheck)
        if now - age < recheck and dkey == self._last_dur_key:
            return cached
        title = self._enumerate_title()
        self._title_cache = (title, now)
        self._last_dur_key = dkey
        return title

    def _enumerate_title(self) -> str:
        import win32gui
        import win32process

        titles: list[tuple[bool, str]] = []

        def cb(hwnd, _):
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid == self._pm.process_id:
                t = win32gui.GetWindowText(hwnd)
                # 标题格式“歌名 - 歌手”。除桌面歌词等旧干扰项外，必须排除
                # 同进程的 MediaPlayer SMTC 内部窗口（标题末尾为 GUID）。
                if ("桌面歌词" not in t and "迷你播放器" not in t
                        and "GDI+" not in t):
                    titles.append((bool(win32gui.IsWindowVisible(hwnd)), t))
            return True

        try:
            win32gui.EnumWindows(cb, None)
        except Exception:
            return self._title_cache[0]
        # 主窗口通常可见。最小化时仍允许不可见的合法标题作为回退；不能再按
        # “最长字符串”选，以免内部诊断窗口覆盖真正歌曲标题。
        return select_track_window_title(titles)
