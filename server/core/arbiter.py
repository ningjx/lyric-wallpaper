# -*- coding: utf-8 -*-
"""多源仲裁：在多个数据源快照之间选出当前应展示的播放器状态。

策略（与旧 nowplaying_server.py 一致）：
  - 有播放器正在播放时只在它们之间选；否则在暂停的播放器之间选；
  - 同一组内取「状态最近变化」的那个（最近切到该源的优先）。

失联剔除：快照时间戳超过 max_snapshot_age 视为源已停更，直接剔除，避免
「死源霸屏」。阈值必须显著大于数据源轮询间隔（默认 0.2s 的 10 倍），
否则正常抖动也会被误判失联。
"""
from __future__ import annotations

import time
from typing import Dict, Optional

from .state import RawSnapshot

#: 数据源快照超过该时长（秒）视为失联，不再参与仲裁（默认值，可被配置覆盖）
DEFAULT_MAX_SNAPSHOT_AGE = 2.0


class Arbiter:
    def __init__(self, priority: tuple[str, ...] = (),
                 max_snapshot_age: float = DEFAULT_MAX_SNAPSHOT_AGE) -> None:
        self.priority = list(priority)
        self.max_snapshot_age = max_snapshot_age

    def pick(self, snapshots: Dict[str, RawSnapshot],
             transitions: Dict[str, float],
             now: Optional[float] = None) -> RawSnapshot | None:
        """从各源快照中选一个作为当前播放器状态；无有效快照返回 None。"""
        now = now or time.monotonic()

        fresh: list[RawSnapshot] = []
        for name, snap in snapshots.items():
            if not snap.has_song:
                continue
            if now - snap.ts > self.max_snapshot_age:
                continue  # 源失联，剔除
            fresh.append(snap)
        if not fresh:
            return None

        playing = [snap for snap in fresh if snap.playing] or fresh
        return max(
            playing,
            key=lambda s: (transitions.get(s.source, 0.0),
                           -self.priority.index(s.source)
                           if s.source in self.priority else -1),
        )

    def state_changed(self, prev: RawSnapshot | None, snap: RawSnapshot) -> bool:
        """播放状态是否发生「值得记录转换时间戳」的变化。"""
        if prev is None:
            return True
        return ((prev.playing, prev.song, prev.author,
                 round(prev.duration, 1)) !=
                (snap.playing, snap.song, snap.author, round(snap.duration, 1)))