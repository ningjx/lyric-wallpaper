import { describe, expect, it } from "vitest";
import { FramePacer } from "../frame-pacer";

/** 在 60Hz rAF 上跑 seconds 秒，返回放行的帧序号。 */
function run(pacer: FramePacer, hz: number, seconds: number): number[] {
  const stepMs = 1000 / hz;
  const frames = Math.round(hz * seconds);
  const allowed: number[] = [];
  for (let i = 1; i <= frames; i++) {
    if (pacer.shouldUpdate(i * stepMs)) allowed.push(i);
  }
  return allowed;
}

describe("FramePacer", () => {
  it("passes every frame when no limit is set", () => {
    const pacer = new FramePacer();
    expect(pacer.getFps()).toBe(0);
    expect(run(pacer, 60, .1)).toHaveLength(6);
  });

  it("halves a 60Hz rAF down to 30fps", () => {
    const pacer = new FramePacer();
    pacer.setFps(30);
    expect(run(pacer, 60, 1)).toHaveLength(30);
  });

  it("thirds a 144Hz rAF down to 48fps", () => {
    const pacer = new FramePacer();
    pacer.setFps(48);
    // 144Hz 下每 3 帧放行 1 帧 → 48fps
    expect(run(pacer, 144, 1)).toHaveLength(48);
  });

  it("cannot exceed the host rAF cadence", () => {
    const pacer = new FramePacer();
    pacer.setFps(240);
    // 上限高于宿主节拍时退化为不限制，而不是凭空造帧
    expect(run(pacer, 60, 1)).toHaveLength(60);
  });

  it("treats invalid or non-positive limits as unrestricted", () => {
    const pacer = new FramePacer();
    pacer.setFps(Number.NaN);
    expect(pacer.getFps()).toBe(0);
    pacer.setFps(-1);
    expect(pacer.getFps()).toBe(0);
    pacer.setFps(30);
    pacer.setFps(0);
    expect(run(pacer, 60, .1)).toHaveLength(6);
  });

  it("applies the new limit from the next frame after a change", () => {
    const pacer = new FramePacer();
    expect(pacer.shouldUpdate(0)).toBe(true);
    pacer.setFps(30);
    expect(pacer.shouldUpdate(16)).toBe(true);
  });

  it("releases only one frame after a long suspend", () => {
    const pacer = new FramePacer();
    pacer.setFps(30);
    expect(pacer.shouldUpdate(0)).toBe(true);
    // 休眠 60 秒后恢复：不应把积攒的 1800 帧一次补齐
    expect(pacer.shouldUpdate(60_000)).toBe(true);
    expect(pacer.shouldUpdate(60_016)).toBe(false);
    expect(pacer.shouldUpdate(60_032)).toBe(false);
    expect(pacer.shouldUpdate(60_049)).toBe(true);
  });
});
