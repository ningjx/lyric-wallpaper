import { describe, expect, it } from "vitest";
import { toSnapshot } from "../nowPlaying";
import type { NowPlayingState } from "../types";

function state(overrides: Partial<NowPlayingState["player"] & NowPlayingState["track"]> = {}): NowPlayingState {
  return {
    player: {
      hasSong: true,
      isPaused: false,
      volumePercent: 100,
      seekbarCurrentPosition: 42.5,
      seekbarCurrentPositionHuman: "00:42",
      statePercent: 50,
      likeStatus: "false",
      repeatType: "list",
      ...overrides,
    },
    track: {
      id: "123",
      title: "歌名",
      author: "歌手",
      album: "专辑",
      cover: "cover-url",
      duration: 200,
      durationHuman: "03:20",
      url: "",
      isVideo: false,
      isAdvertisement: false,
      inLibrary: false,
      ...overrides,
    },
  };
}

describe("toSnapshot", () => {
  it("归一化 /query 状态为 PlayerSnapshot", () => {
    const s = toSnapshot(state());
    expect(s.hasSong).toBe(true);
    expect(s.song).toBe("歌名");
    expect(s.author).toBe("歌手");
    expect(s.playing).toBe(true);
    expect(s.progress).toBe(42.5);
    expect(s.duration).toBe(200);
    expect(s.trackId).toBe("123");
    expect(s.resolveState).toBe("idle"); // /query 无此字段，置默认
    expect(s.hasLyric).toBe(false);
  });

  it("暂停时 playing=false", () => {
    const s = toSnapshot(state({ isPaused: true }));
    expect(s.playing).toBe(false);
  });

  it("无歌曲时 hasSong=false 且 song/author 为空", () => {
    const s = toSnapshot(state({ hasSong: false, title: "", id: "" }));
    expect(s.hasSong).toBe(false);
    expect(s.song).toBe("");
  });
});