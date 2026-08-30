# -*- coding: utf-8 -*-
"""扫描网易云内存中的歌词 - 第二轮: UTF-8 编码 + 纯文本歌词行"""
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

# 1. 当前歌曲信息（窗口标题）
import win32gui
import win32process
title = ""
def cb(hwnd, _):
    global title
    if win32gui.IsWindowVisible(hwnd):
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        if pid == pm.process_id:
            t = win32gui.GetWindowText(hwnd)
            if " - " in t:
                title = t
                return False
    return True
win32gui.EnumWindows(cb, None)
print(f"当前歌曲: {title}")
song_name = title.split(" - ")[-1]

# 2. 枚举区域
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
print(f"可读区域: {len(regions)} 个, 共 {total / 1024 / 1024:.0f} MB\n")

# 3. 扫描 UTF-8 歌词时间戳 '[mm:ss.xx]'
print("=== A. 扫描 UTF-8 时间戳歌词 ===")
pattern_utf8 = b'[00:'
found_a = 0
for ra, rs in regions:
    if rs > 32 * 1024 * 1024:
        continue
    try:
        data = pm.read_bytes(ra, rs)
    except Exception:
        continue
    idx = 0
    while True:
        idx = data.find(pattern_utf8, idx)
        if idx < 0:
            break
        chunk = data[max(0, idx - 0x20): min(len(data), idx + 0x200)]
        try:
            text = chunk.decode("utf-8", errors="replace")
            # 验证是歌词: 含时间戳+文字
            if "]" in text and any("一" <= c <= "鿿" for c in text):
                print(f"\n  [UTF-8] 0x{ra + idx:X} 区域 0x{ra:X} ({rs/1024:.0f}KB):")
                print(f"    {text[:250]!r}")
                found_a += 1
                break
        except Exception:
            pass
        idx += 1
print(f"UTF-8 时间戳歌词: 找到 {found_a} 处")

# 4. 扫描当前歌词行（桌面歌词模式下的纯文本，UTF-16）
print("\n=== B. 扫描歌名/关键字的 UTF-16 文本 ===")
# 当前歌词行应该包含歌曲中的词，扫描中文文本块
# 简化: 找 UTF-16 中文连续块 (>=8 个中文字符)
found_b = 0
for ra, rs in regions:
    if rs > 32 * 1024 * 1024:
        continue
    if found_b >= 10:
        break
    try:
        data = pm.read_bytes(ra, rs)
    except Exception:
        continue
    # 粗略扫: 找 3 个连续中文 UTF-16 字符 (0x4E00-0x9FFF)
    i = 0
    while i < len(data) - 40:
        b = data[i:i+2]
        if len(b) == 2:
            cp = struct.unpack("<H", b)[0]
            if 0x4E00 <= cp <= 0x9FFF:
                # 连续检查 5 个字符
                cps = []
                j = i
                while j < len(data) - 2 and len(cps) < 8:
                    cp2 = struct.unpack("<H", data[j:j+2])[0]
                    if 0x4E00 <= cp2 <= 0x9FFF:
                        cps.append(cp2)
                        j += 2
                    else:
                        break
                if len(cps) >= 8:
                    text = "".join(chr(c) for c in cps)
                    # 排除常见 UI 文本
                    skip_words = ["网易云音乐", "播放列表", "正在播放", "登录", "设置", "发现音乐", "我的音乐", "朋友", "账号", "本地音乐", "下载管理", "我的主播电台", "收藏", "搜索"]
                    if not any(w in text for w in skip_words):
                        print(f"  0x{ra + i:X}: {text}...")
                        found_b += 1
                        i = j
                        continue
                i = j
                continue
        i += 2
print(f"\nUTF-16 中文文本块: 找到 {found_b} 处")
