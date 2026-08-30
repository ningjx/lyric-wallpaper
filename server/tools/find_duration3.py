# -*- coding: utf-8 -*-
"""API 查「温泉 - 许嵩/刘美麟」时长 -> 内存定位时长字段"""
import json
import struct
import sys
import time
import urllib.parse
import urllib.request

import pymem

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# 1. API 查时长
url = "https://music.163.com/api/search/get/web?s=" + urllib.parse.quote("温泉 许嵩") + "&type=1&limit=3"
req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0", "Referer": "https://music.163.com"})
resp = urllib.request.urlopen(req, timeout=10)
data = json.loads(resp.read())
songs = data.get("result", {}).get("songs", [])
for s in songs:
    print(f"API: {s['name']} - {s['artists'][0]['name']} 时长 {s['duration'] / 1000:.2f}s (id={s['id']})")

if not songs:
    print("查不到")
    sys.exit(1)

duration_s = songs[0]["duration"] / 1000
print(f"\n目标时长: {duration_s:.2f}s")

# 2. 内存扫描
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
cur_pos = struct.unpack("<d", pm.read_bytes(progress_addr, 8))[0]
print(f"当前进度: {cur_pos:.2f}s")

print("\n扫描 cloudmusic.dll 找时长值...")
data = pm.read_bytes(base, size)
matches = []
for i in range(0, len(data) - 8, 8):
    v = struct.unpack_from("<d", data, i)[0]
    if abs(v - duration_s) < 0.5:
        matches.append(base + i)
        print(f"  命中: dll+0x{i:X} 值={v:.3f}")

if matches:
    nearest = min(matches, key=lambda a: abs(a - progress_addr))
    print(f"\n距进度最近: dll+0x{nearest - base:X} (距离 {(nearest - progress_addr):+d})")
else:
    print("DLL 中无匹配，扫描进度所在页 ±1MB...")
    for region_start, region_size in [
        (progress_addr - 0x100000, 0x200000),
    ]:
        region = pm.read_bytes(region_start, region_size)
        heap_matches = []
        for i in range(0, len(region) - 8, 8):
            v = struct.unpack_from("<d", region, i)[0]
            if abs(v - duration_s) < 0.5:
                heap_matches.append(region_start + i)
        if heap_matches:
            print(f"堆命中 {len(heap_matches)} 处:")
            for a in heap_matches[:20]:
                print(f"  0x{a:X} (距进度 {a - progress_addr:+d})")
            nearest = min(heap_matches, key=lambda a: abs(a - progress_addr))
            print(f"\n距进度最近: 0x{nearest:X} (距离 {(nearest - progress_addr):+d})")
        else:
            print("±1MB 内无匹配")
