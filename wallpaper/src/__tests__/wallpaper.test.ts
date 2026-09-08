import { afterEach, describe, expect, it } from "vitest";
import { setupWallpaperEnvironment, type WallpaperSettings } from "../wallpaper";

describe("Wallpaper Engine property bridge", () => {
  afterEach(() => {
    delete (globalThis as { window?: unknown }).window;
  });

  it("enabling high-quality blur preserves the configured blur radius", () => {
    const fakeWindow: Record<string, unknown> = {};
    (globalThis as { window: Record<string, unknown> }).window = fakeWindow;
    let received: WallpaperSettings | undefined;
    setupWallpaperEnvironment((settings) => {
      received = { ...settings, liquid: { ...settings.liquid } };
    });

    const listener = fakeWindow.wallpaperPropertyListener as {
      applyUserProperties(properties: Record<string, unknown>): void;
    };
    listener.applyUserProperties({ separableblur: { value: true } });
    expect(received?.liquid.separableBlur).toBe(true);
    expect(received?.liquid.blurRadius).toBe(0);

    listener.applyUserProperties({ blurradius: { value: 0 } });
    expect(received?.liquid.blurRadius).toBe(0);
  });
});
