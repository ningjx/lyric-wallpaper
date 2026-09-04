# -*- coding: utf-8 -*-
"""歌词解析链：按优先级尝试多个 Provider，带熔断/冷却/负缓存。

稳定目标：任何一个 Provider 挂掉都不拖垮整体 ——
  - 失败 Provider 进入冷却窗口，冷却期内直接跳过；
  - 整条链有总超时上限 total_timeout；
  - 结果短窗负缓存，避免同一首歌反复触发失败的 Provider。
"""
from __future__ import annotations

import asyncio
import time
from typing import Any, List, Optional, Sequence

from .identities import TrackIdentifiers, normalize_key
from .providers import LyricsProvider, LyricsResult


class _Entry:
    __slots__ = ("value", "expire")

    def __init__(self, value: Any, expire: float) -> None:
        self.value = value
        self.expire = expire


class LyricsChain:
    def __init__(self, providers: Sequence[LyricsProvider], *,
                 total_timeout: float = 8.0, parallel: bool = False,
                 negative_ttl: float = 15.0, cooldown_cap: float = 15.0,
                 cooldown_ttl: float = 300.0) -> None:
        self._providers = list(providers)
        self._total_timeout = total_timeout
        self._parallel = parallel
        self._negative_ttl = negative_ttl
        self._cooldown_cap = cooldown_cap
        self._cooldown_ttl = cooldown_ttl
        self._cooldown_until: dict[str, float] = {}
        self._neg_cache: dict[tuple, _Entry] = {}

    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        """对一条歌曲身份跑整条链；返回首个命中的 LyricsResult 或 None。"""
        key = normalize_key(ids.title, ids.artist)
        hit = self._neg_cache.get(key)
        if hit and hit.expire > time.monotonic():
            return hit.value
        try:
            result = await asyncio.wait_for(
                self._fetch_all(ids), timeout=self._total_timeout)
        except (asyncio.TimeoutError, asyncio.CancelledError):
            result = None
        self._neg_cache[key] = _Entry(
            result, time.monotonic() + (self._cooldown_ttl
                                        if result else self._negative_ttl))
        return result

    async def _fetch_all(self, ids: TrackIdentifiers) -> LyricsResult | None:
        now = time.monotonic()
        eligible = [
            p for p in self._providers
            if self._eligible(p, ids)
            and now >= self._cooldown_until.get(p.name, 0.0)
        ]
        if not eligible:
            return None

        if self._parallel:
            results = await asyncio.gather(
                *(self._safe_fetch(p, ids) for p in eligible),
                return_exceptions=True)
            for res in results:
                if isinstance(res, LyricsResult) and res.has_lyric:
                    return res
            return None

        # 顺序兜底：默认按登记顺序取首个命中
        for p in eligible:
            res = await self._safe_fetch(p, ids)
            if res is not None and res.has_lyric:
                return res
        return None

    async def _safe_fetch(self, provider: LyricsProvider,
                          ids: TrackIdentifiers) -> LyricsResult | None:
        try:
            return await asyncio.wait_for(
                provider.fetch(ids), timeout=self._total_timeout)
        except Exception:
            self._cooldown_until[provider.name] = time.monotonic() + min(
                provider.cooldown(), self._cooldown_cap)
            return None

    @staticmethod
    def _eligible(provider: LyricsProvider, ids: TrackIdentifiers) -> bool:
        for field in provider.requires():
            if not getattr(ids, field, None):
                return False
        return True