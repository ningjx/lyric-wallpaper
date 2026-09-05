import { describe, expect, it } from "vitest";
import { parseLrc, mergeTranslation } from "../parser";

describe("parseLrc", () => {
  it("解析基础单时间标签行", () => {
    const lines = parseLrc("[00:12.34] 第一句");
    expect(lines).toHaveLength(1);
    expect(lines[0].time).toBeCloseTo(12.34);
    expect(lines[0].text).toBe("第一句");
  });

  it("支持一行多个时间标签（分别展开为多行）", () => {
    const lines = parseLrc("[00:01.00][00:02.00] 重复句");
    expect(lines).toHaveLength(2);
    expect(lines[0].time).toBeCloseTo(1);
    expect(lines[1].time).toBeCloseTo(2);
    expect(lines[0].text).toBe("重复句");
    expect(lines[1].text).toBe("重复句");
  });

  it("跳过元信息行与 JSON 元数据行", () => {
    const lrc = [
      "[ti:歌名]",
      "[ar:歌手]",
      '{"t":0,"c":[{"tx":"作词"}]}',
      "[00:10.00] 正文",
    ].join("\n");
    const lines = parseLrc(lrc);
    expect(lines).toHaveLength(1);
    expect(lines[0].text).toBe("正文");
  });

  it("去掉行尾标点", () => {
    const lines = parseLrc("[00:05.00] 你好，世界。");
    expect(lines[0].text).toBe("你好，世界");
  });

  it("按时间升序排序", () => {
    const lines = parseLrc("[00:30.00] b\n[00:10.00] a");
    expect(lines.map((l) => l.time)).toEqual([10, 30]);
  });

  it("过滤空文本行", () => {
    const lines = parseLrc("[00:01.00]\n[00:02.00] 有字");
    expect(lines).toHaveLength(1);
  });

  it("空输入返回空数组", () => {
    expect(parseLrc("")).toEqual([]);
  });
});

describe("mergeTranslation", () => {
  it("将翻译按时间就近合并进原词", () => {
    const original = parseLrc("[00:10.00] 你好\n[00:20.00] 世界");
    // 双语 LRC 的翻译行时间与原词行一致（或略早）
    const trans = parseLrc("[00:10.00] hello\n[00:20.00] world");
    const merged = mergeTranslation(original, trans);
    expect(merged[0].translated).toBe("hello");
    expect(merged[1].translated).toBe("world");
  });

  it("翻译早于原词超过 8 秒则忽略", () => {
    // 原词在 20s，翻译只有 10s（相差 10s > 8s），不合并
    const original = parseLrc("[00:20.00] 世界");
    const trans = parseLrc("[00:10.00] world");
    const merged = mergeTranslation(original, trans);
    expect(merged[0].translated).toBeUndefined();
  });

  it("无翻译时原样返回", () => {
    const original = parseLrc("[00:10.00] 你好");
    const merged = mergeTranslation(original, []);
    expect(merged).toHaveLength(1);
    expect(merged[0].translated).toBeUndefined();
  });
});