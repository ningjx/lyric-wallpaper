# -*- coding: utf-8 -*-
"""纯逻辑单元测试：仲裁 / 缓存 / 歌词链 / 熔断 / 负载构造。

不依赖网易云/Apple 数据源；异步片段用 asyncio.run 包裹（无需 pytest-asyncio）。
"""
from __future__ import annotations

import asyncio

from server.core.arbiter import Arbiter
from server.core.state import (
    RawSnapshot, TrackInfo, lyric_payload, query_payload,
)
from server.core.store import ResolvedPlayer, StateStore
from server.lyrics.chain import LyricsChain
from server.lyrics.cache import LyricsCache
from server.lyrics.http import HttpFuseError, HttpClient
from server.lyrics.identities import TrackIdentifiers
from server.lyrics.providers import (
    FileLyricsProvider, LyricsProvider, LyricsResult,
)
from server.lyrics.similarity import (
    calculate_similarity, EXACT_MATCH_THRESHOLD,
)


# ============ 仲裁 ============
def snap(source, playing, song="歌", author="手", **kw):
    kw.setdefault("progress", 1.0)
    kw.setdefault("duration", 60.0)
    return RawSnapshot(source=source, playing=playing, song=song,
                       author=author, has_song=True, **kw)


def test_arbiter_no_snapshot_returns_none():
    assert Arbiter().pick({}, {}) is None


def test_arbiter_playing_beats_paused():
    arb = Arbiter(priority=("applemusic", "netease"))
    snaps = {"a": snap("netease", playing=False),
             "b": snap("applemusic", playing=True)}
    best = arb.pick(snaps, {"netease": 1.0, "applemusic": 2.0})
    assert best is not None and best.playing


def test_arbiter_drops_stale_snapshot():
    import time
    arb = Arbiter()
    stale = snap("netease", playing=True, ts=time.monotonic() - 10)
    assert arb.pick({"netease": stale}, {}, now=time.monotonic()) is None


def test_arbiter_recent_transition_wins():
    arb = Arbiter()
    snaps = {"a": snap("netease", playing=True),
             "b": snap("applemusic", playing=True)}
    transitions = {"netease": 1.0, "applemusic": 5.0}
    assert arb.pick(snaps, transitions) is snaps["b"]


# ============ 状态存储（推快照→仲裁→变更回调） ============
def test_store_resolves_and_notifies():
    store = StateStore()
    events = {"state": 0, "song": 0}
    def on_state():
        events["state"] += 1
    def on_song(song, author, duration):
        events["song"] += 1
    store.attach(on_state, on_song)

    store.push_source("netease", snap("netease", playing=True))
    assert store.resolved.has_song
    assert store.resolved.song == "歌"
    assert events["song"] == 1

    # 同歌常规进度帧：不触发切歌，只触发状态更新
    events["song"] = 0
    store.push_source("netease", snap("netease", playing=True, progress=5.0))
    assert events["song"] == 0
    assert store.resolved.progress == 5.0


# ============ /query 负载构造 ============
def test_query_payload_no_song():
    p = query_payload(ResolvedPlayer.empty(), None)
    assert p["player"]["hasSong"] is False
    assert p["track"]["title"] == ""


def test_query_payload_with_track():
    res = ResolvedPlayer(has_song=True, playing=True, progress=12.3,
                         duration=212.5, song="随机漫步", author="陶喆")
    track = TrackInfo(id="554191378", title="随机漫步", author="陶喆",
                      album="专辑", cover="url", source="netease")
    p = query_payload(res, track)
    assert p["player"]["hasSong"] is True
    assert p["player"]["isPaused"] is False
    assert p["track"]["id"] == "554191378"
    assert p["track"]["title"] == "随机漫步"
    assert p["track"]["url"] == "https://music.163.com/#/song?id=554191378"


def test_query_payload_pseudo_id_when_no_track():
    res = ResolvedPlayer(has_song=True, playing=True, progress=1.0,
                         duration=10.0, song="本地歌", author="小红")
    p = query_payload(res, None)
    assert p["track"]["id"].startswith("local-")


# ============ /api/lyric 负载构造 ============
def test_lyric_payload_with_provider():
    result = LyricsResult(provider="local-file", has_lyric=True,
                          lrc="[00:00.00] 测试")
    p = lyric_payload("歌", "手", 60.0, result)
    assert p["hasLyric"] is True
    assert p["source"] == "local-file"     # 实际命中 provider
    assert p["lrc"] == "[00:00.00] 测试"


