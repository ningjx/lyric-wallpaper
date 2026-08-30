# -*- coding: utf-8 -*-
"""测试多种歌词 API 端点"""
import json
import sys
import urllib.parse
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
    "Referer": "https://music.163.com",
}

song_id = 167709  # 河山大好 - 许嵩

# 尝试多个歌词端点
endpoints = [
    ("官方 api/song/lyric", f"https://music.163.com/api/song/lyric?id={song_id}&lv=1&kv=1&tv=-1"),
    ("官方 api/song/lyric (无参数)", f"https://music.163.com/api/song/lyric?id={song_id}"),
    ("weapi 风格 (POST)", None),  # 需要加密，跳过
    ("第三方 lrclib", f"https://lrclib.net/api/search?q=" + urllib.parse.quote("河山大好 许嵩")),
]

for name, url in endpoints:
    if url is None:
        continue
    print(f"=== {name} ===")
    try:
        req = urllib.request.Request(url, headers={**HEADERS, "Accept": "application/json"})
        resp = urllib.request.urlopen(req, timeout=10)
        raw = resp.read()
        print(f"  HTTP {resp.status}, {len(raw)} bytes")
        try:
            data = json.loads(raw)
            if isinstance(data, dict):
                lrc = data.get("lrc", {})
                if isinstance(lrc, dict):
                    lyric = lrc.get("lyric", "")
                else:
                    lyric = str(lrc)
                print(f"  lrc: {len(lyric)} 字符")
                print(f"  前 2 行: {lyric[:120]!r}")
            elif isinstance(data, list):
                print(f"  列表 {len(data)} 项")
                if data:
                    d = data[0]
                    print(f"  第一项: {d.get('trackName')} - {d.get('artistName')}")
                    print(f"  syncedLyrics: {str(d.get('syncedLyrics'))[:120]!r}")
        except json.JSONDecodeError:
            print(f"  非 JSON: {raw[:100]!r}")
    except Exception as e:
        print(f"  失败: {e}")
    print()

# 网易云 weapi 歌词（需要加密参数，但 lv=-1 的接口也许可用）
print("=== 网易云带 cookie 的歌词接口 ===")
try:
    req = urllib.request.Request(
        f"https://music.163.com/api/song/lyric?id={song_id}&lv=-1&kv=-1&tv=-1",
        headers={**HEADERS, "Cookie": "appver=2.10.6; os=pc;"},
    )
    resp = urllib.request.urlopen(req, timeout=10)
    data = json.loads(resp.read())
    lrc = data.get("lrc", {})
    lyric = lrc.get("lyric", "") if isinstance(lrc, dict) else str(lrc)
    print(f"  lrc: {len(lyric)} 字符")
    print(f"  前 3 行: {lyric[:200]!r}")
except Exception as e:
    print(f"  失败: {e}")
