# -*- coding: utf-8 -*-
"""扫描网易云进程内存中的歌词文本"""
import struct
import sys

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")
print(f"进程 ID: {pm.process_id}")

# 先看当前歌曲
base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        break

PROGRESS_OFFSET = 0x1D808F8
DURATION_OFFSET = 0x1DE1038
prog = struct.unpack("<d", pm.read_bytes(base + PROGRESS_OFFSET, 8))[0]
dur = struct.unpack("<d", pm.read_bytes(base + DURATION_OFFSET, 8))[0]
print(f"当前播放进度: {prog:.2f}s / {dur:.2f}s\n")

# 枚举进程的所有内存区域
print("=== 枚举可读内存区域 ===")
regions = []
import ctypes
from ctypes import wintypes

PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01

class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD),
        ("PartitionId", wintypes.WORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.VirtualQueryEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.POINTER(MEMORY_BASIC_INFORMATION), ctypes.c_size_t]
kernel32.VirtualQueryEx.restype = ctypes.c_size_t

h = kernel32.OpenProcess(PROCESS_VM_READ, False, pm.process_id)
addr = 0
while True:
    mbi = MEMORY_BASIC_INFORMATION()
    ret = kernel32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi))
    if ret == 0:
        break
    if mbi.State == MEM_COMMIT and (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0:
        if mbi.Protect in (0x04, 0x08, 0x02, 0x20, 0x40):  # 可读的页面
            regions.append((mbi.BaseAddress, mbi.RegionSize))
    addr = mbi.BaseAddress + mbi.RegionSize
    if addr > 0x7FFFFFFFFFFF:
        break

total = sum(r[1] for r in regions)
print(f"可读区域: {len(regions)} 个, 共 {total / 1024 / 1024:.0f} MB")

# 在堆内存中扫描 UTF-16 中文歌词文本模式 "[xx:xx.xx]"
# 歌词行格式: [00:12.34]歌词内容
print("\n=== 扫描 UTF-16 歌词时间戳模式 ===")
# UTF-16LE 的 '[00:' 是 5B 00 30 00 30 00 3A 00
pattern = b'\x5b\x00\x30\x00\x30\x00\x3a\x00'
found_lyric_regions = []

for i, (ra, rs) in enumerate(regions):
    if rs > 64 * 1024 * 1024:  # 跳过超大区域(先看中小堆)
        continue
    try:
        data = pm.read_bytes(ra, rs)
    except Exception:
        continue
    idx = data.find(pattern)
    if idx >= 0:
        # 解析这附近的内容
        start = max(0, idx - 0x40)
        end = min(len(data), idx + 0x200)
        chunk = data[start:end]
        try:
            text = chunk.decode("utf-16-le", errors="replace")
            if "[" in text and "]" in text:
                found_lyric_regions.append((ra, rs, text[:200]))
                print(f"\n命中区域: 0x{ra:X} (大小 {rs/1024:.0f} KB)")
                print(f"  内容: {text[:200]!r}")
        except Exception:
            pass

print(f"\n共 {len(found_lyric_regions)} 个区域包含歌词时间戳")
