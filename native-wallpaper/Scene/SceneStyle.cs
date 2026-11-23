namespace LyricWallpaper.Scene;

internal enum BackgroundLayout { Cover, Contain, Stretch, Tile }
internal enum HighlightStyle { Directional, Ambient, Even }

internal readonly record struct BackgroundView(BackgroundLayout Layout, int Zoom, int X, int Y);

internal readonly record struct GlassTone(float Saturation, float Brightness, float Contrast, bool Chromatic);
internal readonly record struct MaterialColor(int Argb, float Alpha);
internal readonly record struct GlassHighlight(MaterialColor Color, float Angle, float Falloff, float Width, HighlightStyle Style);
internal readonly record struct GlassShadow(MaterialColor Color, int Radius, int X, int Y);
internal readonly record struct GlassMaterial(float Strength, int RefractionHeight, int RefractionAmount, int Blur, int Radius,
    bool FullSceneBlur, GlassTone Tone, GlassHighlight Highlight, MaterialColor Tint, MaterialColor Surface, GlassShadow Shadow);

internal readonly record struct LyricGlassStyle(int TextColor, float FarGlass, bool Depth, bool BehindGlass);

internal readonly record struct SceneStyle(BackgroundView Background, GlassMaterial Glass, LyricGlassStyle Lyrics);

internal readonly record struct SceneFrame(SceneStyle Style, IReadOnlyList<GlassCard> Lyrics, IReadOnlyList<GlassCard> Widgets);
