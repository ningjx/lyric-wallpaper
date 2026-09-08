import { describe, expect, it } from "vitest";
import { ScrollRenderGate } from "../render-scheduler";

describe("ScrollRenderGate", () => {
  it("coalesces sub-pixel scroll updates after it settles", () => {
    const gate = new ScrollRenderGate();
    expect(gate.shouldCommit(120)).toBe(true);
    expect(gate.shouldCommit(120)).toBe(false);
    expect(gate.shouldCommit(120.05)).toBe(false);
    expect(gate.shouldCommit(120.051)).toBe(true);
    expect(gate.shouldCommit(120.101)).toBe(false);
  });

  it("commits the current position again after a scene reset", () => {
    const gate = new ScrollRenderGate();
    gate.shouldCommit(0);
    gate.reset();
    expect(gate.shouldCommit(0)).toBe(true);
  });
});
