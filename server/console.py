# -*- coding: utf-8 -*-
"""
控制台输出：底部实时状态行 + 上方滚动日志。

底部一行实时刷新播放状态（原地刷新、不换行，带旋转动画，类似 docker pull）；
其余日志（启动横幅、偏移探测、切歌等）在状态行上方正常换行打印。
所有输出经同一把锁串行化，避免后台探测线程与主线程的输出互相打断。

用法：
  from console import console
  console.log("普通日志")              # 状态行上方打印一行，随后重绘状态行
  console.set_status("播放中 ...")      # 原地刷新底部状态行（不换行）
  console.stop("已停止。")              # 清除状态行并打印终止日志
"""
import atexit
import sys
import threading

# 旋转动画帧（braille 圆点，docker / kubernetes 风格）
SPINNER = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"]

# 隐藏/显示终端光标（隐藏后状态行末尾不再出现闪烁的输入光标）
HIDE_CURSOR = "\x1b[?25l"
SHOW_CURSOR = "\x1b[?25h"
RESET = "\x1b[0m"
BOLD = "\x1b[1m"
DIM = "\x1b[2m"
GRAY = "\x1b[90m"
RED = "\x1b[91m"
GREEN = "\x1b[92m"
YELLOW = "\x1b[93m"
# 24-bit 品牌色（Windows Terminal / PowerShell 7 均支持）
NETEASE_RED = "\x1b[38;2;255;0;0m"       # #FF0000
APPLE_PINK = "\x1b[38;2;251;66;90m"      # #FB425A
CYAN = "\x1b[96m"


def _enable_vt():
    """Windows 控制台开启 ANSI 转义（\\x1b[K 清行需要），失败则静默忽略"""
    if sys.platform == "win32":
        try:
            import ctypes
            kernel32 = ctypes.windll.kernel32
            h = kernel32.GetStdHandle(-11)  # STD_OUTPUT_HANDLE
            mode = ctypes.c_uint32()
            kernel32.GetConsoleMode(h, ctypes.byref(mode))
            kernel32.SetConsoleMode(h, mode.value | 0x0004)  # ENABLE_VIRTUAL_TERMINAL_PROCESSING
        except Exception:
            pass


class Console:
    def __init__(self):
        self._lock = threading.Lock()
        self._status = ""
        self._visible = False
        self._frame = 0
        self._cursor_hidden = False
        # 非交互（重定向到文件）时不启用原地刷新，避免输出控制字符
        self._tty = hasattr(sys.stdout, "isatty") and sys.stdout.isatty()

    def _raw(self, text: str):
        sys.stdout.write(text)
        sys.stdout.flush()

    def _paint(self, text: str, *colors: str) -> str:
        """交互终端使用 ANSI 色彩；重定向输出保持纯文本。"""
        return "".join(colors) + text + RESET if self._tty else text

    def _style_log(self, text: str) -> str:
        if text.startswith("Now Playing"):
            return self._paint(text, BOLD, CYAN)
        if text.startswith("  端点:"):
            return self._paint(text, DIM, GRAY)
        if text.startswith("  数据源:"):
            return self._paint(text, DIM, GRAY)
        if text.startswith("[offset-probe]"):
            color = GREEN if "✓" in text else YELLOW
            return self._paint(text, color)
        if text.startswith("Apple Music"):
            return self._paint(text, APPLE_PINK)
        if text.startswith("网易云音乐"):
            return self._paint(text, NETEASE_RED)
        if "不可用" in text or "失败" in text:
            return self._paint(text, RED)
        if text.startswith("✓"):
            return self._paint(text, GREEN)
        if text.startswith("▶"):
            return self._paint(text, CYAN)
        return text

    def _style_status(self, text: str) -> str:
        """为“状态 / 平台 / 歌词标注 / 歌名 / 进度”状态栏分别着色。

        兼容两种段数：4 段（无歌词标注）与 5 段（含 [词]/[··]/[无] 标注）。
        """
        parts = text.split("  ")
        if len(parts) == 4:
            state, platform, song, progress = parts
            tag = None
        elif len(parts) == 5:
            state, platform, tag, song, progress = parts
        else:
            return text
        state_color = GREEN if state == "播放中" else YELLOW
        platform_color = APPLE_PINK if platform == "Apple Music" else NETEASE_RED
        seg = [
            self._paint(state, BOLD, state_color),
            self._paint(platform, BOLD, platform_color),
        ]
        if tag is not None:
            tag_color = GREEN if tag == "[词]" else (YELLOW if tag == "[··]" else GRAY)
            seg.append(self._paint(tag, BOLD, tag_color))
        seg.append(self._paint(song, GRAY))
        seg.append(self._paint(progress, CYAN))
        return "  ".join(seg)

    def _clear_line(self):
        # 回到行首并清到行尾（覆盖旧状态行，避免残留字符）
        self._raw("\r\x1b[K")

    def _draw_status(self):
        if not self._visible:
            return
        if not self._cursor_hidden:
            self._raw(HIDE_CURSOR)
            self._cursor_hidden = True
        frame = SPINNER[self._frame % len(SPINNER)]
        self._raw("\r\x1b[K" + self._paint(frame, BOLD, CYAN) + " "
                  + self._style_status(self._status))

    def log(self, text: str):
        """状态行上方打印一条日志（换行），随后重绘底部状态行"""
        with self._lock:
            if not self._tty:
                self._raw(self._style_log(text) + "\n")
                return
            self._clear_line()
            self._raw(self._style_log(text) + "\n")
            self._draw_status()

    def set_status(self, text: str):
        """更新底部状态行（原地刷新，不换行），旋转动画前进一帧"""
        with self._lock:
            self._status = text
            self._visible = True
            self._frame += 1
            if self._tty:
                self._draw_status()

    def stop(self, text: str):
        """终止时打印最后一条日志，清除状态行并恢复光标"""
        with self._lock:
            if self._tty:
                self._clear_line()
                if self._cursor_hidden:
                    self._raw(SHOW_CURSOR)
                    self._cursor_hidden = False
            self._status = ""
            self._visible = False
            self._raw(text + "\n")


console = Console()
_enable_vt()


def _restore_cursor_on_exit():
    """进程退出前兜底恢复光标，避免异常退出时终端光标一直隐藏"""
    try:
        if console._cursor_hidden:
            sys.stdout.write(SHOW_CURSOR)
            sys.stdout.flush()
    except Exception:
        pass


atexit.register(_restore_cursor_on_exit)
