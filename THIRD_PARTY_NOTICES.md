# Third-party notices

## liquid-glass-webgl

The upstream renderer is included as the Git submodule `wallpaper/vendor/liquid-glass-webgl` and is used directly by `wallpaper/src/liquid/reference-wallpaper.ts`. Its WebGL 1 renderer provides the continuous-curvature mask, offscreen framebuffer composition, refraction, separable blur, highlight, shadow, foreground texture rasterization, and scroll culling used by the lyric glass panel.

The local integration adds the lyric data adapter, panel configuration, static wallpaper asset, and the separate parameter control panel.

The full Apache License 2.0 text is available at <https://www.apache.org/licenses/LICENSE-2.0>.
