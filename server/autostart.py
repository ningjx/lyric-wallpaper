"""绿色版的“登录后启动”支持。

不注册 Windows Service：播放器进程与 SMTC 位于交互用户会话中，启动目录
快捷方式既无需管理员权限，也能让托盘程序在正确的会话内运行。
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

SHORTCUT_NAME = "LyricServer.lnk"


def startup_shortcut_path() -> Path:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        raise RuntimeError("无法确定 APPDATA 目录")
    return Path(appdata) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "Startup" / SHORTCUT_NAME


def executable_path() -> Path:
    return Path(sys.executable if getattr(sys, "frozen", False) else sys.argv[0]).resolve()


def is_enabled() -> bool:
    path = startup_shortcut_path()
    if not path.is_file():
        return False
    # 绿色 EXE 被移动后，旧快捷方式虽存在但已失效；界面将其视为未启用，
    # 用户勾选保存即可原地修复。
    if not getattr(sys, "frozen", False):
        return True
    try:
        from win32com.client import Dispatch
        shortcut = Dispatch("WScript.Shell").CreateShortCut(str(path))
        return Path(shortcut.Targetpath).resolve() == executable_path()
    except Exception:
        return False


def enable() -> None:
    """创建指向当前绿色 EXE 的登录启动快捷方式。"""
    if not getattr(sys, "frozen", False):
        raise RuntimeError("自启动快捷方式仅在打包后的 LyricServer.exe 中可用")
    path = startup_shortcut_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        from win32com.client import Dispatch
        shell = Dispatch("WScript.Shell")
        shortcut = shell.CreateShortCut(str(path))
        target = executable_path()
        shortcut.Targetpath = str(target)
        shortcut.WorkingDirectory = str(target.parent)
        shortcut.Description = "LyricServer 登录后启动"
        shortcut.Save()
    except Exception as exc:
        raise RuntimeError(f"创建自启动快捷方式失败：{exc}") from exc


def disable() -> None:
    path = startup_shortcut_path()
    if path.exists():
        path.unlink()
