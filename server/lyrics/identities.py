# -*- coding: utf-8 -*-
"""歌曲身份：多 Provider 共用的「通行证」。

Search 负责把 歌名/歌手 解析成某个体系内的 ID（netease_id / qq_id 等）与
匹配元信息（TrackMatch）；LyricsProvider 只认 ID，并结合 TrackMatch 判断
该源的可信度。新增歌词源时，只需在 requires() 声明依赖哪个 ID。
"""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class TrackMatch:
    """某歌词源搜索到的「最佳匹配」元信息 + 与本地身份的相似度。"""
    title: str = ""
    author: str = ""
    duration: float = 0.0
    similarity: int = 0


@dataclass
class TrackIdentifiers:
    title: str
    artist: str
    album: str = ""
    duration: float = 0.0
    netease_id: str | None = None
    qq_id: str | None = None
    # 各源（vendor -> TrackMatch）的搜索匹配结果；Provider 据此回填置信度
    matches: dict[str, TrackMatch] = field(default_factory=dict)


def normalize_key(title: str, artist: str) -> tuple[str, str]:
    """跨 Provider 去重的规范化缓存 key（与来源无关）。"""
    return (title.strip().lower(), artist.strip().lower())