def test_lyric_payload_empty():
    p = lyric_payload("歌", "手", 60.0, None)
    assert p["hasLyric"] is False


# ============ 歌词缓存（内存 + 磁盘） ============
def test_lyric_cache_memory_and_disk(tmp_path):
    cache = LyricsCache(str(tmp_path), mem_cap=8)
    assert cache.get("歌", "手") is None

    cache.put("歌", "手", LyricsResult(
        provider="netease", has_lyric=True, lrc="[00:00.00] 嗨"))
    assert cache.get("歌", "手").lrc == "[00:00.00] 嗨"

    # 新实例从磁盘读出（=「下载歌词」持久化生效）
    cache2 = LyricsCache(str(tmp_path), mem_cap=8)
    assert cache2.get("歌", "手").provider == "netease"


def test_lyric_cache_lru_evicts(tmp_path):
    cache = LyricsCache(str(tmp_path), mem_cap=8)
    for i in range(20):
        cache.put(f"歌{i}", "手", LyricsResult(provider="x", has_lyric=True,
                                               lrc="L"))
    # 内存 LRU 有上限（磁盘仍可兜底读取，属于持久化特性）
    assert len(cache._mem) == 8
    assert cache.get("歌19", "手") is not None


# ============ 歌词链（兜底 + 冷却 + 负缓存） ============
class _StubProvider(LyricsProvider):
    def __init__(self, name, result, requires=(), cooldown=60.0):
        self.name = name
        self._result = result
        self._requires = requires
        self._cooldown_ = cooldown
        self.calls = 0

    def requires(self):
        return self._requires

    def cooldown(self):
        return self._cooldown_

    async def fetch(self, ids):
        self.calls += 1
        if isinstance(self._result, Exception):
            raise self._result
        return self._result(ids)


def _ok(provider="x", sim=100):
    return lambda ids: LyricsResult(provider=provider, has_lyric=True,
                                    lrc="L", similarity=sim)


def test_chain_falls_back_on_failure():
    chain = LyricsChain([
        _StubProvider("bad", RuntimeError("boom"), cooldown=5.0),
        _StubProvider("good", _ok("good")),
    ], total_timeout=5.0)
    ids = TrackIdentifiers(title="歌", artist="手")
    res = asyncio.run(chain.fetch(ids))
    assert res is not None and res.provider == "good"


def test_chain_skips_cooled_down_provider():
    bad = _StubProvider("bad", RuntimeError("boom"), cooldown=5.0)
    good = _StubProvider("good", _ok("good"))
    chain = LyricsChain([bad, good], total_timeout=5.0)
    ids = TrackIdentifiers(title="歌", artist="手")

    assert asyncio.run(chain.fetch(ids)).provider == "good"
    assert bad.calls == 1  # 第一次失败进入冷却
    asyncio.run(chain.fetch(ids))
    assert bad.calls == 1  # 冷却期内不再尝试坏源


def test_chain_negative_cache_short_circuit():
    bad = _StubProvider("bad", RuntimeError("boom"), cooldown=0.0)
    chain = LyricsChain([bad], total_timeout=5.0, negative_ttl=30)
    ids = TrackIdentifiers(title="歌", artist="手")

    res1 = asyncio.run(chain.fetch(ids))
    assert res1 is None
    import time
    # 命中负缓存：第二次不再触发 provider
    res2 = asyncio.run(chain.fetch(ids))
    assert res2 is None
    assert bad.calls == 1


def test_chain_requires_gate():
    netease = _StubProvider("netease", _ok("netease"),
                            requires=("netease_id",))
    chain = LyricsChain([netease], total_timeout=5.0)

    ids = TrackIdentifiers(title="歌", artist="手")  # 无 netease_id → 被门挡下
    assert asyncio.run(chain.fetch(ids)) is None
    assert netease.calls == 0

    # 注意负缓存按 (title, artist) 命中，用新键验证带上 id 后可出歌词
    ids2 = TrackIdentifiers(title="歌2", artist="手", netease_id="123")
    res = asyncio.run(chain.fetch(ids2))
    assert res is not None and res.provider == "netease"


