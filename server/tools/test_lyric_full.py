# -*- coding: utf-8 -*-
"""完整验证带 cookie 的歌词接口: lrc/tlyric/klyric 三个字段"""
import json
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
    "Referer": "https://music.163.com",
    "Cookie": "appver=2.10.6; os=pc;",
}

song_id = 167709

url = f"https://music.163.com/api/song/lyric?id={song_id}&lv=-1&kv=-1&tv=-1"
req = urllib.request.Request(url, headers=HEADERS)
resp = urllib.request.urlopen(req, timeout=10)
data = json.loads(resp.read())

print("响应完整结构:")
print(json.dumps({k: (v if not isinstance(v, str) else f"<{len(v)}字符>") for k, v in data.items()}, ensure_ascii=False, indent=2))

for field in ("lrc", "tlyric", "klyric"):
    val = data.get(field, {})
    if isinstance(val, dict):
        lyric = val.get("lyric", "")
        version = val.get("version", "")
    else:
        lyric = str(val)
        version = ""
    print(f"\n{field}: {len(lyric)} 字符, version={version}")
    if lyric:
        print(f"  首行: {lyric.splitlines()[0][:80]}")
        print(f"  末行: {lyric.splitlines()[-1][:80]}")
