# -*- coding: utf-8 -*-
"""调试: 当前内存读取状态"""
import struct
import sys

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")
base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        break

print(f"cloudmusic.dll 基址: 0x{base:X}")

OFFSET_PROGRESS = 0x1D808F8
OFFSET_DURATION = 0x1DE1038
OFFSET_RATE = 0x1D80900

prog = struct.unpack("<d", pm.read_bytes(base + OFFSET_PROGRESS, 8))[0]
dur = struct.unpack("<d", pm.read_bytes(base + OFFSET_DURATION, 8))[0]
rate = struct.unpack("<d", pm.read_bytes(base + OFFSET_RATE, 8))[0]
print(f"进度: {prog:.2f}s | 时长: {dur:.2f}s | 速率: {rate:.3f}")

# 窗口标题
import win32gui
import win32process

titles = []
def cb(hwnd, _):
    if win32gui.IsWindowVisible(hwnd):
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        if pid == pm.process_id:
            t = win32gui.GetWindowText(hwnd)
            if t:
                titles.append(t)
    return True

win32gui.EnumWindows(cb, None)
print(f"可见窗口标题 ({len(titles)} 个):")
for t in titles:
    print(f"  {t!r}")

# 也列出不可见窗口
all_titles = []
def cb2(hwnd, _):
    _, pid = win32process.GetWindowThreadProcessId(hwnd)
    if pid == pm.process_id:
        t = win32gui.GetWindowText(hwnd)
        if t:
            all_titles.append((win32gui.IsWindowVisible(hwnd), t))
    return True

win32gui.EnumWindows(cb2, None)
print(f"\n所有窗口标题 ({len(all_titles)} 个):")
for vis, t in all_titles:
    print(f"  [{'可见' if vis else '隐藏'}] {t!r}")
