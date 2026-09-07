import { describe, expect, it, vi } from "vitest";
import { WallpaperPerformanceProbe } from "../performance-probe";
import type { PerfSnapshot } from "../../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer/perf-monitor";

const renderer = (totalFrames: number) => ({ totalFrames } as PerfSnapshot);

describe("WallpaperPerformanceProbe", () => {
  it("reports rAF and rendered rates independently", () => {
    let now = 0;
    const clock = vi.spyOn(performance, "now").mockImplementation(() => now);
    const probe = new WallpaperPerformanceProbe();
    probe.reset(renderer(10));
    probe.onAnimationFrame();
    probe.onAnimationFrame();
    now = 1000;
    const snapshot = probe.snapshot("layered", renderer(11));
    expect(snapshot.rafPerSecond).toBe(2);
    expect(snapshot.renderedPerSecond).toBe(1);
    expect(snapshot.pipeline).toBe("layered");
    clock.mockRestore();
  });
});
