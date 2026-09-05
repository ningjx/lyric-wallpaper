# -*- coding: utf-8 -*-
"""搜索器：把 歌名/歌手 解析成某体系内的歌曲 ID 与附加信息。

TrackSearcher 是可插拔的 —— 已接入 NeteaseSearcher（eapi）与 QQMusicSearcher。
每个搜索器用 SongMatchingUtil 的相似度算法在候选里挑最佳匹配，并在返回的
dict 里附带 `similarity`（0-100），供上层（resolver / 智能选优）判断是否可信。
"""
from __future__ import annotations

import json
import re
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any, Dict, Optional

from .eapi import eapi_encrypt, USER_AGENT
from .http import NeteaseHttp
from .identities import TrackIdentifiers, normalize_key
from .similarity import calculate_similarity, EXACT_MATCH_THRESHOLD

NETEASE_SEARCH_URL = "https://interface3.music.163.com/eapi/search/get"
QQ_SEARCH_URL = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp"


class TrackSearcher(ABC):
    vendor: str = ""

    @abstractmethod
    async def search(self, ids: TrackIdentifiers) -> Optional[dict]:
        """返回该体系内识别信息（含 id/similarity），无法解析返回 None。"""
        raise NotImplementedError


@dataclass
class _Entry:
    value: Any
    expire: float


def _match_threshold(ids: TrackIdentifiers) -> int:
    """歌手名缺失时适当降低阈值（与 Widdit 一致）。"""
    return 75 if not (ids.artist or "").strip() else EXACT_MATCH_THRESHOLD


def _pick_best(local_title: str, local_artist: str,
               items: list[dict],
               title_key: str, artists_key: str,
               artists_name: str) -> Optional[dict]:
    """在候选里按相似度挑最佳匹配；返回附带 similarity 的 dict，无候选返回 None。"""
    if not items:
        return None
    best = None
    best_sim = -1
    for it in items:
        title = (it.get(title_key) or "").strip()
        artists = it.get(artists_key) or []
        author = " / ".join((a.get(artists_name) or "") for a in artists).strip()
        sim = calculate_similarity(local_title, local_artist, title, author)
        if sim > best_sim:
            best_sim = sim
            best = (it, title, author)
        if sim >= 100:
            break
    it, title, author = best
    result = dict(it)
    result["_similarity"] = best_sim
    result["_title"] = title
    result["_author"] = author
    return result


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
        data = {
            "s": query, "limit": "5", "offset": "0", "type": "1",
            "csrf_token": "",
        }
        params = eapi_encrypt(NETEASE_SEARCH_URL, data)
        try:
            resp = await self.http.post_form(
                NETEASE_SEARCH_URL,
                data={"params": params},
                headers={"User-Agent": USER_AGENT,
                         "Referer": "https://music.163.com/"},
                key=f"search:netease:{query}")
        except Exception:
            return None
        if not resp or resp.get("code") != 200:
            return None
        songs = ((resp.get("result") or {}).get("songs")) or []
        best = _pick_best(ids.title, ids.artist, songs, "name", "artists", "name")
        if best is None:
            return None
        album = best.get("album") or {}
        return {
            "id": str(best.get("id", "")),
            "title": best["_title"] or ids.title,
            "author": best["_author"] or ids.artist,
            "album": album.get("name", "") or "",
            "cover": album.get("picUrl", "") or "",
            "duration": round((best.get("duration", 0) or 0) / 1000, 3),
            "similarity": best["_similarity"],
        }


class QQMusicSearcher(TrackSearcher):
    vendor = "qq"

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
        params = {
            "ct": "24", "qqmusic_ver": "1298", "remoteplace": "txt.yqq.center",
            "t": "0", "aggr": "1", "cr": "1", "catZhida": "1", "lossless": "0",
            "flag_qc": "0", "p": "1", "n": "8", "w": query, "g_tk": "5381",
            "loginUin": "0", "hostUin": "0", "format": "json",
            "inCharset": "utf8", "outCharset": "utf-8", "notice": "0",
            "platform": "yqq", "needNewCode": "0",
        }
        try:
            text = await self.http.get_text(
                QQ_SEARCH_URL, params=params,
                headers={"Referer": "https://y.qq.com/"},
                key=f"search:qq:{query}")
        except Exception:
            return None
        resp = _parse_jsonp(text)
        if not resp or resp.get("code") != 0:
            return None
        songs = (((resp.get("data") or {}).get("song") or {}).get("list")) or []
        best = _pick_best(ids.title, ids.artist, songs, "songname", "singer", "name")
        if best is None:
            return None
        return {
            "id": str(best.get("songid", "")),
            "title": best["_title"] or ids.title,
            "author": best["_author"] or ids.artist,
            "album": best.get("albumname", "") or "",
            "cover": _qq_cover(best.get("albummid", "")),
            "duration": round((best.get("interval", 0) or 0), 3),
            "similarity": best["_similarity"],
        }


def _qq_cover(albummid: str) -> str:
    return (f"https://y.qq.com/music/photo_new/T002R500x500M000"
            f"{albummid}_1.jpg") if albummid else ""


def _parse_jsonp(text: Optional[str]) -> Any:
    """兼容 JSONP 包裹：返回纯 JSON 解析结果。"""
    if not text:
        return None
    text = text.strip()
    if text.startswith("{"):
        try:
            return json.loads(text)
        except Exception:
            return None
    m = re.search(r"\((\{.*\})\)", text, re.DOTALL)
    if m:
        try:
            return json.loads(m.group(1))
        except Exception:
            return None
    return None