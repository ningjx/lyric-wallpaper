# -*- coding: utf-8 -*-
"""扫描网易云进程内存中的歌词文本 (修复版)"""
import ctypes
import struct
import sys
from ctypes import wintypes

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")
print(f"进程 ID: {pm.process_id}")

base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        break

PROGRESS_OFFSET = 0x1D808F8
DURATION_OFFSET = 0x1DE1038
prog = struct.unpack("<d", pm.read_bytes(base + PROGRESS_OFFSET, 8))[0]
dur = struct.unpack("<d", pm.read_bytes(base + DURATION_OFFSET, 8))[0]
print(f"当前播放: {prog:.2f}s / {dur:.2f}s\n")

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

# 用 pymem 自己的句柄（带完整权限）
h = pm.process_handle

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

total = sum(r[1] for r in regions)
print(f"可读已提交区域: {len(regions)} 个, 共 {total / 1024 / 1024:.0f} MB")

# 扫描 UTF-16 歌词时间戳 '[mm:ss.xx]' 模式: 5B 00 3x 00 3x 00 3A 00
print("\n=== 扫描 UTF-16 歌词时间戳 ===")
found_regions = []
scanned = 0

for ra, rs in regions:
    if rs > 32 * 1024 * 1024:  # 只扫 <=32MB 的堆块
        continue
    try:
        data = pm.read_bytes(ra, rs)
    except Exception:
        continue
    scanned += 1
    # 多种时间戳格式
    for pattern in [b'\x5b\x00\x30\x00', b'\x5b\x00\x31\x00', b'\x5b\x00\x32\x00', b'\x5b\x00\x33\x00']:
        idx = data.find(pattern)
        while idx >= 0:
            chunk = data[max(0, idx - 0x20): min(len(data), idx + 0x180)]
            try:
                text = chunk.decode("utf-16-le", errors="replace")
                lines = [l for l in text.split("\n") if l.strip()]
                valid = False
                if lines and "]" in lines[0]:
                    head = lines[0].split("]")[0]
                    if head.startswith("[") and ":" in head and len(head) <= 10:
                        valid = True
                if valid:
                    if any(c.isalpha() or "一" <= c <= "鿿" for c in text[:80]):
                        found_regions.append((ra, rs, text[:150]))
                        print(f"\n命中区域: 0x{ra:X} ({rs/1024:.0f} KB), 偏移 {idx}")
                        print(f"  内容预览: {text[:150]!r}")
                        break
            except Exception:
                pass
            idx = data.find(pattern, idx + 1)
            if idx >= 0:
                continue
            break

print(f"\n扫描了 {scanned} 个区域, 找到 {len(found_regions)} 个歌词区域")
