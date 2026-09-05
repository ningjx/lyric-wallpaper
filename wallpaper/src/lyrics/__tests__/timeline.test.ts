import { describe, expect, it } from "vitest";
import { findCurrentLine } from "../timeline";
import { parseLrc } from "../parser";

describe("findCurrentLine", () => {
  it("空数组返回 -1", () => {
    expect(findCurrentLine([], 5)).toBe(-1);
  });

  it("时间早于第一行返回 -1", () => {
    const lines = parseLrc("[00:10.00] a");
    expect(findCurrentLine(lines, 9)).toBe(-1);
  });

  it("精确落在某行返回该行", () => {
    const lines = parseLrc("[00:10.00] a\n[00:20.00] b\n[00:30.00] c");
    expect(findCurrentLine(lines, 20)).toBe(1);
  });

  it("落在两行之间返回更早的一行", () => {
    const lines = parseLrc("[00:10.00] a\n[00:20.00] b");
    expect(findCurrentLine(lines, 15)).toBe(0);
  });

  it("超过最后一行返回最后一行", () => {
    const lines = parseLrc("[00:10.00] a\n[00:20.00] b");
    expect(findCurrentLine(lines, 999)).toBe(1);
  });
});