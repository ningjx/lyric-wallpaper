# -*- coding: utf-8 -*-
"""服务配置：dataclass 默认值 + 可选 config.json 覆盖。

默认值与旧 nowplaying_server.py 行为完全一致（端口 9863、仅本机监听）。
运行参数优先级：命令行 > config.json > 默认值。
"""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field, fields
from typing import Any

SERVER_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.dirname(SERVER_DIR)


def _default_data_dir() -> str:
    """歌词/缓存数据目录（仓库内 data/，便于用户手工放置 .lrc）。"""
    return os.path.join(PROJECT_DIR, "data")


@dataclass
class MusicSourceConfig:
    """数据源通用参数。"""
    netease_poll_interval: float = 0.2   # 网易云内存读取周期（秒）
    apple_poll_interval: float = 0.2     # Apple SMTC 读取周期（秒）
    title_recheck_interval: float = 30.0 # 窗口标题兜底重查周期（秒）
    snapshot_max_age: float = 2.0        # 快照超过此时长判源失联（须远大于轮询间隔）


@dataclass
class HttpConfig:
    """服务监听参数。"""
    host: str = "127.0.0.1"
    port: int = 9863
    token: str = ""              # 空 = 不鉴权
    sse_enabled: bool = True
    log_file: str = ""           # 非 TTY 场景写文件日志


@dataclass
class NeteaseHttpConfig:
    """网易云 API 客户端（重试/熔断/单飞）。"""
    timeout: float = 5.0
    retries: int = 3
    fuse_threshold: int = 3          # 连续失败 N 次打开熔断
    fuse_cooldown: float = 60.0      # 熔断冷却秒
    search_cache_ttl: float = 300.0  # 搜索命中缓存 TTL
    search_negative_ttl: float = 15.0
    lyric_cache_ttl: float = 300.0
    lyric_negative_ttl: float = 15.0


@dataclass
class CacheConfig:
    """本地缓存。"""
    data_dir: str = field(default_factory=_default_data_dir)
    lyrics_mem_cap: int = 128        # 内存 LRU 容量


@dataclass
class LyricConfig:
    """歌词解析链。"""
    provider_order: list[str] = field(
        default_factory=lambda: ["local-file", "netease", "qq"])
    total_timeout: float = 8.0       # 整条链上限
    parallel: bool = True            # 多源并行 + 智能选优


@dataclass
class ServerConfig:
    music: MusicSourceConfig = field(default_factory=MusicSourceConfig)
    http: HttpConfig = field(default_factory=HttpConfig)
    netease: NeteaseHttpConfig = field(default_factory=NeteaseHttpConfig)
    cache: CacheConfig = field(default_factory=CacheConfig)
    lyrics: LyricConfig = field(default_factory=LyricConfig)
    source_priority: tuple[str, ...] = ("applemusic", "netease")


def _merge(dst: dict, src: dict) -> None:
    for k, v in src.items():
        if isinstance(v, dict) and isinstance(dst.get(k), dict):
            _merge(dst[k], v)
        else:
            dst[k] = v


def config_from_dict(data: dict) -> ServerConfig:
    """从 dict（config.json）构造配置，白名单字段。"""
    known = {f.name for f in fields(ServerConfig)}
    cfg = {}
    for k in list(known):
        if k in data:
            cfg[k] = data[k]
    config = ServerConfig(**cfg)

    for sub, cls in (
        ("music", MusicSourceConfig), ("http", HttpConfig),
        ("netease", NeteaseHttpConfig), ("cache", CacheConfig),
        ("lyrics", LyricConfig),
    ):
        raw = data.get(sub)
        if not isinstance(raw, dict):
            continue
        names = {f.name for f in fields(cls)}
        kw = {k: v for k, v in raw.items() if k in names}
        setattr(config, sub, cls(**kw))
    return config


def load_config(path: str | None = None) -> ServerConfig:
    cfg = ServerConfig()
    if path and os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                cfg = config_from_dict(json.load(f))
        except FileNotFoundError:
            pass
        except Exception as e:
            from .console import console
            console.log(f"读取配置文件失败（使用默认值）: {e}")
    return cfg