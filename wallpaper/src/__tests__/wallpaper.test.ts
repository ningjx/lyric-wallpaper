import { afterEach, describe, expect, it } from "vitest";
import { DEFAULT_SEPARABLE_BLUR_RADIUS } from "../liquid/reference-wallpaper";
import { setupWallpaperEnvironment, type WallpaperSettings } from "../wallpaper";

describe("Wallpaper Engine property bridge", () => {
  afterEach(() => {
    delete (globalThis as { window?: unknown }).window;
  });

  it("enabling high-quality blur supplies a visible radius unless the user also set one", () => {
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
    expect(received?.liquid.blurRadius).toBe(DEFAULT_SEPARABLE_BLUR_RADIUS);

    listener.applyUserProperties({ blurradius: { value: 0 } });
    expect(received?.liquid.blurRadius).toBe(0);
  });
});
