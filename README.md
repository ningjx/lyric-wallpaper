# 动态歌词壁纸

一个 Windows 桌面歌词壁纸程序。它在主屏桌面图标后方用 D3D11 绘制滚动歌词、译文和液态玻璃效果，并在同一进程内采集网易云音乐或 Microsoft Store 版 Apple Music 的播放状态，查询和缓存歌词。

## 开始使用

1. [下载 Windows x64 单文件 EXE](https://github.com/ningjx/lyric-wallpaper/releases/latest/download/LyricWallpaper-win-x64.exe)，或在 [GitHub Releases](https://github.com/ningjx/lyric-wallpaper/releases) 查看版本记录。
2. 直接运行 `LyricWallpaper-win-x64.exe`。发布包自带 .NET 运行时。
3. 双击托盘图标，或右键选择「打开设置」。默认使用纯色背景，可以选择自己的图片。修改会实时应用并自动保存。

设置窗口顶部的「测试滚动」可以播放演示歌词，左下角的「开机自启」与托盘菜单同步。程序读取 `%LOCALAPPDATA%\LyricServer\config.json`，壁纸设置保存在同目录的 `wallpaper.json`。

## 从源码构建

在 Windows x64 上安装 .NET 10 SDK，然后在仓库根目录运行以下命令。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-native-wallpaper.ps1
```

构建产物为 `release/LyricWallpaper-win-x64.exe`。更多启动参数、DPR 和全场模糊的测试结果见 [原生版使用说明](native-wallpaper/README.md)。

## 项目结构

| 目录 | 用途 |
| --- | --- |
| [`native-wallpaper/`](native-wallpaper/) | D3D11 桌面渲染、WPF 设置和托盘、进程内播放器采集与歌词服务 |
| [`server/`](server/) | 旧版 Wallpaper Engine 使用的独立 Python HTTP 歌词服务 |
| [`scripts/`](scripts/) | Windows 发布包与歌词采集构建脚本 |

## Wallpaper Engine 旧版

请到 [`codex/wallpaper-engine` 分支](https://github.com/ningjx/lyric-wallpaper/tree/codex/wallpaper-engine) 查看旧客户端、WebGL 渲染器和相应的构建说明。切换到原生版前可先退出旧歌词壁纸和旧托盘服务。两个版本沿用同一目录下的歌词采集配置。

## 许可证与致谢

项目按 [Apache License 2.0](LICENSE) 发布。歌词服务的设计参考 [Widdit/now-playing-service](https://github.com/Widdit/now-playing-service)，原生设置与托盘采用 WPF UI。其他第三方许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
