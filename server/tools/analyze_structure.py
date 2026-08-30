# -*- coding: utf-8 -*-
"""分析进度值的内存结构：找指向它的指针 + 验证时长候选"""
import struct
import sys
import time

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")

base = None
size = 0
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        size = module.SizeOfImage
        break

PROGRESS_OFFSET = 0x1D808F8
progress_addr = base + PROGRESS_OFFSET

print("=== 分析 1: 找指向进度值的指针（用于版本无关定位）===")
# 读取整个 DLL 数据段，找值为 progress_addr 或 progress_addr-0xB8 的指针
# 3.1.18 的结构是: dll+STATIC -> 解引用 -> +0xB8 -> 进度
data = pm.read_bytes(base, size)
print(f"DLL 已读取 ({size / 1024 / 1024:.1f} MB)")

# 查找指向 progress_addr 的指针（直接指针）
print(f"\n查找直接指向 0x{progress_addr:X} 的指针...")
found_direct = []
for i in range(0, len(data) - 8, 8):
    v = struct.unpack_from("<Q", data, i)[0]
    if v == progress_addr:
        found_direct.append(base + i)
        print(f"  找到: dll+0x{i:X} -> 进度地址 (直接)")

# 查找指向 progress_addr - 0xB8 的指针（3.1.18 同款结构）
target_sub_b8 = progress_addr - 0xB8
print(f"\n查找指向 0x{target_sub_b8:X} (进度-0xB8) 的指针...")
found_sub = []
for i in range(0, len(data) - 8, 8):
    v = struct.unpack_from("<Q", data, i)[0]
    if v == target_sub_b8:
        found_sub.append(base + i)
        print(f"  找到: dll+0x{i:X} -> 进度-0xB8")

# 查找指向进度附近 ±0x200 的指针
print(f"\n查找指向进度±0x200 范围内的指针...")
found_near = []
for i in range(0, len(data) - 8, 8):
    v = struct.unpack_from("<Q", data, i)[0]
    if progress_addr - 0x200 <= v <= progress_addr + 0x200 and v != progress_addr:
        found_near.append((base + i, v))
        if len(found_near) <= 20:
            print(f"  找到: dll+0x{i:X} -> 0x{v:X} (距进度 {v - progress_addr:+d})")

print(f"\n附近指针共 {len(found_near)} 个")

print()
print("=== 分析 2: 进度值周围 0x200 字节的结构 dump ===")
dump = pm.read_bytes(progress_addr - 0x100, 0x200)
print("偏移    | float64 值 | 解释")
print("-" * 55)
for i in range(0, 0x200, 8):
    v = struct.unpack_from("<d", dump, i)[0]
    off = i - 0x100
    note = ""
    if off == 0:
        note = "  <<< 播放进度"
    elif 30.0 <= v <= 6000.0:
        m, s = divmod(v, 60)
        note = f"  可能时长 {int(m)}:{s:05.2f}"
    print(f"{off:+6X}  | {v:12.4f} |{note}")

print()
print("=== 分析 3: 时长候选跟踪 ===")
print("请记住当前歌曲，稍后切换到另一首时长差异大的歌（如超过1分钟差异）")
print("先记录当前所有时长候选...")
print()

# 从上次结果知道这些候选，重新扫描并记录
progress_region_start = progress_addr - 0x4000
region_data = pm.read_bytes(progress_region_start, 0x8000)
cur_pos = struct.unpack("<d", pm.read_bytes(progress_addr, 8))[0]

candidates_now = {}
for i in range(0, 0x8000 - 8, 8):
    v = struct.unpack_from("<d", region_data, i)[0]
    if 60.0 <= v <= 6000.0 and v > cur_pos:
        candidates_now[progress_region_start + i] = v

print(f"当前记录 {len(candidates_now)} 个时长候选")
print("请在 30 秒内切换到另一首歌（时长明显不同的）...")
print()

for tick in range(15):
    time.sleep(2)
    cur_pos = struct.unpack("<d", pm.read_bytes(progress_addr, 8))[0]
    print(f"  {tick * 2 + 2:2d}s 后 | 当前进度: {cur_pos:7.2f}s")

print()
print("切换歌曲后重新读取所有候选...")
region_data2 = pm.read_bytes(progress_region_start, 0x8000)
cur_pos2 = struct.unpack("<d", pm.read_bytes(progress_addr, 8))[0]
print(f"当前进度: {cur_pos2:.2f}s (若 < 之前的值，说明切歌成功)")
print()

changed = []
for addr, v1 in candidates_now.items():
    off = addr - progress_region_start
    v2 = struct.unpack_from("<d", region_data2, off)[0]
    if v1 != v2:
        changed.append((addr, v1, v2))

if changed:
    print(f"切歌后发生变化的时长候选 ({len(changed)} 个):")
    for addr, v1, v2 in changed:
        m1, s1 = divmod(v1, 60)
        m2, s2 = divmod(v2, 60)
        print(f"  0x{addr:X} (dll+0x{addr - base:X}): {int(m1)}:{s1:05.2f} -> {int(m2)}:{s2:05.2f}")
    print("\n这些就是跟随歌曲切换的真实时长字段！")
else:
    print("没有候选变化（可能没切歌，或候选范围不对）")
