# -*- coding: utf-8 -*-
"""歌词解析编排任务：切歌事件 → 搜索补齐身份 → 歌词链 → 缓存 → 更新状态存储。

只会在「切歌」时执行（不像旧版每个请求都打网络）；/api/lyric 通过
TrackContext.future 等待本次解析完成，避免前端拿到空的竞态。
"""
from __future__ import annotations

import asyncio
from typing import Optional, Sequence

from ..console import console
from ..core.state import TrackInfo, pseudo_track_id
from ..core.stats import Metrics
from ..core.store import StateStore, TrackContext
from .cache import LyricsCache
from .chain import LyricsChain
from .identities import TrackIdentifiers, TrackMatch, normalize_key
from .providers import LyricsResult
from .search import TrackSearcher


class ResolverTask:
    def __init__(self, store: StateStore,
                 searchers: Sequence[TrackSearcher],
                 chain: LyricsChain, cache: LyricsCache,
                 metrics: Optional[Metrics] = None) -> None:
        self.store = store
        self.searchers = list(searchers)
        self.chain = chain
        self.cache = cache
        self.metrics = metrics
        self._inflight: set[asyncio.Task] = set()

    def schedule(self, song: str, author: str, duration: float) -> None:
        """发布新曲目上下文并取消上一首尚未完成的解析。"""
        previous = self.store.track_context
        if previous and not previous.future.done():
            previous.future.set_result(None)
        for pending in self._inflight:
            pending.cancel()
        ctx = self.store.begin_track_context(song, author, duration)
        task = asyncio.create_task(self._resolve(ctx))
        self._inflight.add(task)
        task.add_done_callback(self._inflight.discard)

    async def stop(self) -> None:
        ctx = self.store.track_context
        if ctx and not ctx.future.done():
            ctx.future.set_result(None)
        tasks = tuple(self._inflight)
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)

    # ---- 内部 ----
    async def _resolve(self, ctx: TrackContext) -> None:
        song, author, duration = ctx.song, ctx.author, ctx.duration
        ids = TrackIdentifiers(title=song, artist=author, duration=duration)
        track_info: Optional[TrackInfo] = None
        result: Optional[LyricsResult] = None
        state = "degraded"

        try:
            # 1) 搜索/身份补全（多源：每个源各自解析 ID，选相似度最高的做展示）
            best_sim = -1
            for searcher in self.searchers:
                info = await searcher.search(ids)
                if not info:
                    continue
                vid = str(info.get("id") or "")
                if searcher.vendor == "netease":
                    ids.netease_id = vid
                elif searcher.vendor == "qq":
                    ids.qq_id = vid
                # 各歌词 Provider 据此计算置信度（相似度）
                ids.matches[searcher.vendor] = TrackMatch(
                    title=info.get("title") or song,
                    author=info.get("author") or author,
                    duration=info.get("duration") or 0.0,
                    similarity=info.get("similarity", 0),
                )
                sim = info.get("similarity", 0)
                cand = TrackInfo(
                    id=vid,
                    title=info.get("title") or song,
                    author=info.get("author") or author,
                    album=info.get("album") or "",
                    cover=info.get("cover") or "",
                    source=searcher.vendor)
                if sim > best_sim:
                    best_sim = sim
                    track_info = cand
            if track_info is None:
                # 所有源搜索失败（离线/曲库无）：本地伪 ID，保切歌检测可用
                track_info = TrackInfo(
                    id=pseudo_track_id(song, author),
                    title=song, author=author, source="local")

            # 2) 歌词：本地缓存优先 → Chain 拉取 → 写回缓存
            key = normalize_key(song, author)
            result = self.cache.get(song, author)
            if result is not None:
                state = "from-cache"
            else:
                result = await self.chain.fetch(ids)
                if result is not None:
                    self.cache.put(song, author, result)
                    state = "ok"
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            console.log(f"歌词解析失败（{song} - {author}）: {exc}")
            state = "degraded"
        finally:
            if track_info is None:  # 异常兜底
                track_info = TrackInfo(
                    id=pseudo_track_id(song, author),
                    title=song, author=author, source="local")
            if self.metrics:
                self.metrics.incr("resolve." + state)
            self.store.finish_track_context(
                ctx, ids, track_info, result, state)
