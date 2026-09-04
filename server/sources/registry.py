# -*- coding: utf-8 -*-
"""数据源注册表：统一构建/启动/停止/健康汇聚。

新增播放器源 = 实现 sources/<name>.py 中的 PlayerSource 子类，在这里注册一行。
"""
from __future__ import annotations

from typing import Callable, Dict

from ..config import ServerConfig
from ..core.state import RawSnapshot
from .apple import AppleMusicSource
from .base import PlayerSource
from .netease import NeteaseSource


def build_sources(config: ServerConfig,
                  publish: Callable[[str, RawSnapshot], None]
                  ) -> Dict[str, PlayerSource]:
    return {
        "netease": NeteaseSource(
            lambda snap: publish("netease", snap),
            interval=config.music.netease_poll_interval,
            title_recheck=config.music.title_recheck_interval,
        ),
        "applemusic": AppleMusicSource(
            lambda name, snap: publish(name, snap),
            interval=config.music.apple_poll_interval,
        ),
    }


def start_all(sources: Dict[str, PlayerSource]) -> None:
    for source in sources.values():
        source.start()


def stop_all(sources: Dict[str, PlayerSource]) -> None:
    for source in sources.values():
        source.stop()


def health_map(sources: Dict[str, PlayerSource]) -> Dict[str, str]:
    return {name: src.health() for name, src in sources.items()}