# -*- coding: utf-8 -*-
"""实时监控：等待切歌，验证进度/时长/速率字段原地更新"""
import struct
import sys
import time

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")
base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        break

PROGRESS_OFFSET = 0x1D808F8   # 播放进度 (秒, float64)
DURATION_OFFSET = 0x1DE1038   # 歌曲时长 (秒, float64)
RATE_OFFSET = PROGRESS_OFFSET + 8  # 疑似播放速率 (1.0 = 正常)

print("实时监控 cloudmusic.dll 播放字段")
print("请随便切一首歌，看看字段如何变化\n")
print(f"{'时间':>9} | {'进度':>9} | {'时长':>8} | {'速率':>6} | 事件")

last_prog = None
last_dur = None

while True:
    try:
        prog = struct.unpack("<d", pm.read_bytes(base + PROGRESS_OFFSET, 8))[0]
        dur = struct.unpack("<d", pm.read_bytes(base + DURATION_OFFSET, 8))[0]
        rate = struct.unpack("<d", pm.read_bytes(base + RATE_OFFSET, 8))[0]

        event = ""
        if last_prog is not None:
            if prog < last_prog - 1.0:
                event = " *** 切歌/回退! 进度重置 ***"
            elif abs((prog - last_prog) - 1.0) > 0.5 and prog > last_prog:
                event = " *** 进度跳变(拖动?) ***"
            elif abs(prog - last_prog) < 0.01 and abs(rate) > 0.5:
                event = " 已暂停?"
            if last_dur is not None and abs(dur - last_dur) > 0.5:
                event += f" *** 时长变化 {last_dur:.1f}->{dur:.1f} ***"

        pm_, ps_ = divmod(prog, 60)
        dm_, ds_ = divmod(dur, 60)
        print(f"{time.strftime('%H:%M:%S')} | {int(pm_):02d}:{ps_:05.2f} | {int(dm_):02d}:{ds_:05.2f} | {rate:6.3f} |{event}")

        last_prog = prog
        last_dur = dur
    except Exception as e:
        print(f"读取失败: {e}")

    time.sleep(0.5)
