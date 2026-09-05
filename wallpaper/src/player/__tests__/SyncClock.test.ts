import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SyncClock } from "../SyncClock";

/**
 * 用可控的 performance.now 驱动 SyncClock，避免测试依赖真实时钟。
 */
function installFakeClock() {
  let now = 0;
  const spy = vi.spyOn(performance, "now").mockImplementation(() => now);
  return {
    advance(ms: number) {
      now += ms;
    },
    restore() {
      spy.mockRestore();
    },
  };
}

describe("SyncClock", () => {
  let clock: ReturnType<typeof installFakeClock>;

  beforeEach(() => {
    clock = installFakeClock();
  });

  afterEach(() => {
    clock.restore();
  });

  it("播放中：now() 随本地时钟平滑推进", () => {
    const c = new SyncClock();
    c.calibrate(10, true);
    clock.advance(2000);
    expect(c.now()).toBeCloseTo(12);
  });

  it("暂停：now() 冻结在暂停位置", () => {
    const c = new SyncClock();
    c.calibrate(10, true);
    clock.advance(1000);
    c.calibrate(11, false); // 暂停在 11s
    clock.advance(5000);
    expect(c.now()).toBeCloseTo(11);
  });

  it("小偏差用平滑衔接、不回跳", () => {
    const c = new SyncClock();
    c.calibrate(10, true);
    clock.advance(2000); // 本地 clock 已到 12
    // 服务端报 12.2（偏差 0.2 < 阈值 1.5），应平滑对齐而非跳回
    c.calibrate(12.2, true);
    const after = c.now();
    expect(after).toBeGreaterThanOrEqual(12);
  });

  it("大跳变（切歌/拖进度）硬校准", () => {
    const c = new SyncClock();
    c.calibrate(10, true);
    clock.advance(1000);
    c.calibrate(50, true); // 突然跳到 50
    expect(c.now()).toBeCloseTo(50);
  });

  it("reset 回到未播放状态", () => {
    const c = new SyncClock();
    c.calibrate(10, true);
    c.reset();
    expect(c.now()).toBe(0);
  });
});