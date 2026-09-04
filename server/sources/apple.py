# -*- coding: utf-8 -*-
"""Apple Music 数据源（Windows SMTC）。

修复点 vs 旧版 AppleMusicMonitor：
  - 初始化/运维异常不再「永久退出」——改为指数退避重试，永不阻塞网易云主路径；
  - 可选依赖：winrt 未安装时源直接降级为不可用，服务照常启动。
"""
from __future__ import annotations

import asyncio
import threading
import time
from typing import Callable, Optional

from ..console import console
from ..core.state import RawSnapshot
from .base import PlayerSource

try:  # 可选依赖：缺 winrt 时 Apple 源自动停用，不影响网易云
    from winrt.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager,
    )
except Exception:  # pragma: no cover - 依赖缺失环境
    GlobalSystemMediaTransportControlsSessionManager = None


class AppleMusicSource(PlayerSource):
    name = "applemusic"

    def __init__(self, publish: Callable[[str, RawSnapshot], None], *,
                 interval: float = 0.2, max_backoff: float = 30.0) -> None:
        self.publish = publish
        self.interval = max(0.05, interval)
        self.max_backoff = max_backoff
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._health = "ok"
        self._ready = False
        self._last_error = ""
        self._last_song_key: str | None = None

    # ---- 生命周期 ----
    def start(self) -> None:
        if GlobalSystemMediaTransportControlsSessionManager is None:
            self._health = "dead"
            console.log("winrt.Windows.Media.Control 未安装，Apple Music 源已停用")
            return
        if self._thread is None:
            self._thread = threading.Thread(
                target=self._run, name="source-applemusic", daemon=True)
            self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def health(self) -> str:
        return self._health

    def is_ready(self) -> bool:
        """首轮 SMTC 会话枚举是否已完成（供 /healthz / 仲裁诊断）。"""
        return self._ready

    def _run(self) -> None:
        try:
            asyncio.run(self._watch())
        except Exception as exc:  # pragma: no cover - 兜底
            self._health = "dead"
            self._last_error = str(exc)

    async def _watch(self) -> None:
        backoff = 1.0
        unavailable_announced = False
        while not self._stop.is_set():
            try:
                manager = await (
                    GlobalSystemMediaTransportControlsSessionManager
                    .request_async())
                self._ready = True
                self._health = "ok"
                if unavailable_announced:
                    unavailable_announced = False
                    console.log("Apple Music SMTC 已恢复")
                while not self._stop.is_set():
                    status = await self._read_status(manager)
                    if status is not None:
                        self.publish(self.name, status)
                        if status.has_song:
                            self._note_playing(status)
                    await asyncio.sleep(self.interval)
            except asyncio.CancelledError:
                break
            except Exception as exc:
                # SMTC 偶发不可用（睡眠/锁屏/会话损坏）：退避重试；只在
                # 「可用→不可用」切换时报一次，静默重试，恢复时报「已恢复」。
                self._ready = False
                self._health = "degraded"
                self._last_error = str(exc)
                if not unavailable_announced:
                    unavailable_announced = True
                    console.log(f"Apple Music SMTC 暂不可用（静默重试，检测到即恢复）: {exc}")
                try:
                    await asyncio.sleep(backoff)
                except asyncio.CancelledError:
                    break
                backoff = min(backoff * 2, self.max_backoff)
            else:
                backoff = 1.0

    async def _read_status(self, manager) -> RawSnapshot | None:
        for session in manager.get_sessions():
            source_id = (session.source_app_user_model_id or "").lower()
            if "applemusic" not in source_id:
                continue

            props = await session.try_get_media_properties_async()
            timeline = session.get_timeline_properties()
            playback = session.get_playback_info()
            duration = timeline.end_time.total_seconds()
            if not props.title or duration <= 0:
                return None

            # Apple Music 把专辑拼进 Artist 字段（“歌手 — 专辑”），只取歌手
            author = props.artist.split(" — ", 1)[0].strip()

            # SMTC 时间线会定期刷新；两次刷新间隔内用时间戳补偿
            progress = timeline.position.total_seconds()
            is_playing = playback.playback_status.name == "PLAYING"
            if is_playing:
                try:
                    elapsed = time.time() - timeline.last_updated_time.timestamp()
                    progress = min(progress + max(0.0, elapsed), duration)
                except Exception:
                    pass

            return RawSnapshot(
                source="applemusic", playing=is_playing, progress=progress,
                duration=duration, song=props.title.strip(), author=author,
                has_song=True)
        return None

    def _note_playing(self, status: RawSnapshot) -> None:
        key = f"{status.song}|{status.author}"
        if key == self._last_song_key:
            return
        self._last_song_key = key
        console.log(f"Apple Music：正在播放「{status.song} - {status.author}」")