# -*- coding: utf-8 -*-
"""检查网易云歌词来源：1.本地缓存文件 2.桌面歌词窗口 3.内存扫描"""
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# 1. 找本地歌词缓存
print("=== 1. 本地缓存检查 ===")
cache_paths = [
    os.path.expandvars(r"%LOCALAPPDATA%\Netease\CloudMusic"),
    os.path.expandvars(r"%APPDATA%\Netease\CloudMusic"),
]
for p in cache_paths:
    if os.path.isdir(p):
        print(f"目录存在: {p}")
        # 递归找歌词相关文件
        for root, dirs, files in os.walk(p):
            for f in files:
                if "lyric" in f.lower() or f.endswith((".lrc", ".lyric", ".ncm")):
                    fp = os.path.join(root, f)
                    size = os.path.getsize(fp)
                    print(f"  {fp} ({size} bytes)")
            # 只显示前几层
        break

# 2. 找桌面歌词窗口
print("\n=== 2. 桌面歌词窗口检查 ===")
import win32gui
import win32process

found_lyrics_win = []
def enum_cb(hwnd, _):
    cls = win32gui.GetClassName(hwnd)
    title = win32gui.GetWindowText(hwnd)
    if "Lyric" in cls or "lyric" in cls.lower() or "歌词" in title or "Lyric" in title:
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        found_lyrics_win.append((hwnd, cls, title, pid))
        print(f"  窗口: class={cls!r} title={title!r} pid={pid}")
    return True

win32gui.EnumWindows(enum_cb, None)
if not found_lyrics_win:
    print("  未找到桌面歌词窗口（桌面歌词可能未开启）")

# 3. 列出网易云 webdata 目录内容
print("\n=== 3. webdata 目录结构 ===")
webdata = os.path.expandvars(r"%LOCALAPPDATA%\Netease\CloudMusic\webdata")
if os.path.isdir(webdata):
    for root, dirs, files in os.walk(webdata):
        depth = root.replace(webdata, "").count(os.sep)
        if depth <= 2:
            for f in files:
                fp = os.path.join(root, f)
                print(f"  {fp} ({os.path.getsize(fp)} bytes)")
else:
    print("  webdata 目录不存在")

# 4. 检查歌词数据库文件
print("\n=== 4. 歌词数据库 ===")
for p in cache_paths:
    if os.path.isdir(p):
        for f in os.listdir(p):
            if f.endswith((".db", ".dat", ".json")) and ("lyric" in f.lower() or "lyr" in f.lower()):
                print(f"  {os.path.join(p, f)}")
