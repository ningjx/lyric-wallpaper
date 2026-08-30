# -*- coding: utf-8 -*-
"""通过窗口标题获取当前歌曲 -> 查 API 拿时长 -> 内存中定位时长字段"""
import struct
import sys
import time

import pymem
import win32gui
import win32process

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

# 1. 从窗口标题获取当前歌曲
title = ""
def enum_cb(hwnd, _):
    global title
    if win32gui.IsWindowVisible(hwnd):
        t = win32gui.GetWindowText(hwnd)
        if " - " in t and "cloudmusic" not in t.lower():
            # 排除浏览器等，取网易云进程的窗口
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid == pm.process_id:
                title = t
                return False
    return True

win32gui.EnumWindows(enum_cb, None)
print(f"当前窗口标题: {title}")

# 2. 用公共 API 查歌曲时长（网易云官方未加密接口）
import urllib.request
import urllib.parse
import json

song_name = title.split(" - ")[-1] if " - " in title else title
print(f"搜索歌曲: {song_name}")

url = "https://music.163.com/api/search/get/web?s=" + urllib.parse.quote(song_name) + "&type=1&limit=3"
req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0", "Referer": "https://music.163.com"})
resp = urllib.request.urlopen(req, timeout=10)
data = json.loads(resp.read())
songs = data.get("result", {}).get("songs", [])
if songs:
    s = songs[0]
    duration_ms = s.get("duration", 0)
    duration_s = duration_ms / 1000
    print(f"API 查到: {s['name']} - {s['artists'][0]['name']}, 时长 {duration_s:.2f}s")
else:
    print("API 查不到，改用本地文件")
    sys.exit(1)

# 3. 在内存中找这个时长值
print(f"\n在 cloudmusic.dll 中查找 float64 值 {duration_s:.2f}...")
data = pm.read_bytes(base, size)
matches = []
for i in range(0, len(data) - 8, 8):
    v = struct.unpack_from("<d", data, i)[0]
    if abs(v - duration_s) < 0.5:
        matches.append(base + i)
        print(f"  命中: dll+0x{i:X} (0x{base + i:X}) 值={v:.3f}")

if matches:
    print(f"\n共 {len(matches)} 处内存含有当前歌曲时长")
    print("最接近进度值的那个最可能是同一结构中的时长字段")
    nearest = min(matches, key=lambda a: abs(a - progress_addr))
    print(f"距进度最近: 0x{nearest:X} (dll+0x{nearest - base:X}), 距离 {(nearest - progress_addr):+d} 字节")
else:
    print("DLL 中没有找到（时长可能存储在堆内存中，需要指针链）")
    # 尝试在堆区域找 - 扫描进度值所在的内存区域更大范围
    print("\n尝试扫描进度值所在页面 ±64KB...")
    region = pm.read_bytes(progress_addr - 0x10000, 0x20000)
    heap_matches = []
    for i in range(0, len(region) - 8, 8):
        v = struct.unpack_from("<d", region, i)[0]
        if abs(v - duration_s) < 0.5:
            heap_matches.append(progress_addr - 0x10000 + i)
            print(f"  命中: 0x{progress_addr - 0x10000 + i:X} (距进度 {i - 0x10000:+d})")
    if heap_matches:
        nearest = min(heap_matches, key=lambda a: abs(a - progress_addr))
        print(f"\n距进度最近: 0x{nearest:X}, 距离 {(nearest - progress_addr):+d} 字节")
    else:
        print("还是没找到，时长在堆上且距离进度较远")
