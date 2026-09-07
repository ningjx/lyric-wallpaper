import type { PerfSnapshot } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer/perf-monitor";

export interface WallpaperPerformanceSnapshot {
  pipeline: "layered" | "scene-fbo";
  rafPerSecond: number;
  renderedPerSecond: number;
  renderer: PerfSnapshot;
}

/** 只读的页面级统计：把持续运行的 rAF 与实际 GPU render 分开计数。 */
export class WallpaperPerformanceProbe {
  private rafFrames = 0;
  private baselineRafFrames = 0;
  private baselineRenderedFrames = 0;
  private baselineTime = performance.now();

  onAnimationFrame(): void {
    this.rafFrames++;
  }

  reset(renderer: PerfSnapshot): void {
    this.baselineRafFrames = this.rafFrames;
    this.baselineRenderedFrames = renderer.totalFrames;
    this.baselineTime = performance.now();
  }

  snapshot(pipeline: "layered" | "scene-fbo", renderer: PerfSnapshot): WallpaperPerformanceSnapshot {
    const elapsedSeconds = Math.max(.001, (performance.now() - this.baselineTime) / 1000);
    return {
      pipeline,
      rafPerSecond: (this.rafFrames - this.baselineRafFrames) / elapsedSeconds,
      renderedPerSecond: (renderer.totalFrames - this.baselineRenderedFrames) / elapsedSeconds,
      renderer,
    };
  }
}
