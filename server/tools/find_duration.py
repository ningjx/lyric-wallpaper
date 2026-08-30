# -*- coding: utf-8 -*-
"""在进度值附近扫描歌曲总时长（常量 float64）"""
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

PROGRESS_OFFSET = 0x1D808F8
REGION_HALF = 0x4000  # 扫描进度值附近 ±16KB

progress_addr = base + PROGRESS_OFFSET
region_start = progress_addr - REGION_HALF
region_size = REGION_HALF * 2

print(f"进度地址: 0x{progress_addr:X}")
print(f"扫描区域: 0x{region_start:X} ~ 0x{region_start + region_size:X} ({region_size} 字节)")
print()

# 读两次，间隔 2.5 秒
data1 = pm.read_bytes(region_start, region_size)
pos1 = struct.unpack("<d", pm.read_bytes(progress_addr, 8))[0]
time.sleep(2.5)
data2 = pm.read_bytes(region_start, region_size)
pos2 = struct.unpack("<d", pm.read_bytes(progress_addr, 8))[0]

print(f"采样期间进度: {pos1:.2f}s -> {pos2:.2f}s (Δ{pos2 - pos1:.2f})")
print()

# 找常量 float64：两次读值完全相同，且是合理的歌曲时长 (60~6000 秒)，且大于当前进度
candidates = []
for i in range(0, region_size - 8, 8):
    v1 = struct.unpack_from("<d", data1, i)[0]
    if not (60.0 <= v1 <= 6000.0):
        continue
    if v1 <= pos2:  # 时长必须大于当前进度
        continue
    v2 = struct.unpack_from("<d", data2, i)[0]
    if v1 == v2:
        addr = region_start + i
        off = addr - base
        m, s = divmod(v1, 60)
        candidates.append((off, v1, addr))
        print(f"  候选: dll+0x{off:X} (0x{addr:X}) 时长={int(m):02d}:{s:05.2f}")

if not candidates:
    print("  未找到常量时长候选")
else:
    print(f"\n共 {len(candidates)} 个候选。")
    print("其中位于进度值附近且连续内存的，最可能是真实总时长。")
