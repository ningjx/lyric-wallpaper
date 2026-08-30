# -*- coding: utf-8 -*-
"""探测网易云本地端口 20017 的 HTTP API 路径"""
import socket

PATHS = [
    # 网易云系常见路径
    "/", "/api", "/api/", "/eapi", "/eapi/", "/weapi", "/weapi/",
    "/api/status", "/status", "/api/player", "/player", "/api/play",
    "/api/v1/status", "/v1/status", "/api/v1/player", "/api/now",
    "/now", "/current", "/api/current", "/api/song", "/song",
    "/api/playback", "/playback", "/api/playback/status",
    "/state", "/api/state", "/info", "/api/info", "/meta", "/api/meta",
    "/version", "/api/version", "/health", "/api/health", "/ping",
    "/api/ping", "/remote", "/api/remote", "/remote/status",
    "/control", "/api/control", "/music", "/api/music",
    "/api/player/status", "/player/status", "/api/player/info",
    # WebSocket 握手探测
    "/ws", "/wsinfo", "/ws/info", "/websocket", "/api/ws",
    # 桌面歌词/状态相关
    "/lyric", "/api/lyric", "/lyrics", "/api/lyrics", "/desktop/lyric",
    "/progress", "/api/progress", "/position", "/api/position",
    # 网易云 music 库相关
    "/api/nmusic", "/nmusic", "/music/status", "/api/music/status",
]


def probe(path):
    try:
        s = socket.create_connection(("127.0.0.1", 20017), timeout=1.5)
        req = f"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"
        s.sendall(req.encode())
        s.settimeout(2)
        data = b""
        while True:
            try:
                chunk = s.recv(4096)
                if not chunk:
                    break
                data += chunk
            except socket.timeout:
                break
        s.close()
        if data:
            text = data.decode("utf-8", errors="replace")
            # 只显示非 404/400 的响应
            first_line = text.split("\r\n")[0] if "\r\n" in text else text[:50]
            if "404" not in first_line and "400" not in first_line or len(text) > 200:
                print(f"[{first_line}] {path}")
                print(f"    {text[:300]}")
        return data
    except Exception as e:
        return None


def ws_probe(path):
    """WebSocket 握手探测"""
    try:
        s = socket.create_connection(("127.0.0.1", 20017), timeout=1.5)
        key = "dGhlIHNhbXBsZSBub25jZQ=="
        req = (f"GET {path} HTTP/1.1\r\n"
               f"Host: 127.0.0.1\r\n"
               f"Upgrade: websocket\r\n"
               f"Connection: Upgrade\r\n"
               f"Sec-WebSocket-Key: {key}\r\n"
               f"Sec-WebSocket-Version: 13\r\n\r\n")
        s.sendall(req.encode())
        s.settimeout(2)
        data = s.recv(4096)
        s.close()
        text = data.decode("utf-8", errors="replace")
        print(f"[WS] {path} => {text[:200]}")
        return data
    except Exception as e:
        print(f"[WS] {path} => 失败: {e}")
        return None


print("=== HTTP 路径探测 ===")
for p in PATHS:
    probe(p)

print("\n=== WebSocket 握手探测 ===")
for p in ["/ws", "/wsinfo", "/ws/info", "/websocket", "/", "/api/ws"]:
    ws_probe(p)
