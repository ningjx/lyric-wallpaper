# -*- coding: utf-8 -*-
"""分析时长值附近的结构：找歌曲ID/标题指针，验证结构稳定性"""
import json
import struct
import sys
import time
import urllib.parse
import urllib.request

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")
base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        break

DURATION_OFFSET = 0x1DE1038
duration_addr = base + DURATION_OFFSET

print(f"时长地址: 0x{duration_addr:X} (dll+0x{DURATION_OFFSET:X})")
print()

# 持续观察时长值是否稳定（歌曲不变时应该恒定）
print("=== 观察 5 秒时长值稳定性 ===")
for i in range(5):
    v = struct.unpack("<d", pm.read_bytes(duration_addr, 8))[0]
    m, s = divmod(v, 60)
    print(f"  {time.strftime('%H:%M:%S')} | 时长 = {v:.3f}s ({int(m)}:{s:05.2f})")
    time.sleep(1)

print()
print("=== 时长值周围 ±0x100 结构 dump (8字节对齐) ===")
dump = pm.read_bytes(duration_addr - 0x100, 0x200)
print("偏移    | float64      | uint64(hex)       | 解释")
print("-" * 70)
for i in range(0, 0x200, 8):
    off = i - 0x100
    f = struct.unpack_from("<d", dump, i)[0]
    u = struct.unpack_from("<Q", dump, i)[0]
    note = ""
    if off == 0:
        note = "<<< 时长"
    elif 100_000_000 <= u <= 100_000_000_000:
        note = "? 可能是歌曲ID"
    elif 30.0 <= f <= 6000.0:
        m, s = divmod(f, 60)
        note = f"? 时长 {int(m)}:{s:05.2f}"
    print(f"{off:+6X}  | {f:13.4f} | 0x{u:016X} | {note}")

print()
print("=== 在 DLL 中找指向时长地址附近的指针 ===")
data = pm.read_bytes(base, 0x22C4000)
found = []
for i in range(0, len(data) - 8, 8):
    v = struct.unpack_from("<Q", data, i)[0]
    if duration_addr - 0x200 <= v <= duration_addr + 0x200:
        found.append((base + i, v))
        print(f"  静态指针: dll+0x{i:X} -> 0x{v:X} (距时长 {v - duration_addr:+d})")

print(f"\n共 {len(found)} 个静态指针指向时长附近")
