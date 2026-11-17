"""Native wallpaper child process. Stdout carries JSON Lines and stdin owns its lifetime."""
from __future__ import annotations

import argparse
import asyncio
import json
import math
import sys
import threading
from collections.abc import Callable
from typing import TextIO

from server.config import desktop_config_path, load_config
from server.core.store import StateStore
from server.server import serve, setup_file_logging


class SnapshotPipe:
    def __init__(self, output: TextIO, stop: Callable[[], None]) -> None:
        self._output = output
        self._stop = stop
        self._last_playback: tuple | None = None
        self._last_track: tuple | None = None

    def publish(self, store: StateStore) -> None:
        state = store.resolved
        song, author = (state.song, state.author) if state.has_song else ("", "")
        ctx = store.track_context
        result = ctx.lyric_result if ctx and state.has_song and ctx.song_key == (song, author) else None
        lyrics = result.lrc if result and result.has_lyric else ""
        translation = result.translated_lyric if result and result.has_lyric else ""
        track = (song, author, lyrics, translation)
        position = round(max(0, state.progress), 3) if math.isfinite(state.progress) else 0
        playback = (state.has_song, state.has_song and state.playing, position)
        if playback == self._last_playback and track == self._last_track:
            return
        message = dict(hasSong=playback[0], playing=playback[1], position=position)
        if track != self._last_track:
            message.update(song=song, author=author, lyrics=lyrics, translation=translation)
        try:
            self._output.write(json.dumps(message, ensure_ascii=True) + "\n")
            self._output.flush()
        except OSError:
            self._stop()
        else:
            self._last_playback, self._last_track = playback, track


async def run_worker(config_path: str | None, stdin: TextIO, output: TextIO) -> None:
    cfg = load_config(config_path or desktop_config_path())
    setup_file_logging(cfg)
    loop = asyncio.get_running_loop()
    stop = asyncio.Event()

    def wait_for_parent() -> None:
        try:
            stdin.read(1)  # EOF also covers parent crashes.
        finally:
            if not loop.is_closed():
                loop.call_soon_threadsafe(stop.set)

    threading.Thread(target=wait_for_parent, name="wallpaper-parent", daemon=True).start()
    pipe = SnapshotPipe(output, stop.set)
    await serve(cfg, stop, console_status=False, start_http=False, on_snapshot=pipe.publish)


def main() -> None:
    parser = argparse.ArgumentParser(description="Native wallpaper lyrics worker")
    parser.add_argument("--config")
    args = parser.parse_args()
    output = sys.stdout
    sys.stdout = sys.stderr
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    asyncio.run(run_worker(args.config, sys.stdin, output))


if __name__ == "__main__":
    main()
