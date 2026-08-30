# -*- coding: utf-8 -*-
"""网易云音乐 3.1.28 播放进度内存读取 - 完整验证"""
import struct
import sys
import time

import pymem

# 修复 Windows 控制台 GBK 编码问题
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

pm = pymem.Pymem("cloudmusic.exe")

base = None
for module in pm.list_modules():
    if module.name.lower() == "cloudmusic.dll":
        base = module.lpBaseOfDll
        break

if base is None:
    print("未找到 cloudmusic.dll")
    sys.exit(1)

# 已验证的偏移: float64 播放进度（秒）
PROGRESS_OFFSET = 0x1D808F8

print(f"cloudmusic.dll 基址: 0x{base:X}")
print(f"播放进度地址: 0x{base + PROGRESS_OFFSET:X} (dll+0x{PROGRESS_OFFSET:X})")
print()
print("现在请配合做以下测试：")
print("  1. 保持播放 10 秒（看值是否每秒+1）")
print("  2. 然后按暂停 5 秒（看值是否冻结）")
print("  3. 再继续播放（看值是否恢复）")
print("  4. 再拖动进度条（看值是否跳变）")
print()

last_val = None
last_t = None
paused_frozen = False

try:
    for i in range(60):
        t = time.time()
        try:
            val = struct.unpack("<d", pm.read_bytes(base + PROGRESS_OFFSET, 8))[0]
            m, s = divmod(val, 60)
            if last_val is not None:
                dt = t - last_t
                dv = val - last_val
                if abs(dv) < 0.05 and abs(dv - dt) > 0.2:
                    state = "[冻结-已暂停]"
                elif abs(dv - dt) < 0.3:
                    state = "[播放中]"
                elif abs(dv) > 1.0:
                    state = "[跳变-拖进度条]"
                else:
                    state = f"[Δv={dv:.2f} Δt={dt:.2f}]"
                print(f"  {time.strftime('%H:%M:%S')} | {int(m):02d}:{s:05.2f} | {val:8.3f}s | {state}")
            else:
                print(f"  {time.strftime('%H:%M:%S')} | {int(m):02d}:{s:05.2f} | {val:8.3f}s | 初始")
            last_val = val
            last_t = t
        except Exception as e:
            print(f"  读取失败: {e}")
            last_val = None
            last_t = None
        time.sleep(1)
except KeyboardInterrupt:
    pass

print()
print("验证完成。如果看到 [播放中]/[冻结-已暂停]/[跳变] 均符合预期，")
print(f"则偏移 dll+0x{PROGRESS_OFFSET:X} 确认有效。")
