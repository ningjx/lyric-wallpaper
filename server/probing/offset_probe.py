# -*- coding: utf-8 -*-
"""
cloudmusic.dll 播放状态偏移自动探测模块
========================================
按 DLL 版本缓存偏移；版本不匹配或偏移失效时，自动扫描进程内存重新定位。

原理（继承 tools/mem_scan.py 已验证的启发式）：
  - 播放进度是 cloudmusic.dll 数据段里的一个 float64 全局变量，
    播放时以每秒 +1.0 匀速递增。
  - 扫整个 DLL 镜像，取两帧快照（间隔 3 秒），找 Δ≈3s 的 float64。
  - 进度值 +0x8 处是播放速率（1.0=播放 / 0.0=暂停），用于反歧义。
  - 时长在进度 +~0x60000 附近，是「>进度 且两帧不变」的稳定 float64。

用法：
  resolver = OffsetResolver()
  resolver.start()              # 后台线程：先试缓存，失败则自动探测
  offsets = resolver.current()  # dict {"progress","duration","rate"} 或 None（探测中）
  resolver.offset_state()       # "idle"|"probing"|"ready"|"failed"（供 /healthz）
"""
import json
import math
import os
import struct
import threading
import time

import pymem

PROCESS_NAME = "cloudmusic.exe"
MODULE_NAME = "cloudmusic.dll"

# 内置已知版本偏移，作为缓存默认值和快速路径。
# 缓存键用 cloudmusic.exe 的 FileVersion（程序可读）。
KNOWN_OFFSETS = {
    "3.1.28.8527": {   # 对应 About 显示的 3.1.28 (Build:205135)
        "progress": 0x1D808F8,
        "duration": 0x1DE1038,
        "rate": 0x1D80900,
    },
}

# 探测参数
PROGRESS_MIN = 0.0
PROGRESS_MAX = 3600.0
DELTA_LO = 2.5
DELTA_HI = 3.5
DELTA_LO_LOOSE = 1.5
DELTA_HI_LOOSE = 5.5
DURATION_NEAR = 0x60000
DURATION_TOL = 0x20000
SNAP_WAIT = 3.0
CONFIG_NAME = "offsets_config.json"


def log(msg: str) -> None:
    """后台线程内安全打印（经共享控制台，避免打断底部状态行）"""
    try:
        from ..console import console
        console.log(f"[offset-probe] {msg}")
    except Exception:  # 独立运行时包不可导入，退回纯终端打印
        print(f"[offset-probe] {msg}", flush=True)


def find_module(pm):
    """定位 cloudmusic.dll 基址与镜像大小，返回 (base, size) 或 (None, 0)"""
    try:
        for m in pm.list_modules():
            if m.name.lower() == MODULE_NAME:
                return m.lpBaseOfDll, m.SizeOfImage
    except Exception:
        pass
    return None, 0


def process_exists() -> bool:
    """网易云进程是否在运行。目标进程不存在属常态（非异常），安静等待即可。"""
    try:
        import psutil
        return any((p.info["name"] or "").lower() == PROCESS_NAME
                   for p in psutil.process_iter(["name"]))
    except Exception:
        return True  # 无法判断时交给 pymem 尝试，避免误判成「不存在」而空转


def read_version(pm, size=0, base=None):
    """读取网易云版本号（用作缓存键）。优先 cloudmusic.exe 文件版本。"""
    try:
        import psutil
        exe = psutil.Process(pm.process_id).exe()
        if exe:
            import win32api
            info = win32api.GetFileVersionInfo(exe, "\\")
            ms, ls = info["FileVersionMS"], info["FileVersionLS"]
            return (f"{win32api.HIWORD(ms)}.{win32api.LOWORD(ms)}."
                    f"{win32api.HIWORD(ls)}.{win32api.LOWORD(ls)}")
    except Exception:
        pass

    if base:
        try:
            e_lfanew = struct.unpack("<I", pm.read_bytes(base + 0x3C, 4))[0]
            ts = struct.unpack("<I", pm.read_bytes(base + e_lfanew + 8, 4))[0]
            return f"stamp_{ts:08X}_{size:x}"
        except Exception:
            pass

    return f"size_{size:x}"


def _read_image(pm, base, size):
    """整段读取 DLL 镜像；失败时降级为分块读取（不可读页填 0）"""
    try:
        return pm.read_bytes(base, size)
    except Exception:
        chunk = 0x100000
        buf = bytearray(size)
        for off in range(0, size, chunk):
            n = min(chunk, size - off)
            try:
                buf[off:off + n] = pm.read_bytes(base + off, n)
            except Exception:
                pass
        return bytes(buf)


def _scan_progress(data1, data2):
    hits = _scan_strict(data1, data2)
    if hits:
        return hits
    return _scan_loose(data1, data2)


def _scan_strict(data1, data2):
    return _scan_impl(data1, data2, DELTA_LO, DELTA_HI,
                      rate_check=lambda r: 0.99 <= r <= 1.01)