# ============ 本地文件 Provider ============
def test_file_provider_matches(tmp_path):
    (tmp_path / "歌 - 手.lrc").write_text("[00:00.00] 歌词", encoding="utf-8")
    provider = FileLyricsProvider(tmp_path)
    ids = TrackIdentifiers(title=" 歌 ", artist="手")
    res = asyncio.run(provider.fetch(ids))
    assert res is not None and res.provider == "local-file"
    assert res.lrc == "[00:00.00] 歌词"


# ============ HTTP 熔断 ============
class _BoomResp:
    status = 502

    async def __aenter__(self):
        return self

    async def __aexit__(self, *a):
        return None

    async def json(self):
        return {}


class _BoomSession:
    def __init__(self):
        self.calls = 0

    def get(self, url, **kw):   # 模拟 aiohttp：同步返回异步上下文管理器
        self.calls += 1
        return _BoomResp()


def test_http_fuse_opens_after_threshold():
    async def main():
        http = HttpClient(session=_BoomSession(), timeout=2.0, retries=0,
                         fuse_threshold=3, fuse_cooldown=60.0)
        for _ in range(3):
            assert await http.get_json("http://x/a", key="a") is None
        assert http.is_fused()
        try:
            await http.get_json("http://x/b", key="b")
            raise AssertionError("应在熔断后抛 HttpFuseError")
        except HttpFuseError:
            pass
    asyncio.run(main())


# ============ 歌曲相似度算法 ============
def test_similarity_exact_and_version():
    assert calculate_similarity("晴天", "周杰伦", "晴天", "周杰伦") >= EXACT_MATCH_THRESHOLD
    # 本地原版 vs 云端 Live 版：应判不匹配
    assert calculate_similarity("晴天", "周杰伦", "晴天 (Live)", "周杰伦") < EXACT_MATCH_THRESHOLD
    # remix 版本关键词：应判不匹配
    assert calculate_similarity("Shape of You", "Ed Sheeran",
                                "Shape of You (Remix)", "Ed Sheeran") < EXACT_MATCH_THRESHOLD


def test_similarity_case_and_fullwidth():
    # 大小写不敏感
    assert calculate_similarity("Night Dancer", "yoasobi",
                                "night dancer", "YOASOBI") >= EXACT_MATCH_THRESHOLD
    # 全角转半角
    assert calculate_similarity("Ｓｈａｐｅ　ｏｆ　Ｙｏｕ", "Ｅｄ　Ｓｈｅｅｒａｎ",
                                "Shape of You", "Ed Sheeran") >= EXACT_MATCH_THRESHOLD


def test_similarity_wrong_artist_rejected():
    # 同名歌但歌手完全不同：应判不匹配
    assert calculate_similarity("Hello", "Adele", "Hello", "Lionel Richie") < EXACT_MATCH_THRESHOLD


# ============ eapi 加密 ============
def test_eapi_encrypt_output_shape():
    from server.lyrics.eapi import eapi_encrypt
    params = eapi_encrypt("https://interface3.music.163.com/eapi/song/lyric/v1",
                          {"id": "123", "lv": "0"})
    # 大写 hex，且长度是 16 字节块的整数倍
    assert params and all(c in "0123456789ABCDEF" for c in params)
    assert len(params) % 32 == 0


# ============ 智能选优 ============
def test_chain_smart_select_similarity_first():
    # 相似度高的源胜出
    qq = _StubProvider("qq", _ok("qq", sim=90), requires=("qq_id",))
    ne = _StubProvider("netease", _ok("netease", sim=95), requires=("netease_id",))
    chain = LyricsChain([ne, qq], total_timeout=5.0, parallel=True)
    ids = TrackIdentifiers(title="歌", artist="手", netease_id="1", qq_id="2")
    res = asyncio.run(chain.fetch(ids))
    assert res is not None and res.provider == "netease"


def test_chain_smart_select_completeness_tiebreak():
    # 相似度相同，齐全度（有翻译）高者胜出
    qq = _StubProvider("qq", lambda ids: LyricsResult(
        provider="qq", has_lyric=True, lrc="L", translated_lyric="T",
        similarity=100), requires=("qq_id",))
    ne = _StubProvider("netease", lambda ids: LyricsResult(
        provider="netease", has_lyric=True, lrc="L", similarity=100),
        requires=("netease_id",))
    chain = LyricsChain([ne, qq], total_timeout=5.0, parallel=True)
    ids = TrackIdentifiers(title="歌", artist="手", netease_id="1", qq_id="2")
    res = asyncio.run(chain.fetch(ids))
    assert res is not None and res.provider == "qq"