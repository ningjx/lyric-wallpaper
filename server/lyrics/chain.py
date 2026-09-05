# -*- coding: utf-8 -*-
"""歌词解析链：按优先级尝试多个 Provider，带熔断/冷却/负缓存。

稳定目标：任何一个 Provider 挂掉都不拖垮整体 ——
  - 失败 Provider 进入冷却窗口，冷却期内直接跳过；
  - 整条链有总超时上限 total_timeout；
  - 结果短窗负缓存，避免同一首歌反复触发失败的 Provider。

智能选优（parallel=True）：并行跑全部可用源，按「相似度优先、齐全度次之」
挑最佳结果，等价于 Widdit 的 selectBestLyric + selectByScore。
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


def _completeness(res: LyricsResult) -> int:
    """结果齐全度（原词/翻译/逐字各计 1 分，用于相似度相同时的次级排序）。"""
    return (1 if res.lrc else 0) + (1 if res.translated_lyric else 0) \
        + (1 if res.karaoke_lyric else 0)


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
            valid = [r for r in results
                     if isinstance(r, LyricsResult) and r.has_lyric]
            if not valid:
                return None
            # 智能选优：相似度优先，其次看「原词/翻译/逐字」齐全度。
            # local-file 相似度记为 100，天然排最前。
            valid.sort(key=lambda r: (r.similarity, _completeness(r)),
                       reverse=True)
            return valid[0]

        # 顺序兜底：按登记顺序取首个命中
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