def _scan_loose(data1, data2):
    return _scan_impl(data1, data2, DELTA_LO_LOOSE, DELTA_HI_LOOSE,
                      rate_check=lambda r: abs(r) >= 0.5)


def _scan_impl(data1, data2, delta_lo, delta_hi, rate_check):
    """核心扫描：找 val∈[PROGRESS_MIN,PROGRESS_MAX] 且 Δ∈[delta_lo,delta_hi]
    且 +0x8 处速率满足 rate_check 的 float64，返回字节偏移列表。
    numpy 为必需依赖（向量化，快两个数量级）；缺 numpy 掉回纯 Python。"""
    hits = []

    try:
        import numpy as np
    except ImportError:
        np = None

    if np is not None:
        a1 = np.frombuffer(data1, dtype="<f8")
        a2 = np.frombuffer(data2, dtype="<f8")
        n = min(len(a1), len(a2))
        mask = (a1[:n] >= PROGRESS_MIN) & (a1[:n] <= PROGRESS_MAX)
        idx = np.nonzero(mask)[0]
        if idx.size == 0:
            return []
        delta = a2[idx] - a1[idx]
        sel = (delta >= delta_lo) & (delta <= delta_hi)
        for i in idx[sel]:
            if i + 1 < n and rate_check(float(a2[i + 1])):
                hits.append(int(i) * 8)
        return hits

    # 纯 Python 回退（慢，仅 numpy 缺失时）
    n = len(data1) - 8
    n2 = len(data2) - 8
    for i in range(0, n + 1, 8):
        v1 = struct.unpack_from("<d", data1, i)[0]
        if not (PROGRESS_MIN <= v1 <= PROGRESS_MAX):
            continue
        v2 = struct.unpack_from("<d", data2, i)[0]
        d = v2 - v1
        if delta_lo <= d <= delta_hi:
            if i + 8 <= n2:
                r = struct.unpack_from("<d", data2, i + 8)[0]
                if rate_check(r):
                    hits.append(i)
    return hits


def _find_duration(data1, data2, progress_off, cur_progress):
    lo = progress_off + DURATION_NEAR - DURATION_TOL
    hi = progress_off + DURATION_NEAR + DURATION_TOL
    best_off = None
    best_dist = 1 << 60
    n = min(len(data1), len(data2)) - 8
    for i in range(lo, min(hi, n + 1), 8):
        v1 = struct.unpack_from("<d", data1, i)[0]
        if not (math.isfinite(v1) and 10.0 <= v1 <= 86400.0 and v1 > cur_progress):
            continue
        v2 = struct.unpack_from("<d", data2, i)[0]
        if abs(v1 - v2) < 0.001:
            dist = abs(i - (progress_off + DURATION_NEAR))
            if dist < best_dist:
                best_dist = dist
                best_off = i
    return best_off


def probe(pm):
    """执行一次自动探测。返回偏移 dict 或 None。

    要求：网易云正在播放一首歌（进度 0~3600 秒）。
    """
    base, size = find_module(pm)
    if base is None:
        log("未找到 cloudmusic.dll")
        return None

    t0 = time.time()
    data1 = _read_image(pm, base, size)
    log(f"快照1 读取完成 ({size / 1024 / 1024:.1f} MB, {time.time() - t0:.1f}s)")

    time.sleep(SNAP_WAIT)

    data2 = _read_image(pm, base, size)
    log("快照2 读取完成")

    hits = _scan_progress(data1, data2)
    if not hits:
        log("未找到进度候选（歌曲可能暂停/未播放，或进度不在扫描范围）")
        return None
    log(f"进度候选 {len(hits)} 个")

    best = None
    for off in hits:
        cur = struct.unpack_from("<d", data2, off)[0]
        dur_off = _find_duration(data1, data2, off, cur)
        score = 0 if dur_off is None else 1
        if best is None or score > best["score"]:
            best = {"score": score, "progress": off, "duration": dur_off}
        if dur_off is not None:
            log(f"候选 progress=0x{off:X} duration=0x{dur_off:X} (Δ时长+0x{dur_off - off:X})")

    if best is None or best["duration"] is None:
        log("进度候选均无法定位到稳定时长字段，探测失败")
        return None

    return {
        "progress": best["progress"],
        "duration": best["duration"],
        "rate": best["progress"] + 8,  # 速率紧邻进度
    }


def _sane(vals):
    try:
        p, d, r = vals["progress"], vals["duration"], vals["rate"]
        if not (math.isfinite(p) and math.isfinite(d) and math.isfinite(r)):
            return False
        if not (0.0 <= p <= 86400.0):
            return False
        if not (10.0 <= d <= 86400.0):
            return False
        if not (abs(r) < 0.01 or 0.5 <= abs(r) <= 2.0):
            return False
        return True
    except Exception:
        return False


