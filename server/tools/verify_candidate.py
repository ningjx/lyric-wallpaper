# -*- coding: utf-8 -*-
"""验证候选地址是否为播放进度 - 每秒采样 15 次"""
import struct
import sys
import time

import pymem

pm = pymem.Pymem("cloudmusic.exe")

base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        break

if base is None:
    print("未找到 cloudmusic.dll")
    sys.exit(1)

CANDIDATE = 0x1D808F8  # 上一轮扫描命中的偏移

print(f"cloudmusic.dll 基址: 0x{base:X}")
print(f"验证地址: 0x{base + CANDIDATE:X} (dll+0x{CANDIDATE:X})")
print("每秒采样，共 15 次\n")
print(f"{'时间':>10} | {'值':>10} | {'Δ(与上次)':>10} | 判断")

last_val = None
last_t = None
for i in range(15):
    t = time.time()
    try:
        val = struct.unpack("<d", pm.read_bytes(base + CANDIDATE, 8))[0]
        if last_val is not None:
            delta_t = t - last_t
            delta_v = val - last_val
            verdict = ""
            if abs(delta_v - delta_t) < 0.3:
                verdict = "✓ 完美匹配播放速度"
            elif abs(delta_v) < 0.01:
                verdict = "静止(可能暂停)"
            else:
                verdict = f"? 速度不符 (Δv={delta_v:.2f} vs Δt={delta_t:.2f})"
            print(f"{time.strftime('%H:%M:%S')} | {val:10.3f} | {delta_v:+10.3f} | {verdict}")
        else:
            print(f"{time.strftime('%H:%M:%S')} | {val:10.3f} | {'':>10} | 初始值")
        last_val = val
        last_t = t
    except Exception as e:
        print(f"读取失败: {e}")
        last_val = None
        last_t = None
    time.sleep(1)

print("\n验证完成。")
