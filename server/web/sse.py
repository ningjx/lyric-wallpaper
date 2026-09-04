# -*- coding: utf-8 -*-
"""SSE 事件集线器：广播切歌/状态变化给订阅的前端连接。

只广播「有信息量」的事件（切歌、解析完成、播放状态翻转），
进度帧不推 —— 前端用本地 SyncClock 推进即可，避免 200ms 风暴。
"""
from __future__ import annotations

import asyncio
from typing import Any, Set


class EventHub:
    def __init__(self, maxsize: int = 16) -> None:
        self._subs: Set[asyncio.Queue] = set()
        self._maxsize = maxsize

    def subscribe(self) -> asyncio.Queue:
        q: asyncio.Queue = asyncio.Queue(maxsize=self._maxsize)
        self._subs.add(q)
        return q

    def unsubscribe(self, q: asyncio.Queue) -> None:
        self._subs.discard(q)

    def publish(self, payload: Any) -> None:
        """丢弃最旧事件，防止慢消费者积累（wallpaper=失去连接重建）。"""
        for q in list(self._subs):
            if q.full():
                try:
                    q.get_nowait()
                except asyncio.QueueEmpty:
                    pass
            try:
                q.put_nowait(payload)
            except asyncio.QueueFull:
                pass

    @property
    def subscriber_count(self) -> int:
        return len(self._subs)