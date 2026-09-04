# -*- coding: utf-8 -*-
"""状态数据类：数据源快照、解析后的播放器状态、对外负载构造。

契约与旧 nowplaying_server.py 完全一致（/query 与 /api/lyric 的 JSON 结构）。
"""
from __future__ import annotations

import time
import zlib
from dataclasses import dataclass, field


def sec_to_human(sec: float) -> str:
    """秒 -> MM:SS 字符串"""
    sec = max(0, int(sec))
    return f"{sec // 60:02d}:{sec % 60:02d}"


@dataclass
class RawSnapshot:
    """单个数据源读到的播放状态快照（线程侧产物，无锁传递）。"""
    source: str                # "netease" | "applemusic"
    playing: bool
    progress: float
    duration: float
    song: str
    author: str
    has_song: bool
    ts: float = field(default_factory=time.monotonic)

    @classmethod
    def empty(cls, source: str) -> "RawSnapshot":
        return cls(source=source, playing=False, progress=0.0,
                   duration=0.0, song="", author="", has_song=False)


@dataclass
class TrackInfo:
    """歌曲附加信息（搜索补全：id/专辑/封面）；搜索失败时为本地伪 ID。"""
    id: str
    title: str
    author: str
    album: str = ""
    cover: str = ""
    source: str = ""            # 提供者 tag（netease 等）


def pseudo_track_id(song: str, author: str) -> str:
    """搜索失败（离线等）时用标题哈希生成稳定伪 ID，保证切歌检测仍可用。"""
    return "local-" + str(zlib.crc32(f"{song}|{author}".encode("utf-8")))


def query_payload(res: "ResolvedPlayer", track: TrackInfo | None) -> dict:
    """构造 GET /query 响应（结构与旧服务逐字段一致）。"""
    if not res.has_song:
        return {
            "player": {
                "hasSong": False, "isPaused": False, "volumePercent": 0,
                "seekbarCurrentPosition": 0, "seekbarCurrentPositionHuman": "00:00",
                "statePercent": 0, "likeStatus": "false", "repeatType": "list",
            },
            "track": {
                "id": "", "title": "", "author": "", "album": "", "cover": "",
                "duration": 0, "durationHuman": "00:00", "url": "",
                "isVideo": False, "isAdvertisement": False, "inLibrary": False,
            },
        }

    progress = res.progress
    duration = res.duration

    if track is not None:
        track_id = track.id
        album = track.album
        cover = track.cover
    else:
        track_id = pseudo_track_id(res.song, res.author)
        album = ""
        cover = ""

    return {
        "player": {
            "hasSong": True,
            "isPaused": not res.playing,
            "volumePercent": 100,
            "seekbarCurrentPosition": round(progress, 3),
            "seekbarCurrentPositionHuman": sec_to_human(progress),
            "statePercent": round(progress / duration * 100, 1) if duration > 0 else 0,
            "likeStatus": "false",
            "repeatType": "list",
        },
        "track": {
            "id": track_id,
            "title": res.song,
            "author": res.author,
            "album": album,
            "cover": cover,
            "duration": round(duration, 3),
            "durationHuman": sec_to_human(duration),
            "url": f"https://music.163.com/#/song?id={track_id}" if track_id else "",
            "isVideo": False,
            "isAdvertisement": False,
            "inLibrary": False,
        },
    }


def _lyric_empty(title: str, author: str, duration: float) -> dict:
    return {
        "source": "", "title": title, "author": author,
        "duration": round(duration, 3),
        "hasLyric": False, "hasTranslatedLyric": False, "hasKaraokeLyric": False,
        "lrc": "", "translatedLyric": "", "karaokeLyric": "",
    }


def lyric_payload(title: str, author: str, duration: float,
                  result: "LyricsResult | None") -> dict:
    """构造 GET /api/lyric 响应（结构与旧服务一致；source 为实际命中 provider）。"""
    if result is None or not result.has_lyric:
        return _lyric_empty(title, author, duration)
    return {
        "source": result.provider,
        "title": title, "author": author,
        "duration": round(duration, 3),
        "hasLyric": True,
        "hasTranslatedLyric": bool(result.translated_lyric),
        "hasKaraokeLyric": bool(result.karaoke_lyric),
        "lrc": result.lrc,
        "translatedLyric": result.translated_lyric,
        "karaokeLyric": result.karaoke_lyric,
    }