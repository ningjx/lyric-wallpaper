"""LyricServer 绿色桌面入口：托盘、设置和内嵌 HTTP/SSE 服务。"""
from __future__ import annotations

import asyncio
import logging
import os
import shutil
import sys
from pathlib import Path

from . import autostart
from .config import (
    APP_NAME, ServerConfig, desktop_config_path, load_config, save_config,
    user_data_dir,
)
from .server import serve, setup_file_logging

try:
    from PySide6.QtCore import QThread, QUrl, Signal
    from PySide6.QtGui import QAction, QDesktopServices, QIcon
    from PySide6.QtWidgets import (
        QApplication, QCheckBox, QDialog, QDialogButtonBox, QFormLayout,
        QLabel, QMenu, QMessageBox, QPushButton, QStyle, QSystemTrayIcon,
        QVBoxLayout,
    )
except ImportError as exc:  # 让源码命令行仍可在未安装桌面依赖时工作
    raise RuntimeError("桌面模式需要 PySide6；请安装 server/requirements-desktop.txt") from exc


def _asset_path(name: str) -> Path:
    root = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
    return root / "server" / "assets" / name if getattr(sys, "frozen", False) else Path(__file__).parent / "assets" / name


def _desktop_config() -> ServerConfig:
    cfg = load_config(desktop_config_path())
    data_dir = Path(user_data_dir())
    data_dir.mkdir(parents=True, exist_ok=True)
    cfg.cache.data_dir = str(data_dir)
    cfg.http.log_file = str(data_dir / "logs" / "lyric-server.log")
    Path(cfg.http.log_file).parent.mkdir(parents=True, exist_ok=True)
    return cfg


class ServerWorker(QThread):
    started = Signal()
    failed = Signal(str)

    def __init__(self, cfg: ServerConfig) -> None:
        super().__init__()
        self._cfg = cfg
        self._loop: asyncio.AbstractEventLoop | None = None
        self._stop_event: asyncio.Event | None = None

    def run(self) -> None:
        async def runner() -> None:
            self._loop = asyncio.get_running_loop()
            self._stop_event = asyncio.Event()
            try:
                await serve(self._cfg, self._stop_event,
                            on_started=self.started.emit, console_status=False)
            except OSError as exc:
                if getattr(exc, "winerror", None) == 10048:
                    self.failed.emit("端口 9863 已被其他程序占用；请关闭旧版 LyricServer 或 Now Playing Service 后重试。")
                else:
                    self.failed.emit(f"无法启动本地服务：{exc}")
            except Exception as exc:
                logging.exception("LyricServer 意外退出")
                self.failed.emit(f"服务异常退出：{exc}")

        asyncio.run(runner())

    def request_stop(self) -> None:
        if self._loop is not None and self._stop_event is not None:
            self._loop.call_soon_threadsafe(self._stop_event.set)


class SettingsDialog(QDialog):
    def __init__(self, parent: "TrayApplication") -> None:
        super().__init__(parent)
        self._parent = parent
        self.setWindowTitle(f"{APP_NAME} 设置")
        self.setMinimumWidth(430)

        layout = QVBoxLayout(self)
        form = QFormLayout()
        self._autostart = QCheckBox("登录 Windows 后自动启动")
        self._autostart.setChecked(autostart.is_enabled())
        form.addRow("开机自启动", self._autostart)
        self._netease = QCheckBox("网易云音乐")
        self._netease.setChecked(parent.config.music.enable_netease)
        form.addRow("播放器", self._netease)
        self._apple = QCheckBox("Microsoft Store 版 Apple Music")
        self._apple.setChecked(parent.config.music.enable_apple)
        form.addRow("", self._apple)
        form.addRow("本地 API", QLabel("http://127.0.0.1:9863（壁纸固定使用）"))
        form.addRow("数据目录", QLabel(str(Path(user_data_dir()))))
        layout.addLayout(form)

        open_dir = QPushButton("打开数据目录")
        open_dir.clicked.connect(self._parent.open_data_directory)
        clear_cache = QPushButton("清理歌词缓存")
        clear_cache.clicked.connect(self._parent.clear_lyrics_cache)
        layout.addWidget(open_dir)
        layout.addWidget(clear_cache)

        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Save | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self._save)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def _save(self) -> None:
        try:
            if self._autostart.isChecked():
                autostart.enable()
            else:
                autostart.disable()
            self._parent.save_preferences(
                enable_netease=self._netease.isChecked(),
                enable_apple=self._apple.isChecked(),
            )
        except RuntimeError as exc:
            QMessageBox.warning(self, APP_NAME, str(exc))
            return
        self.accept()


