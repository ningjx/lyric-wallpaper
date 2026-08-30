# -*- coding: utf-8 -*-
"""验证: 切歌后内存中的歌词是否更新"""
import ctypes
import struct
import sys
from ctypes import wintypes

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")

MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_ulonglong),
        ("AllocationBase", ctypes.c_ulonglong),
        ("AllocationProtect", wintypes.DWORD),
        ("PartitionId", wintypes.WORD),
        ("RegionSize", ctypes.c_ulonglong),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.VirtualQueryEx.argtypes = [wintypes.HANDLE, ctypes.c_ulonglong, ctypes.POINTER(MEMORY_BASIC_INFORMATION), ctypes.c_ulonglong]
kernel32.VirtualQueryEx.restype = ctypes.c_size_t

h = pm.process_handle

def find_lyrics():
    """扫描所有堆区域找 UTF-8 时间戳歌词"""
    regions = []
    addr = 0
    while addr < 0x7FFFFFFFFFFF:
        mbi = MEMORY_BASIC_INFORMATION()
        ret = kernel32.VirtualQueryEx(h, ctypes.c_ulonglong(addr), ctypes.byref(mbi), ctypes.sizeof(mbi))
        if ret == 0:
            break
        if (mbi.State == MEM_COMMIT and mbi.BaseAddress and
            (mbi.Protect & PAGE_NOACCESS) == 0 and (mbi.Protect & PAGE_GUARD) == 0):
            regions.append((mbi.BaseAddress, mbi.RegionSize))
        addr = mbi.BaseAddress + mbi.RegionSize

    results = []
    for ra, rs in regions:
        if rs > 64 * 1024 * 1024:
            continue
        try:
            data = pm.read_bytes(ra, rs)
        except Exception:
            continue
        idx = 0
        while True:
            idx = data.find(b'[00:', idx)
            if idx < 0:
                break
            chunk = data[max(0, idx - 0x10): min(len(data), idx + 0x400)]
            try:
                text = chunk.decode("utf-8", errors="replace")
                if "]" in text[:20] and any("一" <= c <= "鿿" for c in text[20:80]):
                    results.append((ra, rs, text[:120]))
                    break
            except Exception:
                pass
            idx += 1
    return results

print("=== 第一轮扫描 (当前歌曲) ===")
r1 = find_lyrics()
for ra, rs, text in r1:
    print(f"  区域 0x{ra:X} ({rs/1024:.0f}KB): {text[:80]!r}")
print(f"  共 {len(r1)} 处\n")

print("请在网易云切换到另一首歌，然后等 3 秒...")
import time
time.sleep(3)

print("=== 第二轮扫描 (切歌后) ===")
r2 = find_lyrics()
for ra, rs, text in r2:
    print(f"  区域 0x{ra:X} ({rs/1024:.0f}KB): {text[:80]!r}")
print(f"  共 {len(r2)} 处\n")

if r1 and r2:
    old_first = r1[0][2]
    new_first = r2[0][2]
    if old_first != new_first:
        print("✓ 歌词已随切歌更新！内存中的歌词是实时跟随当前歌曲的")
    else:
        print("✗ 歌词未变化（可能切歌未成功或歌词还没加载）")
