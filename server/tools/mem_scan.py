# -*- coding: utf-8 -*-
"""读取网易云音乐 cloudmusic.dll 信息，尝试已知偏移，并扫描播放进度"""
import struct
import sys
import time

import pymem

pm = pymem.Pymem("cloudmusic.exe")

# 1. 获取 cloudmusic.dll 模块信息
base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        print(f"cloudmusic.dll 基址: 0x{base:X}")
        print(f"cloudmusic.dll 大小: 0x{module.SizeOfImage:X} ({module.SizeOfImage / 1024 / 1024:.1f} MB)")
        break

if base is None:
    print("未找到 cloudmusic.dll")
    sys.exit(1)

# 2. 尝试已知旧版本的偏移（3.1.18 的指针链）
print("\n=== 尝试旧版偏移 ===")
old_chains = [
    (0x01CA1190, 0xB8, "3.1.18 (Netease_obs)"),
    (0x01C6D230, 0xB8, "旧版候选1"),
    (0x01C713B0, 0xB8, "旧版候选2"),
]

for base_off, final_off, label in old_chains:
    try:
        ptr_addr = base + base_off
        raw = pm.read_bytes(ptr_addr, 8)
        ptr_val = struct.unpack("<Q", raw)[0]
        final_addr = ptr_val + final_off
        val = struct.unpack("<d", pm.read_bytes(final_addr, 8))[0]
        print(f"  {label}: ptr=0x{ptr_val:X} -> val={val:.3f}")
    except Exception as e:
        print(f"  {label}: 失败 ({e})")

# 3. 扫描 cloudmusic.dll 内存中的 float64 进度值
print("\n=== 扫描播放进度 (float64) ===")
print("请确保正在播放一首歌，且进度在 1~600 秒之间...")

dll_size = module.SizeOfImage
print(f"正在读取 {dll_size / 1024 / 1024:.1f} MB DLL 内存...")

t0 = time.time()
data = pm.read_bytes(base, dll_size)
t1 = time.time()
print(f"读取完成，耗时 {t1 - t0:.1f}s")

# 扫描所有合理的 float64 值 (1.0 ~ 600.0 秒)
candidates = []
for i in range(0, len(data) - 8, 8):
    val = struct.unpack_from("<d", data, i)[0]
    if 1.0 <= val <= 600.0 and val == int(val * 10) / 10:  # 粗略过滤
        candidates.append((base + i, val))

print(f"第一轮扫描: {len(candidates)} 个候选值")

# 等 3 秒，再读一次，找递增了约 3 秒的候选
time.sleep(3)
data2 = pm.read_bytes(base, dll_size)
print(f"第二轮读取完成")

matched = []
for addr, val1 in candidates:
    off = addr - base
    val2 = struct.unpack_from("<d", data2, off)[0]
    delta = val2 - val1
    if 2.5 <= delta <= 3.5:  # 3 秒内进度增加约 3 秒
        matched.append((addr, val1, val2, delta))
        print(f"  >>> 命中! 地址=0x{addr:X} (cloudmusic.dll+0x{off:X}) "
              f"val1={val1:.1f} val2={val2:.1f} delta={delta:.2f}")

if not matched:
    print("  无命中 (歌曲可能暂停了，或值不在 1-600 范围)")
    # 放宽条件再看
    print("\n=== 放宽条件重扫 ===")
    for i in range(0, len(data) - 8, 8):
        val1 = struct.unpack_from("<d", data, i)[0]
        val2 = struct.unpack_from("<d", data2, i)[0]
        delta = val2 - val1
        if 2.0 <= delta <= 4.0:
            print(f"  候选: 0x{base + i:X} (dll+0x{i:X}) {val1:.1f} -> {val2:.1f} (Δ{delta:.2f})")
