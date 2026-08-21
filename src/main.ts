import "./styles/main.css";
import { NowPlayingApi } from "./api/nowPlaying";
import { SyncClock } from "./player/SyncClock";
import { MusicState } from "./player/MusicState";
import { LyricsRenderer } from "./lyrics/renderer";
import { SceneController } from "./scene";
import { setupWallpaperEnvironment } from "./wallpaper";

function main(): void {
  setupWallpaperEnvironment();

  const sceneEl = document.getElementById("scene");
  const lyricsEl = document.getElementById("lyrics");
  if (!sceneEl || !lyricsEl) return;

  const api = new NowPlayingApi();
  const clock = new SyncClock();
  const renderer = new LyricsRenderer(lyricsEl);

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
