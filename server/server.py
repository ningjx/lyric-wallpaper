# -*- coding: utf-8 -*-
"""服务装配与生命周期。

启动: python -m server [--port 9863] [--config config.json]

组件拓扑：
  sources/*（工作线程，阻塞 IO）──call_soon_threadsafe──▶ StateStore（事件循环单写者）
                                                            │
                            ResolverTask（搜索/歌词，异步） ──┤  └─▶ /query /api/lyric /sse
                    hub(SSE) ◀── store 变更回调 ──────────────┘
"""
from __future__ import annotations

import argparse
import asyncio
import logging
import os
import sys

from aiohttp import web

from .config import ServerConfig, load_config
from .console import console
from .core.arbiter import Arbiter
from .core.state import sec_to_human
from .core.store import StateStore
from .core.stats import Metrics
from .lyrics.cache import LyricsCache
from .lyrics.chain import LyricsChain
from .lyrics.http import NeteaseHttp
from .lyrics.providers import (
    FileLyricsProvider, NeteaseLyricsProvider, QQMusicLyricsProvider,
)
from .lyrics.resolver import ResolverTask
from .lyrics.search import NeteaseSearcher, QQMusicSearcher
from .sources.registry import build_sources, start_all, stop_all
from .web.app import create_app, _state_snapshot
from .web.sse import EventHub

PROVIDER_BUILDERS = {
    "local-file": lambda cfg, http, fs: FileLyricsProvider(fs),
    "netease": lambda cfg, http, fs: NeteaseLyricsProvider(http),
    "qq": lambda cfg, http, fs: QQMusicLyricsProvider(http),
}


def parse_args(argv=None) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        prog="python -m server",
        description="lyric-wallpaper Now Playing 异步服务")
    p.add_argument("--port", type=int, default=None, help="监听端口（默认 9863）")
    p.add_argument("--host", default=None, help="监听地址（默认 127.0.0.1）")
    p.add_argument("--config", default=None, help="config.json 路径（可选）")
    p.add_argument("--token", default=None, help="访问令牌（可选，空=不鉴权）")
    p.add_argument("--log-file", default=None, help="非 TTY 场景的日志文件路径")
    return p.parse_args(argv)


def apply_overrides(cfg: ServerConfig, args) -> ServerConfig:
    if args.port is not None:
        cfg.http.port = args.port
    if args.host is not None:
        cfg.http.host = args.host
    if args.token is not None:
        cfg.http.token = args.token
    if args.log_file is not None:
        cfg.http.log_file = args.log_file
    return cfg


def setup_file_logging(cfg: ServerConfig) -> None:
    """非 TTY 场景把日志写到文件（控制台仍保留状态行）。"""
    if not cfg.http.log_file:
        return
    handler = logging.FileHandler(cfg.http.log_file, encoding="utf-8")
    handler.setFormatter(logging.Formatter(
        "%(asctime)s %(levelname)s %(name)s: %(message)s"))
    logging.getLogger().addHandler(handler)
    logging.getLogger().setLevel(logging.INFO)


def build_lyrics_chain(cfg: ServerConfig, http: NeteaseHttp) -> LyricsChain:
    lyrics_dir = os.path.join(cfg.cache.data_dir, "lyrics")
    providers = []
    for name in cfg.lyrics.provider_order:
        builder = PROVIDER_BUILDERS.get(name)
        if not builder:
            console.log(f"未知歌词 Provider: {name}，已跳过")
            continue
        providers.append(builder(cfg, http, lyrics_dir))
    if not providers:
        providers.append(FileLyricsProvider(lyrics_dir))  # 保底：本地文件
    return LyricsChain(
        providers,
        total_timeout=cfg.lyrics.total_timeout,
        parallel=cfg.lyrics.parallel)


