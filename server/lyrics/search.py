# -*- coding: utf-8 -*-
"""搜索器：把 歌名/歌手 解析成某体系内的歌曲 ID 与附加信息。

TrackSearcher 是可插拔的 —— 未来接入 QQ/Kugou 等只需实现一个子类。
内置 NeteaseSearcher 迁自动网云搜索 API（结果带缓存 + 负缓存）。
"""
from __future__ import annotations

import time
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any, Dict, Optional

from .http import NeteaseHttp
from .identities import TrackIdentifiers, normalize_key

SEARCH_URL = "https://music.163.com/api/search/get/web"


class TrackSearcher(ABC):
    vendor: str = ""

    @abstractmethod
    async def search(self, ids: TrackIdentifiers) -> Optional[dict]:
        """返回该体系内识别信息（含 id），无法解析返回 None。"""
        raise NotImplementedError


@dataclass
class _Entry:
    value: Any
    expire: float


class NeteaseSearcher(TrackSearcher):
    vendor = "netease"

    def __init__(self, http: NeteaseHttp, *, hit_ttl: float = 300.0,
                 negative_ttl: float = 15.0) -> None:
        self.http = http
        self._hit_ttl = hit_ttl
        self._negative_ttl = negative_ttl
        self._cache: Dict[tuple, _Entry] = {}

    async def search(self, ids: TrackIdentifiers) -> Optional[dict]:
        key = normalize_key(ids.title, ids.artist)
        entry = self._cache.get(key)
        if entry and entry.expire > time.monotonic():
            return entry.value

        info = await self._fetch(ids)
        ttl = self._hit_ttl if info else self._negative_ttl
        self._cache[key] = _Entry(info, time.monotonic() + ttl)
        return info

    async def _fetch(self, ids: TrackIdentifiers) -> Optional[dict]:
        query = f"{ids.title} {ids.artist}".strip()
        try:
            data = await self.http.get_json(
                SEARCH_URL,
                params={"s": query, "type": 1, "limit": 5},
                key=f"search:{query}")
        except Exception:
            return None
        if not data:
            return None
        songs = (data.get("result") or {}).get("songs") or []
        if not songs:
            return None

        # 选最佳匹配：优先歌名完全相同
        best = songs[0]
        for s in songs:
            if (s.get("name") or "").strip() == ids.title.strip():
                best = s
                break
        album = best.get("album") or {}
        artists = best.get("artists") or []
        return {
            "id": str(best.get("id", "")),
            "title": best.get("name") or ids.title,
            "author": (artists[0].get("name") if artists else "") or ids.artist,
            "album": album.get("name", "") or "",
            "cover": album.get("picUrl", "") or "",
            "duration": round((best.get("duration", 0) or 0) / 1000, 3),
        }