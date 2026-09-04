# -*- coding: utf-8 -*-
"""歌词本地缓存：内存 LRU + 磁盘持久化（=「下载歌词」）。"""

from __future__ import annotations

import hashlib
import json
from collections import OrderedDict
from pathlib import Path
from typing import Dict, Optional

from .identities import normalize_key
from .providers import LyricsResult


class LyricsCache:
    def __init__(self, data_dir: str, mem_cap: int = 128) -> None:
        self._mem: "OrderedDict[tuple, LyricsResult]" = OrderedDict()
        self._cap = max(8, mem_cap)
        self._disk = Path(data_dir) / "lyrics"
        self._disk.mkdir(parents=True, exist_ok=True)

    # ---- 内存 LRU ----
    def get(self, title: str, artist: str) -> Optional[LyricsResult]:
        key = normalize_key(title, artist)

        if key in self._mem:
            self._mem.move_to_end(key)
            return self._mem[key]

        # 磁盘兜底（进程重启后仍可离线出歌词）
        res = self._load_disk(key)
        if res is not None:
            self._mem[key] = res
            self._trim()
        return res

    def put(self, title: str, artist: str, result: LyricsResult) -> None:
        key = normalize_key(title, artist)
        self._mem[key] = result
        self._mem.move_to_end(key)
        self._trim()
        try:
            path = self._disk_file(key)
            data = {
                "provider": result.provider,
                "has_lyric": result.has_lyric,
                "lrc": result.lrc,
                "translated_lyric": result.translated_lyric,
                "karaoke_lyric": result.karaoke_lyric,
                "meta": result.meta,
            }
            path.write_text(
                json.dumps(data, ensure_ascii=False), encoding="utf-8")
        except Exception:
            pass  # 磁盘写入失败不影响内存命中

    # ---- 内部 ----
    def _trim(self) -> None:
        while len(self._mem) > self._cap:
            self._mem.popitem(last=False)

    def _disk_file(self, key: tuple[str, str]) -> Path:
        h = hashlib.sha1(f"{key[0]}|{key[1]}".encode("utf-8")).hexdigest()[:16]
        return self._disk / f"{h}.json"

    def _load_disk(self, key: tuple[str, str]) -> Optional[LyricsResult]:
        path = self._disk_file(key)
        if not path.is_file():
            return None
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            return LyricsResult(
                provider=data.get("provider", "cache"),
                has_lyric=bool(data.get("has_lyric")),
                lrc=data.get("lrc", ""),
                translated_lyric=data.get("translated_lyric", ""),
                karaoke_lyric=data.get("karaoke_lyric", ""),
                meta=data.get("meta") or {},
            )
        except Exception:
            return None