class TrayApplication(QApplication):
    def __init__(self, argv: list[str]) -> None:
        super().__init__(argv)
        self.setQuitOnLastWindowClosed(False)
        self._worker: ServerWorker | None = None
        self.config = _desktop_config()
        self._restart_pending = False
        self._status = "正在启动"
        self._mutex = self._acquire_mutex()
        if self._mutex is None:
            QMessageBox.information(None, APP_NAME, "LyricServer 已在运行。")
            raise SystemExit(0)

        icon_path = _asset_path("tray.svg")
        icon = QIcon(str(icon_path)) if icon_path.is_file() else self.style().standardIcon(QStyle.StandardPixmap.SP_MediaPlay)
        self._tray = QSystemTrayIcon(icon, self)
        self._tray.setToolTip(f"{APP_NAME}：{self._status}")
        self._tray.setContextMenu(self._menu())
        self._tray.show()
        self.start_server()

    @staticmethod
    def _acquire_mutex():
        if sys.platform != "win32":
            return object()
        import ctypes
        mutex = ctypes.windll.kernel32.CreateMutexW(None, False, "Local\\LyricServer.Singleton")
        if not mutex:
            raise OSError("无法创建单实例锁")
        if ctypes.windll.kernel32.GetLastError() == 183:
            ctypes.windll.kernel32.CloseHandle(mutex)
            return None
        return mutex

    def _menu(self) -> QMenu:
        menu = QMenu()
        self._status_action = QAction("状态：正在启动", self)
        self._status_action.setEnabled(False)
        menu.addAction(self._status_action)
        menu.addSeparator()
        restart = QAction("重启服务", self)
        restart.triggered.connect(self.restart_server)
        menu.addAction(restart)
        settings = QAction("设置", self)
        settings.triggered.connect(self.open_settings)
        menu.addAction(settings)
        logs = QAction("打开日志目录", self)
        logs.triggered.connect(self.open_data_directory)
        menu.addAction(logs)
        menu.addSeparator()
        about = QAction("关于", self)
        about.triggered.connect(self.show_about)
        menu.addAction(about)
        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.quit_application)
        menu.addAction(quit_action)
        return menu

    def _set_status(self, status: str) -> None:
        self._status = status
        self._status_action.setText(f"状态：{status}")
        self._tray.setToolTip(f"{APP_NAME}：{status}")

    def start_server(self) -> None:
        if self._worker is not None and self._worker.isRunning():
            return
        self.config = _desktop_config()
        setup_file_logging(self.config)
        self._set_status("正在启动")
        self._worker = ServerWorker(self.config)
        self._worker.started.connect(lambda: self._set_status("运行中（127.0.0.1:9863）"))
        self._worker.failed.connect(self._server_failed)
        self._worker.finished.connect(self._server_finished)
        self._worker.start()

    def restart_server(self) -> None:
        if self._worker is not None and self._worker.isRunning():
            self._set_status("正在重启")
            self._restart_pending = True
            self._worker.request_stop()
            return
        self.start_server()

    def _server_failed(self, message: str) -> None:
        self._set_status("启动失败")
        self._tray.showMessage(APP_NAME, message, QSystemTrayIcon.MessageIcon.Critical)

    def _server_finished(self) -> None:
        self._worker = None
        if self._restart_pending:
            self._restart_pending = False
            self.start_server()
        elif self._status != "启动失败":
            self._set_status("已停止")

    def open_settings(self) -> None:
        SettingsDialog(self).exec()

    def save_preferences(self, *, enable_netease: bool, enable_apple: bool) -> None:
        if not enable_netease and not enable_apple:
            raise RuntimeError("至少需要保留一个播放器数据源")
        changed = (self.config.music.enable_netease != enable_netease
                   or self.config.music.enable_apple != enable_apple)
        self.config.music.enable_netease = enable_netease
        self.config.music.enable_apple = enable_apple
        save_config(self.config, desktop_config_path())
        if changed:
            self.restart_server()

    def open_data_directory(self) -> None:
        path = Path(user_data_dir())
        path.mkdir(parents=True, exist_ok=True)
        QDesktopServices.openUrl(QUrl.fromLocalFile(str(path)))

    def clear_lyrics_cache(self) -> None:
        cache = Path(user_data_dir()) / "lyrics"
        if not cache.exists():
            QMessageBox.information(None, APP_NAME, "当前没有可清理的歌词缓存。")
            return
        if QMessageBox.question(None, APP_NAME, "确定清理已缓存的歌词吗？") != QMessageBox.StandardButton.Yes:
            return
        shutil.rmtree(cache)
        QMessageBox.information(None, APP_NAME, "歌词缓存已清理。")

    def show_about(self) -> None:
        QMessageBox.information(None, APP_NAME, "LyricServer\n为动态歌词壁纸提供本机播放状态、歌词与 SSE 服务。")

    def quit_application(self) -> None:
        self._restart_pending = False
        if self._worker is not None and self._worker.isRunning():
            self._set_status("正在停止")
            self._worker.request_stop()
            self._worker.wait(5000)
        self._tray.hide()
        self.quit()


def main() -> None:
    if sys.platform != "win32":
        raise SystemExit("LyricServer 桌面程序仅支持 Windows。")
    app = TrayApplication(sys.argv)
    raise SystemExit(app.exec())


if __name__ == "__main__":
    main()
