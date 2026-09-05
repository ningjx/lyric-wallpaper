# -*- coding: utf-8 -*-
"""网易云 eapi 加密。

移植自 Widdit/now-playing-service 的 EapiHelper（原作者 WXRIW，Apache-2.0）：
  - 参数 json 内多塞一个 "header" 字段（设备/客户端信息）
  - message = "nobody" + path + "use" + json + "md5forencrypt" 做 MD5
  - data    = path + "-36cd479b6b5-" + json + "-36cd479b6b5-" + digest
  - AES-128-ECB/PKCS7 加密后 hex 大写，作为唯一表单参数 "params" POST。
"""
from __future__ import annotations

import hashlib
import json
import time
from typing import Dict

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

# eapi 固定密钥（16 字节）
_EAPI_KEY = b"e82ckenh8dichen8"

# 请求 URL 前缀 -> 参与加密的 path 前缀映射
_URL_PREFIXES = (
    "https://interface3.music.163.com/e",
    "https://interface.music.163.com/e",
)

USER_AGENT = ("Mozilla/5.0 (Linux; Android 9; PCT-AL10) "
              "AppleWebKit/537.36 (KHTML, like Gecko) "
              "Chrome/70.0.3538.64 HuaweiBrowser/10.0.3.311 "
              "Mobile Safari/537.36")


def _build_header() -> Dict[str, str]:
    now_ms = int(time.time() * 1000)
    now_s = now_ms // 1000
    return {
        "__csrf": "",
        "appver": "8.0.0",
        "buildver": str(now_s),
        "channel": "",
        "deviceId": "",
        "mobilename": "",
        "resolution": "1920x1080",
        "os": "android",
        "osver": "",
        "requestId": f"{now_ms}_{now_ms % 10000:04d}",
        "versioncode": "140",
        "MUSIC_U": "",
    }


def _url_path(url: str) -> str:
    for prefix in _URL_PREFIXES:
        if url.startswith(prefix):
            return "/" + url[len(prefix):].lstrip("/")
    # 兜底：直接取 path 部分
    return "/" + url.split("/", 3)[-1]


def _aes_ecb_encrypt(plaintext: bytes, key: bytes) -> bytes:
    padder = padding.PKCS7(128).padder()
    padded = padder.update(plaintext) + padder.finalize()
    encryptor = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
    return encryptor.update(padded) + encryptor.finalize()


def eapi_encrypt(url: str, data: Dict[str, str]) -> str:
    """给定 eapi URL 与业务参数字典，返回加密后的 `params` 值（hex 大写）。

    `data` 会被就地加入 `header` 字段（与 Widdit 行为一致），调用方无需重复塞。
    """
    path = _url_path(url)
    payload = dict(data)
    payload["header"] = json.dumps(_build_header(), ensure_ascii=False)
    text = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))

    message = "nobody" + path + "use" + text + "md5forencrypt"
    digest = hashlib.md5(message.encode("utf-8")).hexdigest()

    data_str = path + "-36cd479b6b5-" + text + "-36cd479b6b5-" + digest
    return _aes_ecb_encrypt(data_str.encode("utf-8"), _EAPI_KEY).hex().upper()