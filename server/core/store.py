# -*- coding: utf-8 -*-
"""状态存储：单写者快照 + 解析后的播放器状态 + 变更订阅。

线程模型：数据源工作线程只负责产出 RawSnapshot，通过
`loop.call_soon_threadsafe(store.push_source, name, snap)` 投递；
StateStore 的所有字段仅在事件循环上读写，因此无需任何锁。
"""
from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field
from typing import Callable, Dict, Optional

from .arbiter import Arbiter
from .state import RawSnapshot


@dataclass
class ResolvedPlayer:
    """仲裁后的播放器状态（/query 的 player 部分）。"""
    has_song: bool
    playing: bool
    progress: float
    duration: float
    song: str
    author: str
    source: str = ""
    seq: int = 0
    ts: float = field(default_factory=time.time)

    @classmethod
    def empty(cls) -> "ResolvedPlayer":
        return cls(has_song=False, playing=False, progress=0.0, duration=0.0,
                   song="", author="")


@dataclass
class TrackContext:
    """当前歌曲的解析上下文（搜索 + 歌词的产出去向）。"""
    song: str
    author: str
    duration: float
    song_key: tuple[str, str]
    future: asyncio.Future   # 解析完成时置结果，/api/lyric 可等待
    identifiers: object = None
    track_info: object = None        # TrackInfo
    lyric_result: object = None      # LyricsResult
    resolve_state: str = "resolving"  # resolving / ok / degraded / from-cache


class StateStore:
    def __init__(self, arbiter: Arbiter | None = None) -> None:
        self.arbiter = arbiter or Arbiter()
        self._snapshots: Dict[str, RawSnapshot] = {}
        self._transitions: Dict[str, float] = {}
        self.resolved = ResolvedPlayer.empty()
        self.track_context: Optional[TrackContext] = None
        self._on_state: Optional[Callable[[], None]] = None
        self._on_song: Optional[Callable[[str, str, float], None]] = None
        self._last_resolve_logged: tuple | None = None

    # ---- 订阅（server.py 装配时注入） ----
    def attach(self, on_state: Callable[[], None],
               on_song: Callable[[str, str, float], None]) -> None:
        self._on_state = on_state
        self._on_song = on_song

    # ---- 数据源入口（事件循环线程） ----
    def push_source(self, name: str, snap: RawSnapshot) -> None:
        prev = self._snapshots.get(name)
        if self.arbiter.state_changed(prev, snap):
            self._transitions[name] = time.monotonic()
        self._snapshots[name] = snap
        self._reconcile()

    # ---- 仲裁与广播 ----
    def _reconcile(self) -> None:
        best = self.arbiter.pick(self._snapshots, self._transitions)
        if best is None:
            if self.resolved.has_song:
                self.resolved = ResolvedPlayer.empty()
                self._notify_state()
            return

        new_song = ResolvedPlayer(
            has_song=True, playing=best.playing, progress=best.progress,
            duration=best.duration, song=best.song, author=best.author,
            source=best.source, seq=self.resolved.seq + 1, ts=time.time())
        song_changed = ((best.song, best.author) !=
                        (self.resolved.song, self.resolved.author)) or \
                       not self.resolved.has_song

        self.resolved = new_song

        if song_changed:
            self._notify_song(best.song, best.author, best.duration)
        else:
            self._notify_state()

    def _notify_state(self) -> None:
        if self._on_state:
            self._on_state()

    def _notify_song(self, song: str, author: str, duration: float) -> None:
        if self._on_song:
            self._on_song(song, author, duration)
        self._notify_state()

    # ---- 解析上下文（ResolveTask 使用） ----
    def begin_track_context(self, song: str, author: str,
                            duration: float) -> TrackContext:
        fut = asyncio.get_running_loop().create_future()
        self.track_context = TrackContext(
            song=song, author=author, duration=duration,
            song_key=(song, author), future=fut)
        return self.track_context

    def finish_track_context(self, ctx: TrackContext, identifiers,
                             track_info, lyric_result,
                             resolve_state: str) -> None:
        ctx.identifiers = identifiers
        ctx.track_info = track_info
        ctx.lyric_result = lyric_result
        ctx.resolve_state = resolve_state
        if not ctx.future.done():
            ctx.future.set_result(None)
        self._notify_state()

    def current_lyric_result(self) -> object:
        ctx = self.track_context
        return ctx.lyric_result if ctx else None