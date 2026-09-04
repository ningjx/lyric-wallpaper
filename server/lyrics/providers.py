# -*- coding: utf-8 -*-
"""歌词提供者：给定歌曲身份，返回规范化歌词（LyricsResult）。

新增歌词源 = 实现一个 LyricsProvider 子类 + 在链里注册：
  - NeteaseLyricsProvider：网易云官方歌词 API（需 netease_id）；
  - FileLyricsProvider：本地 .lrc 目录（按 `歌名 - 歌手.lrc` / `<netease_id>.lrc` 匹配），
    零网络、离线可用，也是新源接入前的最小样例。
"""
from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path

from .http import NeteaseHttp
from .identities import TrackIdentifiers

LYRIC_URL = "https://music.163.com/api/song/lyric"


@dataclass
class LyricsResult:
    """规范化的歌词结果（/api/lyric 的字段来源，与具体 provider 无关）。"""
    provider: str
    has_lyric: bool
    lrc: str = ""
    translated_lyric: str = ""
    karaoke_lyric: str = ""
    meta: dict = field(default_factory=dict)


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


class NeteaseLyricsProvider(LyricsProvider):
    name = "netease"

    def requires(self) -> tuple[str, ...]:
        return ("netease_id",)

    def __init__(self, http: NeteaseHttp) -> None:
        self.http = http

    async def fetch(self, ids: TrackIdentifiers) -> LyricsResult | None:
        if not ids.netease_id:
            return None
        try:
            data = await self.http.get_json(
                LYRIC_URL,
                params={"id": ids.netease_id, "lv": -1, "kv": -1, "tv": -1},
                key=f"lyric:{ids.netease_id}")
        except Exception:
            return None
        if not data:
            return None
        lrc = (data.get("lrc") or {}).get("lyric", "") or ""
        return LyricsResult(
            provider="netease",
            has_lyric=bool(lrc),
            lrc=lrc,
            translated_lyric=(data.get("tlyric") or {}).get("lyric", "") or "",
            karaoke_lyric=(data.get("klyric") or {}).get("lyric", "") or "",
            meta={"netease_id": ids.netease_id},
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
                continue
            lrc = await asyncio.to_thread(self._read_utf8, path)
            return LyricsResult(
                provider="local-file",
                has_lyric=bool(lrc),
                lrc=lrc,
                meta={"path": str(path)},
            )
        return None

    @staticmethod
    def _read_utf8(path: Path) -> str:
        return path.read_text(encoding="utf-8", errors="replace")