# -*- coding: utf-8 -*-
"""网易云音乐 SMTC 诊断工具 - 扫描所有 SMTC 会话并监控 Timeline"""
import asyncio
import sys
from datetime import datetime, timezone

from winrt.windows.media.control import GlobalSystemMediaTransportControlsSessionManager


async def scan_sessions(manager):
    """列出所有 SMTC 会话"""
    sessions = manager.get_sessions()
    result = []
    for s in sessions:
        info = s.get_playback_info()
        try:
            props = await s.try_get_media_properties_async()
            title, artist = props.title, props.artist
        except Exception:
            title, artist = "?", "?"
        tl = s.get_timeline_properties()
        result.append((s, info, props, tl))
        print(f"  会话: {s.source_app_user_model_id}")
        print(f"    Status={info.playback_status} | Title={title} | Artist={artist}")
        print(f"    Timeline: Pos={tl.position.total_seconds():.1f}s "
              f"End={tl.end_time.total_seconds():.1f}s LastUpd={tl.last_updated_time}")
    return result


async def main():
    print("=== SMTC 会话扫描 (Python) ===")
    manager = await GlobalSystemMediaTransportControlsSessionManager.request_async()
    sessions = await scan_sessions(manager)

    # 找网易云
    target = None
    for s, info, props, tl in sessions:
        sid = s.source_app_user_model_id
        if any(k in sid.lower() for k in ("netease", "cloudmusic", "网易云")):
            target = (s, info, props, tl)
            print(f"\n>>> 找到网易云: {sid} <<<")
            break

    if target is None:
        print("\n[FAIL] 未找到网易云 SMTC 会话")
        print("结论: 网易云没有向 Windows SMTC 注册会话")
        return

    s, info, props, tl = target
    print(f"\n歌曲: {props.title} - {props.artist}")
    print(f"Timeline: Pos={tl.position.total_seconds():.1f}s "
          f"End={tl.end_time.total_seconds():.1f}s")

    # 实时监控 10 秒
    print("\n=== 实时监控 10 秒 ===")
    for i in range(20):
        await asyncio.sleep(0.5)
        info = s.get_playback_info()
        tl = s.get_timeline_properties()
        now = datetime.now(timezone.utc)
        elapsed = (now - tl.last_updated_time).total_seconds()
        comp = min(tl.position.total_seconds() + elapsed, tl.end_time.total_seconds())
        print(f"  {datetime.now():%H:%M:%S} | {str(info.playback_status):10} | "
              f"补偿后={comp:7.1f}s | Raw={tl.position.total_seconds():7.1f}s | "
              f"End={tl.end_time.total_seconds():7.1f}s | 距更新={elapsed:.2f}s")


asyncio.run(main())