async def serve(cfg: ServerConfig) -> None:
    loop = asyncio.get_running_loop()
    metrics = Metrics()
    arbiter = Arbiter(priority=cfg.source_priority)
    store = StateStore(arbiter)

    # ---- 歌词体系（aiohttp 全局 Session + Provider 链） ----
    http = NeteaseHttp(
        timeout=cfg.netease.timeout,
        retries=cfg.netease.retries,
        fuse_threshold=cfg.netease.fuse_threshold,
        fuse_cooldown=cfg.netease.fuse_cooldown)
    chain = build_lyrics_chain(cfg, http)
    cache = LyricsCache(cfg.cache.data_dir,
                        mem_cap=cfg.cache.lyrics_mem_cap)
    searchers = [
        NeteaseSearcher(http, hit_ttl=cfg.netease.search_cache_ttl,
                        negative_ttl=cfg.netease.search_negative_ttl),
        QQMusicSearcher(http, hit_ttl=cfg.netease.search_cache_ttl,
                        negative_ttl=cfg.netease.search_negative_ttl),
    ]
    resolver = ResolverTask(store, searchers, chain, cache, metrics)

    # ---- SSE 与事件接线 ----
    hub = EventHub()
    last = {"song": None, "resolve": "idle"}

    def on_state() -> None:
        res = store.resolved
        song = (res.song, res.author) if res.has_song else None
        rstate = (store.track_context.resolve_state
                  if store.track_context else "idle")
        if (song, rstate) == (last["song"], last["resolve"]):
            return  # 只广播「有效信息变化」，进度帧不推
        last["song"] = song
        last["resolve"] = rstate
        hub.publish({"type": "state", "state": _state_snapshot(store)})

    def on_song(song: str, author: str, duration: float) -> None:
        resolver.schedule(song, author, duration)  # 切歌才解析歌词
        last["song"] = (song, author)
        hub.publish({"type": "song", "state": _state_snapshot(store)})

    store.attach(on_state, on_song)

    # ---- 底部实时状态行（播放中/暂停 + 平台 + 歌名 + 进度） ----
    # 与旧版 MusicMonitor._watch_status 一致：每 0.5s 原地刷新一行，
    # 只展示播放时间/进度，不打断上方切歌日志。
    def _trunc(text: str, limit: int = 36) -> str:
        return text if len(text) <= limit else text[:limit - 1] + "…"

    async def _console_status_loop() -> None:
        while True:
            res = store.resolved
            if res.has_song:
                state = "播放中" if res.playing else "已暂停"
                platform = "Apple Music" if res.source == "applemusic" else "网易云音乐"
                # 歌词获取标注：[词] 已有 / [··] 解析中 / [无] 未获取到
                ctx = store.track_context
                if (ctx is None or ctx.song_key != (res.song, res.author)
                        or ctx.resolve_state == "resolving"):
                    tag = "[··]"
                elif getattr(ctx.lyric_result, "has_lyric", False):
                    tag = "[词]"
                else:
                    tag = "[无]"
                console.set_status(
                    f"{state}  {platform}  {tag}  {_trunc(res.song)}  "
                    f"{sec_to_human(res.progress)}/{sec_to_human(res.duration)}")
            await asyncio.sleep(0.5)

    status_task = asyncio.create_task(_console_status_loop())

    # ---- 数据源（工作线程 → 事件循环桥） ----
    def publish(name: str, snap) -> None:
        loop.call_soon_threadsafe(store.push_source, name, snap)

    sources = build_sources(cfg, publish)
    app = create_app(cfg, store, resolver, metrics, sources, hub, http)

    runner = None
    try:
        start_all(sources)
        runner = web.AppRunner(app)
        await runner.setup()
        await web.TCPSite(runner, cfg.http.host, cfg.http.port).start()

        console.log(f"Now Playing API 异步服务已启动: "
                    f"http://{cfg.http.host}:{cfg.http.port}")
        console.log("  端点: /query (状态)  /api/lyric (歌词)  "
                    "/sse (推送)  /healthz /metrics (诊断)")
        console.log("  歌词源: " + ", ".join(cfg.lyrics.provider_order))
        console.log("  数据源: Apple Music SMTC / 网易云内存读取")
        console.log("按 Ctrl+C 停止")

        await asyncio.Event().wait()
    finally:
        status_task.cancel()
        stop_all(sources)
        if runner is not None:
            await runner.cleanup()
        await http.close()
        console.stop("已停止。")


def main(argv=None) -> None:
    # Windows 控制台默认 GBK，强制 stdout 走 UTF-8，保证中文/ANSI 正常显示
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    args = parse_args(argv)
    cfg = load_config(args.config)
    apply_overrides(cfg, args)
    setup_file_logging(cfg)
    try:
        asyncio.run(serve(cfg))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()