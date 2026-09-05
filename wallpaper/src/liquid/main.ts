import "./style.css";
import { parseLrc } from "../lyrics/parser";
import { findCurrentLine } from "../lyrics/timeline";
import { NowPlayingApi } from "../api/nowPlaying";
import { SyncClock } from "../player/SyncClock";
import { MusicState } from "../player/MusicState";
import { SceneController } from "../scene";
import { setupWallpaperEnvironment } from "../wallpaper";
import { ReferenceLyricsWallpaper } from "./reference-wallpaper";
import { mountLiquidControls } from "./controls";

const lines = parseLrc(`[00:00.00]把城市的声音调低
[00:05.50]听见晚风穿过缝隙
[00:11.00]有一颗星落在眼底
[00:16.50]让时间慢慢流成光
[00:22.50]我们漂浮在夜色里
[00:28.00]等一场温柔的潮汐
[00:33.50]把没说完的话藏起
[00:39.00]交给遥远的天际
[00:44.50]当世界安静了一秒
[00:50.00]月光轻轻落在肩上
[00:55.50]沿着你的目光远行
[01:01.00]直到天色渐渐明亮`);

const canvas = document.querySelector<HTMLCanvasElement>("#liquid-canvas")!;
const scene = document.querySelector<HTMLElement>("#scene")!;
const fallback = document.querySelector<HTMLElement>("#fallback-lyrics")!;
const controls = document.querySelector<HTMLElement>("#liquid-controls")!;
const params = new URLSearchParams(location.search);
const still = params.get("still") === "1";
const previewMode = still || params.get("demo") === "1";
const requestedTime = Number(params.get("time") ?? 16.5);
let elapsed = Number.isFinite(requestedTime) ? Math.max(0, requestedTime) : 16.5;
let last = performance.now();
let frame: number | null = null;
let wallpaper: ReferenceLyricsWallpaper | null = null;
let musicState: MusicState | null = null;
let clock: SyncClock | null = null;

function showFallback(): void {
  document.documentElement.classList.add("fallback");
  fallback.replaceChildren(...lines.slice(0, 5).map((line, index) => {
    const el = document.createElement("p");
    el.className = `fallback-line ${index === 2 ? "current" : ""}`;
    el.textContent = line.text;
    el.style.setProperty("--y", `${(index - 2) * 112}px`);
    el.style.setProperty("--scale", String(index === 2 ? 1 : .72));
    el.style.setProperty("--opacity", String(index === 2 ? 1 : .35));
    return el;
  }));
}

async function boot(): Promise<void> {
  try {
    await document.fonts.ready;
    wallpaper = new ReferenceLyricsWallpaper(canvas, previewMode ? lines : []);
    await wallpaper.start();
    mountLiquidControls(controls, wallpaper);
    const setControlsVisible = (visible: boolean): void => { controls.hidden = !visible; };
    setControlsVisible(params.get("controls") === "1");
    setupWallpaperEnvironment((settings) => setControlsVisible(settings.showControls));
    if (previewMode) {
      new SceneController(scene).show();
    } else {
      clock = new SyncClock();
      musicState = new MusicState(new NowPlayingApi(), clock, wallpaper, new SceneController(scene));
      musicState.start();
    }
    const tick = (now: number): void => {
      if (previewMode && !still) elapsed += Math.min(.1, (now - last) / 1000);
      last = now;
      const songTime = previewMode ? elapsed % 67 : clock?.now() ?? 0;
      wallpaper?.draw(now / 1000, previewMode ? findCurrentLine(lines, songTime) : findCurrentLineForWallpaper(songTime));
      frame = requestAnimationFrame(tick);
    };
    tick(last);
  } catch {
    showFallback();
  }
}

function findCurrentLineForWallpaper(time: number): number {
  return wallpaper ? findCurrentLine(wallpaper.getLines(), time) : 0;
}

void boot();
window.addEventListener("resize", () => wallpaper?.resize());
window.addEventListener("pagehide", () => { if (frame !== null) cancelAnimationFrame(frame); musicState?.stop(); wallpaper?.dispose(); }, { once: true });
