# -*- coding: utf-8 -*-
"""aiohttp 异步客户端：网易云搜索/歌词请求。

特性（对应架构方案的稳定性设计）：
  - 连接池 + keep-alive（全局一个 Session）；
  - 失败重试（1/2/4s 退避，最多 retries 次）；
  - 熔断：连续失败 N 次进入冷却窗口，窗口内直接抛 HttpFuseError（返回 None 语义）；
  - 单飞：同 key 并发请求只发一次网络，其余共享同一个 Future。
"""
from __future__ import annotations

import asyncio
import time
from typing import Any, Dict, Optional

import aiohttp

DEFAULT_HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
    "Referer": "https://music.163.com",
    "Cookie": "appver=2.10.6; os=pc;",
}


class HttpFuseError(Exception):
    """熔断打开期间的异常（调用方按「不可用」处理）。"""


class NeteaseHttp:
    def __init__(self, session: Optional[aiohttp.ClientSession] = None,
                 *, timeout: float = 5.0, retries: int = 3,
                 fuse_threshold: int = 3, fuse_cooldown: float = 60.0,
                 headers: Optional[dict] = None) -> None:
        self._timeout = timeout
        self._retries = retries
        self._fuse_threshold = fuse_threshold
        self._fuse_cooldown = fuse_cooldown
        self._headers = headers or DEFAULT_HEADERS
        self._own_session = session is None
        self._session = session or aiohttp.ClientSession(
            timeout=aiohttp.ClientTimeout(total=timeout),
            headers=self._headers)
        self._fuse_failures = 0
        self._fuse_until = 0.0
        self._inflight: Dict[str, asyncio.Future] = {}

    async def close(self) -> None:
        if self._own_session and not self._session.closed:
            await self._session.close()

    def is_fused(self) -> bool:
        return time.time() < self._fuse_until

    async def get_json(self, url: str, *, params: Optional[dict] = None,
                       headers: Optional[dict] = None,
                       key: Optional[str] = None) -> Any:
        """GET 并解析 JSON；失败/超时返回 None（绝不抛给业务层）。

        key：单飞去重键（搜索按 query、歌词按 song_id），同一时刻同 key
        只发一次网络请求，其余调用方共享同一份结果。
        """
        key = key or url
        if self.is_fused():
            raise HttpFuseError("netease API circuit open")

        loop = asyncio.get_running_loop()
        fut = self._inflight.get(key)
        if fut is None:
            fut = loop.create_future()
            self._inflight[key] = fut
            asyncio.create_task(self._fetch(key, url, params, headers, fut))
        try:
            # shield：wait_for 超时只放弃等待，不取消在途请求
            return await asyncio.wait_for(
                asyncio.shield(fut), timeout=self._timeout + 3)
        except asyncio.TimeoutError:
            return None

    async def _fetch(self, key: str, url: str, params: Optional[dict],
                     headers: Optional[dict], fut: "asyncio.Future") -> None:
        try:
            for attempt in range(self._retries + 1):
                try:
                    async with self._session.get(
                            url, params=params, headers=headers) as resp:
                        if resp.status != 200:
                            raise aiohttp.ClientConnectionError(
                                f"HTTP {resp.status}")
                        data = await resp.json(content_type=None)
                    self._fuse_failures = 0  # 成功清零
                    if not fut.done():
                        fut.set_result(data)
                    return
                except asyncio.CancelledError:
                    raise
                except Exception as exc:
                    last = exc
                    if attempt < self._retries:
                        await asyncio.sleep(min(2 ** attempt, 4))
            self._record_failure()
            if not fut.done():
                fut.set_result(None)
        finally:
            self._inflight.pop(key, None)

    def _record_failure(self) -> None:
        self._fuse_failures += 1
        if self._fuse_failures >= self._fuse_threshold:
            self._fuse_until = time.time() + self._fuse_cooldown
            self._fuse_failures = 0