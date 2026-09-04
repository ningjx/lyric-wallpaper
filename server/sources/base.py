# -*- coding: utf-8 -*-
"""数据源基类。

约定：目标播放器/服务「不存在」是常态，不是异常 —— 检测到就用，
检测不到就安静等待，只在状态变化（出现/消失/切歌）时打一条日志，
后台轮询不得逐次刷屏。新增数据源请遵守此约定。

PlayerSource 抽象让「再加一个播放器」只写一个类：
  - 输出：只准产 RawSnapshot，经 publish 投递到事件循环；
  - 生命周期：start/stop；PollingSource 提供通用工作线程模板；
  - 健康：health() 供 /healthz、/api/v2 诊断。
"""
from __future__ import annotations

import threading
import time
from typing import Callable, Optional

from ..core.state import RawSnapshot


class PlayerSource:
    name: str = ""

    def start(self) -> None:
        raise NotImplementedError

    def stop(self) -> None:
        raise NotImplementedError

    def health(self) -> str:
        """ok / probing / degraded / dead"""
        return "ok"

    def describe(self) -> str:
        return self.name


class PollingSource(PlayerSource):
    """通用轮询式源：工作线程周期调用 read() 产出快照并发布。

    read() 是阻塞/纯计算均可；publish 必须线程安全
    （server.py 会绑定为 loop.call_soon_threadsafe）。
    """

    def __init__(self, name: str, interval: float, publish: Callable[[RawSnapshot], None]) -> None:
        self.name = name
        self.interval = max(0.05, interval)
        self.publish = publish
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._health = "ok"
        self._last_error = ""
        self._last_published_at = 0.0

    def start(self) -> None:
        if self._thread is None:
            self._thread = threading.Thread(
                target=self._loop, name=f"source-{self.name}", daemon=True)
            self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def health(self) -> str:
        return self._health

    def read(self) -> RawSnapshot | None:
        """每次轮询读取一次状态；返回 None 表示本次不可用（不发布）。"""
        raise NotImplementedError

    def _loop(self) -> None:
        while not self._stop.is_set():
            t = time.monotonic()
            try:
                snap = self.read()
                self._health = "ok"
            except Exception as exc:  # 单次异常降级，不退出线程
                self._health = "degraded"
                self._last_error = str(exc)
                snap = None
            if snap is not None:
                self.publish(snap)
                self._last_published_at = time.monotonic()
            dt = self.interval - (time.monotonic() - t)
            if dt > 0:
                self._stop.wait(dt)