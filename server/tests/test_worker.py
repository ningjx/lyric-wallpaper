import asyncio
import io
import json
import threading
from unittest.mock import Mock

from server import server
from server.core.state import RawSnapshot
from server.core.store import ResolvedPlayer, StateStore
from server.lyrics.providers import LyricsResult
from server.lyrics.resolver import ResolverTask
from server.worker import SnapshotPipe, run_worker


def messages(output):
    return [json.loads(line) for line in output.getvalue().splitlines()]


def test_pipe_sends_track_data_only_when_changed():
    async def check():
        output = io.StringIO()
        pipe = SnapshotPipe(output, Mock())
        store = StateStore()
        pipe.publish(store)
        store.resolved = ResolvedPlayer(True, True, 1, 100, "甲", "歌手")
        ctx = store.begin_track_context("甲", "歌手", 100)
        pipe.publish(store)
        ctx.lyric_result = LyricsResult(provider="cache", has_lyric=True, lrc="[00:01]甲")
        pipe.publish(store)
        store.resolved.progress = 2
        pipe.publish(store)
        pipe.publish(store)
        store.resolved = ResolvedPlayer(True, False, 0, 100, "乙", "歌手")
        pipe.publish(store)
        readings = messages(output)
        assert len(readings) == 5
        assert not readings[0]["hasSong"]
        assert readings[1]["lyrics"] == ""
        assert readings[2]["lyrics"] == "[00:01]甲"
        assert readings[3] == {"hasSong": True, "playing": True, "position": 2}
        assert readings[4]["song"] == "乙"
        assert readings[4]["lyrics"] == ""
        assert not readings[4]["playing"]

    asyncio.run(check())


def test_rapid_track_reselection_replaces_pending_context():
    async def check():
        class Searcher:
            vendor = "netease"

            async def search(self, ids):
                started.set()
                await asyncio.Future()

        started = asyncio.Event()
        store = StateStore()
        resolver = ResolverTask(store, [Searcher()], Mock(), Mock())
        resolver.schedule("甲", "", 100)
        first = store.track_context
        await asyncio.wait_for(started.wait(), 1)
        resolver.schedule("乙", "", 100)
        resolver.schedule("甲", "", 100)
        assert store.track_context.song == "甲"
        assert store.track_context is not first
        assert first.future.done()
        await resolver.stop()
        assert store.track_context.future.done()

    asyncio.run(check())


def test_worker_stops_with_parent_pipe_without_creating_http_app(tmp_path, monkeypatch):
    class ParentPipe:
        def __init__(self):
            self.closed = threading.Event()

        def read(self, size):
            self.closed.wait(2)
            return ""

    class FakeSource:
        def start(self):
            publish("netease", RawSnapshot("netease", True, 12.5, 100, "甲", "歌手", True))

        def stop(self):
            stopped.set()

    class OfflineSearcher:
        vendor = "offline"

        async def search(self, ids):
            return None

    def sources(config, publish_snapshot):
        nonlocal publish
        publish = publish_snapshot
        return {"netease": FakeSource()}

    publish = None
    stopped = threading.Event()
    config = tmp_path / "config.json"
    config.write_text(json.dumps({"cache": {"data_dir": str(tmp_path)},
                                  "music": {"snapshot_max_age": .1},
                                  "lyrics": {"provider_order": ["local-file"]}}), encoding="utf-8")
    monkeypatch.setattr(server, "build_sources", sources)
    app = Mock(side_effect=AssertionError("worker must not create an HTTP app"))
    monkeypatch.setattr(server, "create_app", app)
    monkeypatch.setattr(server, "NeteaseSearcher", lambda *args, **kwargs: OfflineSearcher())
    monkeypatch.setattr(server, "QQMusicSearcher", lambda *args, **kwargs: OfflineSearcher())

    async def check():
        stdin, stdout = ParentPipe(), io.StringIO()
        task = asyncio.create_task(run_worker(str(config), stdin, stdout))
        try:
            for _ in range(100):
                if len(messages(stdout)) >= 3:
                    break
                await asyncio.sleep(.01)
            assert messages(stdout)[1]["position"] == 12.5
            assert not messages(stdout)[-1]["hasSong"]
        finally:
            stdin.closed.set()
            await asyncio.wait_for(task, 2)

    asyncio.run(check())
    app.assert_not_called()
    assert stopped.is_set()