class OffsetResolver:
    """后台线程解析偏移：缓存 -> 已知版本 -> 自动探测 -> 持久化"""

    def __init__(self, config_path=None):
        self._lock = threading.Lock()
        self._offsets = None
        self._thread = None
        self._stop = False
        self._started = False
        self._state = "idle"  # idle / waiting / probing / ready / failed / stopped
        self._last_log_key = ""  # 日志去重：同一状态只报一次，避免后台轮询刷屏
        # 缓存仍在 server/ 根目录（与旧版本一致），换路径会让老用户重复探测一次
        self._config_path = config_path or os.path.join(
            os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
            CONFIG_NAME)

    def current(self):
        with self._lock:
            return self._offsets

    def offset_state(self):
        """对外暴露探测状态（/healthz）：idle/waiting/probing/ready/failed/stopped"""
        with self._lock:
            return self._state

    def start(self):
        with self._lock:
            if self._started:
                return
            self._started = True
            self._state = "idle"
        self._thread = threading.Thread(target=self._run, name="offset-probe", daemon=True)
        self._thread.start()

    def stop(self):
        self._stop = True
        with self._lock:
            if self._state != "ready":
                self._state = "stopped"

    # ---- 内部 ----
    def _load_store(self):
        store = {k: dict(v) for k, v in KNOWN_OFFSETS.items()}
        try:
            with open(self._config_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, dict):
                for ver, off in data.items():
                    if isinstance(off, dict) and {"progress", "duration", "rate"} <= off.keys():
                        store[ver] = {k: int(v) for k, v in off.items()}
        except FileNotFoundError:
            pass
        except Exception as e:
            log(f"读取缓存失败: {e}")
        return store

    def _save(self, ver, offsets):
        store = self._load_store()
        store[ver] = {k: int(v) for k, v in offsets.items()}
        try:
            with open(self._config_path, "w", encoding="utf-8") as f:
                json.dump(store, f, ensure_ascii=False, indent=2, sort_keys=True)
            log(f"已缓存偏移 -> {self._config_path} ({ver})")
        except Exception as e:
            log(f"写缓存失败: {e}")

    def _set_state(self, state):
        with self._lock:
            self._state = state

    def _set_offsets(self, offsets):
        with self._lock:
            self._offsets = offsets

    def _validate(self, pm, base, offsets):
        try:
            vals = {}
            for k, off in offsets.items():
                vals[k] = struct.unpack("<d", pm.read_bytes(base + off, 8))[0]
            return _sane(vals)
        except Exception:
            return False

    def _log_once(self, key: str, text: str) -> None:
        """按状态去重：同一状态只打印一条，避免后台轮询反复刷屏。「检测到就用，检测不到拉倒」。"""
        if self._last_log_key == key:
            return
        self._last_log_key = key
        log(text)

    def _run(self):
        backoff = 10.0
        self._set_state("probing")
        while not self._stop:
            pm = None
            try:
                # 进程/依赖不存在属常态：安静等待，不当作异常
                if not process_exists():
                    self._set_state("waiting")
                    self._log_once("wait", "等待网易云运行（检测到即启用）...")
                    time.sleep(backoff)
                    continue

                pm = pymem.Pymem(PROCESS_NAME)
                base, size = find_module(pm)
                if base is None:
                    self._set_state("waiting")
                    self._log_once("wait", "未找到 cloudmusic.dll，等待...")
                    time.sleep(backoff)
                    continue

                ver = read_version(pm, size, base)
                self._set_state("probing")
                self._log_once(f"ver:{ver}", f"检测到版本: {ver}")

                off = self._load_store().get(ver)
                if off and self._validate(pm, base, off):
                    self._set_offsets(dict(off))
                    self._set_state("ready")
                    self._last_log_key = ""  # 就绪后重置，切歌/下次切换可再报
                    log(f"✓ 偏移已就绪 (progress=0x{off['progress']:X} "
                        f"duration=0x{off['duration']:X} rate=0x{off['rate']:X})")
                    return

                self._log_once(f"probe:{ver}", "偏移失效或未知版本，开始自动探测（请确保正在播放歌曲）...")
                off = probe(pm)
                if off:
                    self._set_offsets(off)
                    self._set_state("ready")
                    self._save(ver, off)
                    self._last_log_key = ""
                    log(f"✓ 偏移已就绪 (progress=0x{off['progress']:X} "
                        f"duration=0x{off['duration']:X} rate=0x{off['rate']:X})")
                    return

                self._log_once(f"fail:{ver}", "探测失败（可能未在播放），静默重试")
                time.sleep(backoff)
                backoff = min(backoff * 1.5, 60.0)
            except Exception as e:
                self._set_state("failed")
                self._log_once(f"err:{type(e).__name__}", f"偏移解析异常: {e}，静默重试")
                time.sleep(backoff)
                backoff = min(backoff * 1.5, 60.0)
            finally:
                if pm is not None:
                    try:
                        pm.close_process()
                    except Exception:
                        pass


if __name__ == "__main__":
    # 独立运行：同步探测一次并打印结果（用于调试）
    import sys
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    resolver = OffsetResolver()
    resolver.start()
    while True:
        off = resolver.current()
        if off is not None:
            print(f"\n结果: {off}")
            print(f"  progress=0x{off['progress']:X}")
            print(f"  duration=0x{off['duration']:X}")
            print(f"  rate    =0x{off['rate']:X}")
            break
        time.sleep(0.5)