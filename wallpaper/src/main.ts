import "./styles/main.css";
import { NowPlayingApi } from "./api/nowPlaying";
import { SyncClock } from "./player/SyncClock";
import { MusicState } from "./player/MusicState";
import { LyricsRenderer, transExtraFor } from "./lyrics/renderer";
import { SceneController } from "./scene";
import { setupWallpaperEnvironment, DEFAULT_SETTINGS } from "./wallpaper";
import type { WallpaperSettings } from "./wallpaper";

function main(): void {
  const sceneEl = document.getElementById("scene");
  const lyricsEl = document.getElementById("lyrics");
  if (!sceneEl || !lyricsEl) return;

  const api = new NowPlayingApi();
  const clock = new SyncClock();
  const renderer = new LyricsRenderer(lyricsEl);

  // 属性面板调节：同步渲染器字号/行距/同步偏移 + CSS 变量（位置/字体/亮度）
  const applySettings = (s: WallpaperSettings): void => {
    const root = document.documentElement;
    root.style.setProperty("--gap", `${s.lineGap}px`);
    root.style.setProperty("--trans-extra", `${transExtraFor(s.fontSize)}px`);
    root.style.setProperty("--offset-x", `${s.offsetX}px`);
    root.style.setProperty("--offset-y", `${s.offsetY}px`);
    root.style.setProperty("--font-family", s.fontFamily);
    lyricsEl.style.filter = s.brightness === 100 ? "" : `brightness(${s.brightness / 100})`;
    renderer.setLayout(s.fontSize, s.lineGap);
    renderer.setSyncOffset(s.syncOffset);
  };

  setupWallpaperEnvironment(applySettings);
  // 初始以默认值刷一次，保证 CSS 变量就位（Wallpaper Engine 首次未必回传属性）
  applySettings(DEFAULT_SETTINGS);

  // 数据流：低频轮询 + 校准本地时钟；有歌淡入、无歌淡出场景层
  new MusicState(api, clock, renderer, new SceneController(sceneEl)).start();

  // 渲染流：高频 rAF，本地时钟驱动，动画不依赖 API 轮询
  const loop = (): void => {
    requestAnimationFrame(loop);
    renderer.update(clock.now());
  };
  requestAnimationFrame(loop);
}

main();
