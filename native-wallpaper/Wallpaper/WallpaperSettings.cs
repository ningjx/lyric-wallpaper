using LyricWallpaper.Scene;

namespace LyricWallpaper.Wallpaper;

internal enum LyricAlignment { Center, Left, Right }

internal sealed record WallpaperSettings
{
    public string Background { get; init; } = "";
    public string BackgroundDirectory { get; init; } = "";
    public int RotationSeconds { get; init; } = 300;
    public BackgroundLayout Layout { get; init; }
    public int BackgroundZoom { get; init; } = 100;
    public int BackgroundX { get; init; }
    public int BackgroundY { get; init; }
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public int FontScale { get; init; } = 100;
    public int TextColor { get; init; } = unchecked((int)0xFFF0FBFF);
    public int Opacity { get; init; } = 100;
    public int Spacing { get; init; } = 100;
    public int Vertical { get; init; } = -11;
    public int Horizontal { get; init; }
    public int TextY { get; init; }
    public int Padding { get; init; } = 17;
    public LyricAlignment Alignment { get; init; }
    public double ScrollSpeed { get; init; } = 5.5;
    public int RenderDistance { get; init; } = 4;
    public double FarScale { get; init; } = .61;
    public double ScaleFalloff { get; init; } = .55;
    public double ScaleCurve { get; init; } = 1.63;
    public double FadeFalloff { get; init; } = .65;
    public double FadeCurve { get; init; } = 1.12;
    public double FarGlass { get; init; } = .15;
    public int Glass { get; init; } = 100;
    public int RefractionHeight { get; init; } = 11;
    public int RefractionAmount { get; init; } = -44;
    public int Blur { get; init; }
    public bool FullSceneBlur { get; init; }
    public bool BehindGlass { get; init; }
    public double Saturation { get; init; } = 1;
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1;
    public int CornerRadius { get; init; } = 35;
    public bool DepthEffect { get; init; } = true;
    public bool Chromatic { get; init; }
    public bool Highlight { get; init; } = true;
    public HighlightStyle HighlightMode { get; init; }
    public int HighlightColor { get; init; } = unchecked((int)0xFFB8EBFF);
    public double HighlightAlpha { get; init; } = .34;
    public double HighlightAngle { get; init; } = -1.98;
    public double HighlightFalloff { get; init; } = 2.1;
    public double HighlightWidth { get; init; } = 1.5;
    public bool Shadow { get; init; }
    public int ShadowColor { get; init; } = unchecked((int)0xFF030F1F);
    public double ShadowAlpha { get; init; } = .18;
    public int ShadowRadius { get; init; } = 28;
    public int ShadowX { get; init; }
    public int ShadowY { get; init; } = 16;
    public int TintColor { get; init; } = unchecked((int)0xFF2E85B8);
    public double TintAlpha { get; init; }
    public int SurfaceColor { get; init; } = unchecked((int)0xFFCCF0FF);
    public double SurfaceAlpha { get; init; }
    public bool Translation { get; init; } = true;
    public int OffsetMs { get; init; }
    public int IdleSeconds { get; init; } = 30;
    public int Fps { get; init; } = 60;
    public double RenderScale { get; init; } = 1;

    public SceneStyle ToSceneStyle() => new(
        new(Layout, BackgroundZoom, BackgroundX, BackgroundY),
        new(Glass / 100f, RefractionHeight, RefractionAmount, Blur, CornerRadius, FullSceneBlur,
            new((float)Saturation, (float)Brightness, (float)Contrast, Chromatic),
            new(new(HighlightColor, Highlight ? (float)HighlightAlpha : 0),
                (float)HighlightAngle, (float)HighlightFalloff, (float)HighlightWidth, HighlightMode),
            new(TintColor, (float)TintAlpha), new(SurfaceColor, (float)SurfaceAlpha),
            new(new(ShadowColor, Shadow ? (float)ShadowAlpha : 0), ShadowRadius, ShadowX, ShadowY)),
        new(TextColor, (float)FarGlass, DepthEffect, BehindGlass));

    public WallpaperSettings Normalize() => this with
    {
        Background = Background ?? "",
        BackgroundDirectory = BackgroundDirectory ?? "",
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Microsoft YaHei UI" : FontFamily,
        Layout = Enum.IsDefined(Layout) ? Layout : BackgroundLayout.Cover,
        Alignment = Enum.IsDefined(Alignment) ? Alignment : LyricAlignment.Center,
        HighlightMode = Enum.IsDefined(HighlightMode) ? HighlightMode : HighlightStyle.Directional,
        RotationSeconds = Math.Clamp(RotationSeconds, 10, 3600),
        BackgroundZoom = Math.Clamp(BackgroundZoom, 10, 400),
        BackgroundX = Math.Clamp(BackgroundX, -100, 100),
        BackgroundY = Math.Clamp(BackgroundY, -100, 100),
        FontScale = Math.Clamp(FontScale, 50, 160),
        Opacity = Math.Clamp(Opacity, 20, 100),
        Spacing = Math.Clamp(Spacing, 35, 175),
        Vertical = Math.Clamp(Vertical, -56, 56),
        Horizontal = Math.Clamp(Horizontal, -52, 52),
        TextY = Math.Clamp(TextY, -40, 40),
        Padding = Math.Clamp(Padding, 0, 160),
        ScrollSpeed = Math.Clamp(ScrollSpeed, 2, 20),
        RenderDistance = Math.Clamp(RenderDistance, 1, 6),
        FarScale = Math.Clamp(FarScale, .35, .95),
        ScaleFalloff = Math.Clamp(ScaleFalloff, .05, 2),
        ScaleCurve = Math.Clamp(ScaleCurve, .5, 4),
        FadeFalloff = Math.Clamp(FadeFalloff, .05, 2),
        FadeCurve = Math.Clamp(FadeCurve, .5, 4),
        FarGlass = Math.Clamp(FarGlass, 0, 1),
        Glass = Math.Clamp(Glass, 0, 125),
        RefractionHeight = Math.Clamp(RefractionHeight, 0, 56),
        RefractionAmount = Math.Clamp(RefractionAmount, -80, 20),
        Blur = Math.Clamp(Blur, 0, 36),
        Saturation = Math.Clamp(Saturation, 0, 2.5),
        Brightness = Math.Clamp(Brightness, -.5, .5),
        Contrast = Math.Clamp(Contrast, .3, 2),
        CornerRadius = Math.Clamp(CornerRadius, 0, 180),
        HighlightAlpha = Math.Clamp(HighlightAlpha, 0, 1),
        HighlightAngle = Math.Clamp(HighlightAngle, -3.14, 3.14),
        HighlightFalloff = Math.Clamp(HighlightFalloff, .2, 5),
        HighlightWidth = Math.Clamp(HighlightWidth, .2, 6),
        ShadowAlpha = Math.Clamp(ShadowAlpha, 0, 1),
        ShadowRadius = Math.Clamp(ShadowRadius, 0, 80),
        ShadowX = Math.Clamp(ShadowX, -50, 50),
        ShadowY = Math.Clamp(ShadowY, -50, 80),
        TintAlpha = Math.Clamp(TintAlpha, 0, .6),
        SurfaceAlpha = Math.Clamp(SurfaceAlpha, 0, .6),
        OffsetMs = Math.Clamp(OffsetMs, -5000, 5000),
        IdleSeconds = Math.Clamp(IdleSeconds, 0, 3600),
        Fps = Math.Clamp(Fps, 15, 144),
        RenderScale = Math.Clamp(RenderScale, .5, 2)
    };

}
