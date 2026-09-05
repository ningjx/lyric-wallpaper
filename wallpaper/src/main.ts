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

  // 输入微调「当前歌」歌词对齐（滚轮为主、拖拽兜底），显示累积偏移并淡出
  setupOffsetAdjust(renderer);
}

/** 排查用：左下角实时标注 WE 到底投递了哪些事件（默认开，确认后改成 false） */
const DEBUG_INPUT = true;

/**
 * 滚轮 / 拖拽 = 歌词偏移 ±N ms（仅当前歌，下一首自动清零）。
 * 方向：上滚/上拖 → 偏移减小；下滚/下拖 → 偏移增大。
 *
 * Wallpaper Engine 桌面态对 Web 壁纸的输入投递不可靠：
 * 滚轮常被桌面外壳/图标层吃掉，而 mousemove/mousedown 通常能到。故：
 * - wheel：能收到就用（按“格”累积，规避高分辨率滚轮一顿吐几十个微小 deltaY 乱跳）
 * - 拖拽：mousedown 起拖 → mousemove 按纵向位移换算偏移 → mouseup 结束，作可靠兜底
 */
function setupOffsetAdjust(renderer: LyricsRenderer): void {
  const STEP_MS = 200;       // 滚轮每格步进
  const DRAG_MS_PER_PX = 10; // 拖拽：纵向 1px ≈ 10ms

  const hintEl = document.getElementById("offset-hint");
  let hintTimer: ReturnType<typeof setTimeout> | null = null;
  let hintShown = false;

  const showHint = (totalMs: number): void => {
    if (!hintEl) return;
    const sign = totalMs > 0 ? "+" : totalMs < 0 ? "-" : "";
    const absSec = (Math.abs(totalMs) / 1000).toFixed(1);
    hintEl.textContent = `偏移 ${sign}${absSec}s`;

    if (!hintShown) {
      hintEl.hidden = false;
      hintShown = true;
      void hintEl.offsetWidth; // 首次显示强制 reflow，保证重触发也能重新淡入
      hintEl.classList.add("show");
    }

    if (hintTimer !== null) clearTimeout(hintTimer);
    hintTimer = setTimeout(() => {
      hintEl.classList.remove("show");
      hintTimer = setTimeout(() => {
        hintEl.hidden = true;
        hintShown = false;
      }, 300);
    }, 1500);
  };

  // —— 滚轮（能收到就用）——
  let wheelAcc = 0; // 不满一格的余量，跨事件累积
  window.addEventListener("wheel", (e: WheelEvent) => {
    if (e.deltaY === 0) return; // 触控板横向滚 / 瞬时 0，略过
    wheelAcc += e.deltaY / 100; // deltaMode 0/1 统一按 100px（≈1 行）算一格
    const steps = Math.round(wheelAcc);
    wheelAcc -= steps;
    if (steps === 0) return;
    renderer.nudgeTempOffset(steps * STEP_MS);
    showHint(renderer.tempOffsetMs);
  });

  // —— 拖拽（可靠兜底）——
  let dragging = false;
  let startY = 0;
  let startOffset = 0;
  let lastDownTime = 0;
  let lastDownY = 0;
  window.addEventListener("mousedown", (e: MouseEvent) => {
    if (e.button !== 0) return;
    // 双击（两次按下 <250ms 且几乎没纵向移动）→ 清零临时偏移，重新校准
    const now = Date.now();
    if (now - lastDownTime < 250 && Math.abs(e.clientY - lastDownY) < 8) {
      lastDownTime = 0;
      dragging = false;
      renderer.clearTempOffset();
      showHint(0);
      return;
    }
    lastDownTime = now;
    lastDownY = e.clientY;
    dragging = true;
    startY = e.clientY;
    startOffset = renderer.tempOffsetMs;
  });
  window.addEventListener("mousemove", (e: MouseEvent) => {
    if (!dragging) return;
    const ms = startOffset + (e.clientY - startY) * DRAG_MS_PER_PX;
    renderer.setTempOffset(ms);
    showHint(renderer.tempOffsetMs);
  });
  const endDrag = (): void => {
    dragging = false;
  };
  window.addEventListener("mouseup", endDrag);
  window.addEventListener("blur", endDrag);

  setupInputDebug();
}

/** 左下角输入自检：实时标注每种事件是否收到 + 最后一条详情 */
function setupInputDebug(): void {
  if (!DEBUG_INPUT) return;
  const el = document.createElement("div");
  el.id = "input-debug";
  el.style.cssText =
    "position:fixed;left:12px;bottom:12px;z-index:99;" +
    "font:12px/1.6 ui-monospace,Consolas,monospace;color:#8cf;" +
    "background:rgba(0,0,0,0.6);padding:6px 10px;border-radius:6px;" +
    "white-space:pre;pointer-events:none;";
  document.body.appendChild(el);

  const types = ["wheel", "mousemove", "mousedown", "mouseup", "keydown"];
  const fired = new Set<string>();

  const render = (last?: string): void => {
    const rows = types.map((t) => `${fired.has(t) ? "✓" : "—"} ${t}`);
    el.textContent = ["[input]", ...rows, last ? `→ ${last}` : ""].join("\n");
  };

  const detail: Record<string, (e: Event) => string> = {
    wheel: (e) => {
      const w = e as WheelEvent;
      return `wheel deltaY=${w.deltaY} mode=${w.deltaMode}`;
    },
    mousemove: (e) => {
      const m = e as MouseEvent;
      return `move x=${m.clientX} y=${m.clientY}`;
    },
    mousedown: () => "down",
    mouseup: () => "up",
    keydown: (e) => `key ${(e as KeyboardEvent).key}`,
  };

  for (const t of types) {
    window.addEventListener(t, (e: Event) => {
      fired.add(t);
      render(detail[t](e));
    });
  }
  render();
}

main();
