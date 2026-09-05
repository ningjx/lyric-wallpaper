# -*- coding: utf-8 -*-
"""歌词提供者：给定歌曲身份，返回规范化歌词（LyricsResult）。

新增歌词源 = 实现一个 LyricsProvider 子类 + 在链里注册：
  - NeteaseLyricsProvider：网易云歌词（eapi 接口，需 netease_id）；
  - QQMusicLyricsProvider：QQ 音乐歌词（原词 + 翻译，需 qq_id）；
  - FileLyricsProvider：本地 .lrc 目录（零网络、离线可用）。

统一约定：搜索阶段（search.py）已把各源的匹配结果写进 ids.extra[vendor]，
包含 title/author/duration/similarity；Provider 只负责按其 ID 取歌词，并把
这些匹配信息原样回填到 LyricsResult，供上层（智能选优）打分。
"""
from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path

from .eapi import eapi_encrypt, USER_AGENT
from .http import NeteaseHttp
from .identities import TrackIdentifiers
from .search import _parse_jsonp
from .similarity import EXACT_MATCH_THRESHOLD

NETEASE_LYRIC_URL = "https://interface3.music.163.com/eapi/song/lyric/v1"
QQ_LYRIC_URL = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"


def _match_ok(ids: TrackIdentifiers, similarity: int) -> bool:
    """相似度是否达到可用阈值（歌手缺失时放宽，与 Widdit 一致）。"""
    threshold = 75 if not (ids.artist or "").strip() else EXACT_MATCH_THRESHOLD
    return similarity >= threshold


@dataclass
class LyricsResult:
    """规范化的歌词结果（/api/lyric 的字段来源，与具体 provider 无关）。"""
    provider: str
    has_lyric: bool
    lrc: str = ""
    translated_lyric: str = ""
    karaoke_lyric: str = ""
    meta: dict = field(default_factory=dict)
    # 命中歌曲的元信息 + 与本地身份的匹配分（供智能选优）
    title: str = ""
    author: str = ""
    duration: float = 0.0
    similarity: int = 0


class LyricsProvider(ABC):
    name: str = ""

    def requires(self) -> tuple[str, ...]:
        """依赖哪个 ID 才能参与；空元组 = 只靠 歌名/歌手 即可。"""
        return ()

    def cooldown(self) -> float:
        """单次失败后的冷却秒（链层面统一节流，避免坏源拖慢请求）。"""
        return 60.0

    @abstractmethod
    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        """无歌词 / 无法解析返回 None。"""
        raise NotImplementedError


def _empty_result(provider: str, ids: TrackIdentifiers,
                  similarity: int) -> LyricsResult:
    """构造「无词但保留匹配分」的结果，便于上层在多个源之间比较。"""
    return LyricsResult(provider=provider, has_lyric=False,
                        similarity=similarity)


class NeteaseLyricsProvider(LyricsProvider):
    name = "netease"
    _vendor = "netease"

    def requires(self) -> tuple[str, ...]:
        return ("netease_id",)

    def __init__(self, http: NeteaseHttp) -> None:
        self.http = http

    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        if not ids.netease_id:
            return None
        info = ids.extra.get("netease", {})
        similarity = info.get("similarity", 0)
        if not _match_ok(ids, similarity):
            return _empty_result("netease", ids, similarity)

        data = {
            "id": ids.netease_id, "cp": "false", "lv": "0", "kv": "0",
            "tv": "0", "rv": "0", "yv": "0", "ytv": "0", "yrv": "0",
            "csrf_token": "",
        }
        params = eapi_encrypt(NETEASE_LYRIC_URL, data)
        try:
            resp = await self.http.post_form(
                NETEASE_LYRIC_URL,
                data={"params": params},
                headers={"User-Agent": USER_AGENT,
                         "Referer": "https://music.163.com/"},
                key=f"lyric:netease:{ids.netease_id}")
        except Exception:
            return None
        if not resp or resp.get("code") != 200 or "lrc" not in resp:
            return _empty_result("netease", ids, similarity)

        lrc = (resp.get("lrc") or {}).get("lyric", "") or ""
        trans = (resp.get("tlyric") or {}).get("lyric", "") or ""
        kara = (resp.get("yrc") or {}).get("lyric", "") or ""
        return LyricsResult(
            provider="netease",
            has_lyric=bool(lrc),
            lrc=lrc, translated_lyric=trans, karaoke_lyric=kara,
            title=info.get("title", ""), author=info.get("author", ""),
            duration=info.get("duration", 0.0), similarity=similarity,
            meta={"netease_id": ids.netease_id},
        )


class QQMusicLyricsProvider(LyricsProvider):
    name = "qq"
    _vendor = "qq"

    def requires(self) -> tuple[str, ...]:
        return ("qq_id",)

    def __init__(self, http: NeteaseHttp) -> None:
        self.http = http

    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        if not ids.qq_id:
            return None
        info = ids.extra.get("qq", {})
        similarity = info.get("similarity", 0)
        if not _match_ok(ids, similarity):
            return _empty_result("qq", ids, similarity)

        params = {
            "musicid": ids.qq_id, "g_tk": "5381", "loginUin": "0",
            "hostUin": "0", "format": "json", "inCharset": "utf8",
            "outCharset": "utf8", "notice": "0", "platform": "yqq",
            "needNewCode": "0", "nobase64": "1",
        }
        try:
            text = await self.http.get_text(
                QQ_LYRIC_URL, params=params,
                headers={"Referer": "https://y.qq.com/"},
                key=f"lyric:qq:{ids.qq_id}")
        except Exception:
            return None
        resp = _parse_jsonp(text)
        if not resp:
            return _empty_result("qq", ids, similarity)

        code = resp.get("code")
        lrc = resp.get("lyric", "") or ""
        trans = resp.get("trans", "") or ""
        # code -1901 表示该歌曲本身无歌词
        has = bool(lrc) and code != -1901
        return LyricsResult(
            provider="qq",
            has_lyric=has,
            lrc=lrc if has else "",
            translated_lyric=trans,
            title=info.get("title", ""), author=info.get("author", ""),
            duration=info.get("duration", 0.0), similarity=similarity,
            meta={"qq_id": ids.qq_id},
        )


class FileLyricsProvider(LyricsProvider):
    name = "local-file"

    def __init__(self, lyrics_dir: str | Path) -> None:
        self._dir = Path(lyrics_dir)
        self._dir.mkdir(parents=True, exist_ok=True)

    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        title = ids.title.strip()
        artist = ids.artist.strip()
        candidates = [f"{title} - {artist}.lrc", f"{title}.lrc"]
        if ids.netease_id:
            candidates.insert(0, f"{ids.netease_id}.lrc")
        for name in candidates:
            path = self._dir / name
            if not path.is_file():
                continue
            lrc = await asyncio.to_thread(self._read_utf8, path)
            return LyricsResult(
                provider="local-file",
                has_lyric=bool(lrc),
                lrc=lrc,
                title=title, author=artist,
                similarity=100,  # 本地文件视为精确命中
                meta={"path": str(path)},
            )
        return None

    @staticmethod
    def _read_utf8(path: Path) -> str:
        return path.read_text(encoding="utf-8", errors="replace")