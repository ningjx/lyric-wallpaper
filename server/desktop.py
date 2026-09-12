"""LyricServer 绿色桌面入口：托盘和内嵌 HTTP/SSE 服务。"""
from __future__ import annotations

import asyncio
import logging
import os
import sys
from pathlib import Path

from . import autostart
from .config import (
    APP_NAME, APP_VERSION, ServerConfig, desktop_config_path, load_config,
    user_data_dir,
)
from .server import serve, setup_file_logging

try:
    from PySide6.QtCore import QThread, QUrl, Qt, Signal
    from PySide6.QtGui import QAction, QDesktopServices, QIcon
    from PySide6.QtWidgets import (
        QApplication, QDialog, QFrame, QHBoxLayout, QLabel, QMenu,
        QMessageBox, QPushButton, QStyle, QSystemTrayIcon, QToolButton,
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


class AboutDialog(QDialog):
    """不依赖系统信息框的轻量品牌说明窗口。"""

    def __init__(self, parent: "TrayApplication") -> None:
        # QApplication 不是 QWidget，不能作为 QDialog 的 Qt 父对象；仅借用它的图标。
        super().__init__()
        self.setWindowFlags(Qt.WindowType.FramelessWindowHint | Qt.WindowType.Dialog)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setModal(True)
        self.setFixedSize(464, 310)
        self.setWindowIcon(parent.windowIcon())
        self._drag_origin = None

        self.setStyleSheet("""
            QFrame#card {
                background: qlineargradient(x1: 0, y1: 0, x2: 1, y2: 1,
                    stop: 0 #FCFEFF, stop: 0.54 #F6FAFD, stop: 1 #EEF5FA);
                border: 1px solid #D5E2EC;
                border-radius: 22px;
            }
            QLabel#eyebrow { color: #167DB1; font-size: 10px; font-weight: 700; letter-spacing: 1.7px; }
            QLabel#title { color: #17354C; font-size: 25px; font-weight: 700; }
            QLabel#version { color: #60788C; font-size: 11px; }
            QLabel#description { color: #42596A; font-size: 13px; line-height: 1.45; }
            QLabel#section { color: #6B8293; font-size: 10px; font-weight: 700; letter-spacing: 1.2px; }
            QLabel#link a { color: #167DB1; font-size: 13px; text-decoration: none; }
            QLabel#link a:hover { color: #075D8C; text-decoration: underline; }
            QFrame#divider { background: #DDE7EE; }
            QToolButton#close {
                color: #60788C; background: transparent; border: 0; border-radius: 12px;
                font-size: 19px; font-weight: 300;
            }
            QToolButton#close:hover { color: #17354C; background: #E5F1F8; }
            QPushButton#done {
                color: #FFFFFF; background: #167DB1; border: 1px solid #0D6E9E;
                border-radius: 9px; padding: 7px 22px; font-size: 12px; font-weight: 600;
            }
            QPushButton#done:hover { background: #2498D2; }
            QPushButton#done:pressed { background: #10618D; }
        """)

        root = QVBoxLayout(self)
        root.setContentsMargins(8, 8, 8, 8)
        card = QFrame()
        card.setObjectName("card")
        root.addWidget(card)
        layout = QVBoxLayout(card)
        layout.setContentsMargins(28, 20, 28, 22)
        layout.setSpacing(0)

        top = QHBoxLayout()
        eyebrow = QLabel("LOCAL LYRIC SERVICE")
        eyebrow.setObjectName("eyebrow")
        top.addWidget(eyebrow)
        top.addStretch()
        close = QToolButton()
        close.setObjectName("close")
        close.setText("×")
        close.setToolTip("关闭")
        close.setFixedSize(28, 28)
        close.clicked.connect(self.reject)
        top.addWidget(close)
        layout.addLayout(top)
        layout.addSpacing(12)

        hero = QHBoxLayout()
        icon = QLabel()
        icon.setPixmap(parent.windowIcon().pixmap(58, 58))
        icon.setFixedSize(58, 58)
        hero.addWidget(icon)
        hero.addSpacing(14)
        name = QVBoxLayout()
        title = QLabel(APP_NAME)
        title.setObjectName("title")
        name.addWidget(title)
        version = QLabel(f"版本 {APP_VERSION}  ·  Windows 本地服务")
        version.setObjectName("version")
        name.addWidget(version)
        name.addStretch()
        hero.addLayout(name)
        hero.addStretch()
        layout.addLayout(hero)
        layout.addSpacing(17)

        description = QLabel("为动态歌词壁纸提供稳定的播放状态、逐行歌词与 SSE 推送。")
        description.setObjectName("description")
        description.setWordWrap(True)
        layout.addWidget(description)
        layout.addSpacing(17)
        divider = QFrame()
        divider.setObjectName("divider")
        divider.setFixedHeight(1)
        layout.addWidget(divider)
        layout.addSpacing(14)

        project_label = QLabel("项目主页")
        project_label.setObjectName("section")
        layout.addWidget(project_label)
        layout.addSpacing(5)
        link = QLabel('<a href="https://github.com/ningjx/lyric-wallpaper">github.com/ningjx/lyric-wallpaper ↗</a>')
        link.setObjectName("link")
        link.setOpenExternalLinks(True)
        layout.addWidget(link)
        layout.addStretch()

        footer = QHBoxLayout()
        footer.addStretch()
        done = QPushButton("完成")
        done.setObjectName("done")
        done.setDefault(True)
        done.clicked.connect(self.accept)
        footer.addWidget(done)
        layout.addLayout(footer)

    def mousePressEvent(self, event) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._drag_origin = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
            event.accept()

    def mouseMoveEvent(self, event) -> None:
        if self._drag_origin is not None and event.buttons() & Qt.MouseButton.LeftButton:
            self.move(event.globalPosition().toPoint() - self._drag_origin)
            event.accept()

    def mouseReleaseEvent(self, event) -> None:
        self._drag_origin = None
        super().mouseReleaseEvent(event)


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
        self.setWindowIcon(icon)
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
        self._autostart_action = QAction("开机启动", self)
        self._autostart_action.setCheckable(True)
        self._autostart_action.setChecked(autostart.is_enabled())
        self._autostart_action.toggled.connect(self.set_autostart)
        menu.addAction(self._autostart_action)
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

    def set_autostart(self, enabled: bool) -> None:
        try:
            if enabled:
                autostart.enable()
            else:
                autostart.disable()
        except RuntimeError as exc:
            self._autostart_action.blockSignals(True)
            self._autostart_action.setChecked(autostart.is_enabled())
            self._autostart_action.blockSignals(False)
            self._tray.showMessage(APP_NAME, str(exc), QSystemTrayIcon.MessageIcon.Warning)

    def open_data_directory(self) -> None:
        path = Path(user_data_dir())
        path.mkdir(parents=True, exist_ok=True)
        QDesktopServices.openUrl(QUrl.fromLocalFile(str(path)))

    def show_about(self) -> None:
        AboutDialog(self).exec()

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
