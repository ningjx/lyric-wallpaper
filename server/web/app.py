# -*- coding: utf-8 -*-
"""aiohttp 应用：兼容端点的 HTTP 服务 + 扩展端点（/sse /healthz /metrics）。

契约兼容旧 nowplaying_server：
  GET /query      -> 播放器 + 歌曲状态
  GET /api/lyric  -> 当前歌曲歌词（source 字段为实际命中的 provider 名）
新增：
  GET /sse        -> Server-Sent Events（切歌/状态变化即时推送）
  GET /healthz    -> 组件健康诊断
  GET /metrics    -> 指标快照（请求数/耗时/解析状态）
"""
from __future__ import annotations

import asyncio
import json
import time
from typing import Dict

from aiohttp import web

from ..config import ServerConfig
from ..core.state import lyric_payload, query_payload
from ..core.stats import Metrics
from ..core.store import StateStore
from ..lyrics.http import HttpClient
from ..sources.registry import health_map
from .sse import EventHub


# ============ 响应工具 ============
def _json(data: dict, *, status: int = 200) -> web.Response:
    return web.json_response(data, status=status, dumps=_dumps)

def _dumps(data) -> str:
    return json.dumps(data, ensure_ascii=False)

def _sse_frame(data: dict) -> bytes:
    return f"data: {json.dumps(data, ensure_ascii=False)}\n\n".encode("utf-8")


def _state_snapshot(store: StateStore) -> dict:
    res = store.resolved
    ctx = store.track_context
    track = ctx.track_info if ctx else None
    lyric = ctx.lyric_result if ctx else None
    return {
        "seq": res.seq,
        "source": res.source,
        "hasSong": res.has_song,
        "song": res.song,
        "author": res.author,
        "playing": res.playing,
        "progress": round(res.progress, 3),
        "duration": round(res.duration, 3),
        "resolveState": ctx.resolve_state if ctx else "idle",
        # 搜索/歌词解析完成后才有的附加信息。注意：切歌事件（song 事件）
        # 发出的瞬间这些还没就绪（搜索尚未返回），前端不可依赖其非空；
        # 等「解析完成」的 state 事件到达时，这些字段才完整可用。
        "trackId": getattr(track, "id", "") or "",
        "album": getattr(track, "album", "") or "",
        "cover": getattr(track, "cover", "") or "",
        "hasLyric": bool(getattr(lyric, "has_lyric", False)),
    }


# ============ 应用工厂 ============
def create_app(config: ServerConfig, store: StateStore, resolver,
               metrics: Metrics, sources: Dict, hub: EventHub,
               http: HttpClient) -> web.Application:

    @web.middleware
    async def mw(request, handler):
        metrics.incr("http." + request.path)
        token = config.http.token
        if token:
            got = request.headers.get("X-Token") or request.query.get("token")
            if got != token:
                return _json({"error": "unauthorized"}, status=403)
        resp = await handler(request)
        resp.headers.setdefault("Access-Control-Allow-Origin", "*")
        resp.headers.setdefault("Access-Control-Allow-Methods", "GET, OPTIONS")
        resp.headers.setdefault("Access-Control-Allow-Headers",
                                "X-Token, Content-Type")
        return resp

    app = web.Application(middlewares=[mw])
    app["config"] = config
    app["store"] = store
    app["resolver"] = resolver
    app["metrics"] = metrics
    app["sources"] = sources
    app["hub"] = hub
    app["http"] = http

    app.router.add_get("/", handle_root)
    app.router.add_get("/query", handle_query)
    app.router.add_get("/api/lyric", handle_lyric)
    app.router.add_get("/healthz", handle_healthz)
    app.router.add_get("/metrics", handle_metrics)
    app.router.add_get("/sse", handle_sse)
    app.router.add_options("/{tail:.*}", handle_options)
    return app


# ============ 处理器 ============
async def handle_root(request: web.Request) -> web.Response:
    store = request.app["store"]
    return _json({"service": "lyric-wallpaper-nowplaying",
                  "version": 2, "endpoints": [
                      "/query", "/api/lyric", "/sse", "/healthz", "/metrics"]})


async def handle_query(request: web.Request) -> web.Response:
    store: StateStore = request.app["store"]
    metrics: Metrics = request.app["metrics"]
    t = time.monotonic()
    res = store.resolved
    ctx = store.track_context
    track = None
    if ctx is not None and (ctx.song, ctx.author) == (res.song, res.author):
        track = ctx.track_info
    payload = query_payload(res, track)
    metrics.timing("query", time.monotonic() - t)
    return _json(payload)


async def handle_lyric(request: web.Request) -> web.Response:
    store: StateStore = request.app["store"]
    metrics: Metrics = request.app["metrics"]
    t = time.monotonic()
    res = store.resolved
    song = res.song if res.has_song else ""
    author = res.author if res.has_song else ""
    duration = res.duration if res.has_song else 0.0

    ctx = store.track_context
    result = None
    if ctx is not None and (ctx.song, ctx.author) == (song, author):
        # 解析尚未完成：等它（避免前端拿到空结果），超时则返回当前状态
        if not ctx.future.done():
            try:
                await asyncio.wait_for(ctx.future, timeout=10)
            except asyncio.TimeoutError:
                pass
        result = ctx.lyric_result

    payload = lyric_payload(song, author, duration, result)
    metrics.timing("lyric", time.monotonic() - t)
    return _json(payload)


async def handle_healthz(request: web.Request) -> web.Response:
    store = request.app["store"]
    metrics = request.app["metrics"]
    sources = request.app["sources"]
    netease = sources.get("netease")
    offset_state = (netease.resolver.offset_state()
                    if netease is not None else "n/a")
    healthy = offset_state != "failed"
    return _json({
        "status": "ok" if healthy else "degraded",
        "offset": offset_state,
        "sources": health_map(sources),
        "song": store.resolved.song if store.resolved.has_song else "",
    })


async def handle_metrics(request: web.Request) -> web.Response:
    store = request.app["store"]
    metrics = request.app["metrics"]
    ctx = store.track_context
    return _json({
        "metrics": metrics.snapshot(),
        "current": _state_snapshot(store),
        "subscribers": request.app["hub"].subscriber_count,
    })


async def handle_sse(request: web.Request) -> web.Response:
    hub: EventHub = request.app["hub"]
    store: StateStore = request.app["store"]
    q = hub.subscribe()
    resp = web.StreamResponse(
        status=200,
        headers={
            "Content-Type": "text/event-stream",
            "Cache-Control": "no-store",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        })
    await resp.prepare(request)
    try:
        # 初始快照：连接即得当前状态
        await resp.write(_sse_frame({"type": "snapshot",
                                     "state": _state_snapshot(store)}))
        while True:
            try:
                payload = await asyncio.wait_for(q.get(), timeout=25)
            except asyncio.TimeoutError:
                await resp.write(_sse_frame({"type": "ping"}))
                continue
            await resp.write(_sse_frame(payload))
            if request.transport is None or request.transport.is_closing():
                break
    except (ConnectionResetError, asyncio.CancelledError):
        pass
    finally:
        hub.unsubscribe(q)
    return resp


async def handle_options(request: web.Request) -> web.Response:
    return web.Response(status=204, headers={
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Methods": "GET, OPTIONS",
        "Access-Control-Allow-Headers": "X-Token, Content-Type",
        "Access-Control-Max-Age": "86400",
    })