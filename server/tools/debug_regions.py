# -*- coding: utf-8 -*-
"""调试内存区域枚举"""
import ctypes
import struct
import sys
from ctypes import wintypes

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")
print(f"进程 ID: {pm.process_id}")

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400

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
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

h = kernel32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pm.process_id)
hv = h if h else 0
print(f"OpenProcess 句柄: 0x{hv:X} (错误: {ctypes.get_last_error()})")

if not h:
    sys.exit(1)

# 用 pymem 自己打开的句柄试试
pm_handle = pm.process_handle
print(f"pymem 的句柄: 0x{pm_handle:X}")

mbi = MEMORY_BASIC_INFORMATION()
ret = kernel32.VirtualQueryEx(h, ctypes.c_void_p(0x100000000), ctypes.byref(mbi), ctypes.sizeof(mbi))
print(f"VirtualQueryEx(0x100000000): 返回 {ret}, 错误 {ctypes.get_last_error()}")
if ret:
    print(f"  Base=0x{mbi.BaseAddress:X} Size=0x{mbi.RegionSize:X} Protect=0x{mbi.Protect:X} State=0x{mbi.State:X}")

# 用 pymem 句柄
mbi2 = MEMORY_BASIC_INFORMATION()
ret2 = kernel32.VirtualQueryEx(pm_handle, ctypes.c_void_p(0x100000000), ctypes.byref(mbi2), ctypes.sizeof(mbi2))
print(f"VirtualQueryEx(pymem句柄): 返回 {ret2}, 错误 {ctypes.get_last_error()}")
if ret2:
    print(f"  Base=0x{mbi2.BaseAddress:X} Size=0x{mbi2.RegionSize:X} Protect=0x{mbi2.Protect:X}")

# 简单粗暴：从 cloudmusic.dll 基址开始逐个区域枚举（只枚举 64 位地址空间的低区）
print("\n=== 从 0 开始枚举区域 (前 20 个) ===")
addr = 0
count = 0
while count < 20 and addr < 0x7FFFFFFFFFFF:
    mbi = MEMORY_BASIC_INFORMATION()
    ret = kernel32.VirtualQueryEx(pm_handle, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi))
    if ret == 0:
        print(f"VirtualQueryEx 在 0x{addr:X} 失败, 错误 {ctypes.get_last_error()}")
        break
    print(f"  0x{mbi.BaseAddress:X} - 0x{mbi.BaseAddress + mbi.RegionSize:X} "
          f"({mbi.RegionSize/1024:.0f} KB) Protect=0x{mbi.Protect:X} State=0x{mbi.State:X}")
    addr = mbi.BaseAddress + mbi.RegionSize
    count += 1

kernel32.CloseHandle(h)
