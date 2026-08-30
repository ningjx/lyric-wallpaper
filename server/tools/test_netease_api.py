# -*- coding: utf-8 -*-
"""测试网易云公开 API 是否可用"""
import json
import sys
import urllib.parse
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
    "Referer": "https://music.163.com",
}

# 1. 搜索 API
print("=== 搜索 API 测试 ===")
url = "https://music.163.com/api/search/get/web?s=" + urllib.parse.quote("河山大好 许嵩") + "&type=1&limit=3"
req = urllib.request.Request(url, headers=HEADERS)
try:
    resp = urllib.request.urlopen(req, timeout=10)
    data = json.loads(resp.read())
    songs = data.get("result", {}).get("songs", [])
    for s in songs:
        album = s.get("album") or {}
        pic = album.get("picUrl", "")
        print(f"  {s['id']} | {s['name']} - {s['artists'][0]['name']} | 时长 {s['duration']/1000}s")
        print(f"    专辑: {album.get('name', '')} | 封面: {pic[:60]}...")
    if songs:
        song_id = songs[0]["id"]
    else:
        song_id = None
except Exception as e:
    print(f"  搜索失败: {e}")
    song_id = None

# 2. 歌词 API
if song_id:
    print(f"\n=== 歌词 API 测试 (id={song_id}) ===")
    url = f"https://music.163.com/api/song/lyric?id={song_id}&lv=1&kv=1&tv=-1"
    req = urllib.request.Request(url, headers=HEADERS)
    try:
        resp = urllib.request.urlopen(req, timeout=10)
        data = json.loads(resp.read())
        lrc = data.get("lrc", {}).get("lyric", "")
        tlyric = data.get("tlyric", {}).get("lyric", "")
        klyric = data.get("klyric", {}).get("lyric", "")
        print(f"  lrc 长度: {len(lrc)}")
        print(f"  tlyric 长度: {len(tlyric)}")
        print(f"  klyric 长度: {len(klyric)}")
        print(f"  lrc 前 3 行:")
        for line in lrc.split("\n")[:3]:
            print(f"    {line}")
    except Exception as e:
        print(f"  歌词获取失败: {e}")

# 3. 详细歌曲信息 API（可选，获取完整元数据）
if song_id:
    print(f"\n=== 歌曲详情 API 测试 ===")
    url = f"https://music.163.com/api/song/detail/?id={song_id}&ids=[{song_id}]"
    req = urllib.request.Request(url, headers=HEADERS)
    try:
        resp = urllib.request.urlopen(req, timeout=10)
        data = json.loads(resp.read())
        s = data.get("songs", [{}])[0]
        print(f"  name: {s.get('name')}")
        print(f"  album: {s.get('album', {}).get('name')}")
        print(f"  duration: {s.get('duration', 0) / 1000}s")
    except Exception as e:
        print(f"  详情获取失败: {e}")
