# -*- coding: utf-8 -*-
"""轻量指标收集：计数器 + 最近耗时分位数，供 /metrics 输出。

刻意不用 prometheus 客户端（本地小服务，两个 dict 就够）。
"""
from __future__ import annotations

import statistics
import time
from collections import defaultdict, deque


class Metrics:
    def __init__(self, latency_window: int = 200) -> None:
        self._counters: dict[str, int] = defaultdict(int)
        self._timings: dict[str, deque] = defaultdict(
            lambda: deque(maxlen=latency_window))
        self._started = time.time()

    def incr(self, name: str, n: int = 1) -> None:
        self._counters[name] += n

    def timing(self, name: str, seconds: float) -> None:
        self._timings[name].append(seconds * 1000.0)

    def snapshot(self) -> dict:
        out = {"uptime_sec": round(time.time() - self._started, 1),
               "counters": dict(self._counters)}
        for name, samples in self._timings.items():
            if not samples:
                continue
            out.setdefault("timings", {})[name] = {
                "count": len(samples),
                "mean_ms": round(statistics.mean(samples), 3),
                "p50_ms": round(statistics.median(samples), 3),
                "p95_ms": round(sorted(samples)[int(len(samples) * 0.95)], 3)
                if len(samples) > 4 else None,
            }
        return out