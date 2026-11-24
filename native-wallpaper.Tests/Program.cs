using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using LyricWallpaper.Playback;
using LyricWallpaper.Rendering;
using LyricWallpaper.Scene;
using LyricWallpaper.Storage;
using LyricWallpaper.Wallpaper;
using LyricWallpaper.Widgets;
using LyricWallpaper.Widgets.Clock;

var folder = Directory.CreateTempSubdirectory("LyricWallpaper.Tests.");
try
{
    var catalog = new WidgetCatalog();
    var paths = AppPaths.Resolve(Path.Combine(folder.FullName, "config.json"), Path.Combine(folder.FullName, "wallpaper.json"));
    var store = new WidgetStore(paths, catalog);
    var clock = catalog.Definitions.Single();

    Require(store.Load().Items.Single().Visibility == WidgetVisibility.Idle, "default clock remains idle-only");
    File.WriteAllText(paths.LegacyClock, """{"X":0.26,"Y":0.68}""");
    Require(store.Load().Items.Single().Placement.Position == new WidgetPosition(.26f, .68f), "legacy clock position");
    File.WriteAllText(paths.Widgets, """{"Version":1,"Clock":{"Position":{"X":0.65,"Y":0.42},"Padding":34,"ShowDate":false}}""");
    var single = store.Load().Items.Single();
    Require(single.Placement.Position == new WidgetPosition(.65f, .42f) &&
        single.Options is ClockOptions { Padding: 34, ShowDate: false }, "v1 clock settings");

    Guid id = Guid.Parse("486fb9a0-0c64-46f2-998a-a715241deb89");
    string legacy = $$$"""{"Version":2,"Clocks":[{"Id":"{{{id}}}","Settings":{"Position":{"X":0.21,"Y":0.71},"Scale":1.4,"Use24Hour":false}}]}""";
    File.WriteAllText(paths.Widgets, legacy);
    var migrated = store.Load();
    Require(migrated.Items.Single() is { Visibility: WidgetVisibility.Idle, Options: ClockOptions { Use24Hour: false } } original &&
        original.Id == id && original.Placement == new WidgetPlacement(new(.21f, .71f), 1.4) &&
        File.ReadAllText(paths.Widgets) == legacy, "v2 migration preserves identity and waits for first save");
    const int manualClockColor = unchecked((int)0xFFFFD797);
    var added = catalog.Create(clock, new WidgetPosition(.68f, .23f)) with
    {
        Options = new ClockOptions { AutoInvertText = true, TextColor = manualClockColor }
    };
    var saved = migrated with { Items = migrated.Items.Add(added) };
    store.Save(saved);
    Require(store.Load().Items.SequenceEqual(saved.Items) && File.ReadAllText(paths.Widgets + ".v2.bak") == legacy,
        "v3 round-trip and backup");
    Require(store.Load().Items[0].Options is ClockOptions { AutoInvertText: false } &&
        store.Load().Items[1].Options is ClockOptions { AutoInvertText: true, TextColor: manualClockColor },
        "clock auto-inversion is per widget, opt-in for older clocks and retains the manual color");

    var json = JsonNode.Parse(File.ReadAllText(paths.Widgets))!.AsObject();
    var unknown = JsonNode.Parse("""
        {"Type":"future-photo","Id":"640d1378-dbb4-43b2-a12d-7542f96636cc",
         "Visibility":"new-mode","Placement":{"X":7},"Options":{"Asset":["keep",4]},"Extra":true}
        """)!;
    json["Items"]!.AsArray().Add(unknown);
    File.WriteAllText(paths.Widgets, json.ToJsonString());
    var mixed = store.Load();
    store.Save(mixed with { Items = mixed.Items.RemoveAt(0) });
    Require(mixed.Items.Last().Options is UnknownWidgetOptions &&
        JsonNode.DeepEquals(unknown, JsonNode.Parse(File.ReadAllText(paths.Widgets))!["Items"]!.AsArray().Last()),
        "unknown widget survives editing and serialization");
    store.Save(new([]));
    Require(store.Load().Items.IsEmpty, "intentionally empty widget list stays empty");

    const string future = """{"Version":9,"Items":[{"Future":true}]}""";
    File.WriteAllText(paths.Widgets, future);
    Require(store.Load().ReadOnlyReason is not null, "newer document remains read-only");
    bool rejected = false;
    try { store.Save(saved); }
    catch (IOException) { rejected = true; }
    Require(rejected && File.ReadAllText(paths.Widgets) == future, "newer document stays intact");

    var operations = new List<bool>();
    var session = new WidgetEditSession(saved, id, (_, commit) => operations.Add(commit));
    var beforeDrag = session.State;
    session.Preview(session.WithPlacement(new(new(.28f, .65f), 1.4)));
    session.Preview(session.WithPlacement(new(new(.34f, .61f), 1.4)));
    Require(!session.CanUndo && operations.SequenceEqual([false, false]), "drag previews skip disk and undo");
    session.Commit(beforeDrag, session.State);
    session.Undo();
    Require(session.Selected?.Placement == beforeDrag.Document.Items[0].Placement, "undo reverts whole drag in one step");
    session.Redo();
    session.RemoveSelected();
    session.Undo();
    Require(session.Selected is { Id: var restored } && restored == id && session.State.Document.Items.Length == 2,
        "undo deletion restores identity and selection");

    var display = new Rectangle(200, 80, 1920, 1080);
    var work = Rectangle.FromLTRB(200, 80, 2120, 1120);
    var dock = Rectangle.FromLTRB(1884, 80, 2120, 1120);
    var covered = new Rectangle(1498, 503, 365, 194);
    var position = WidgetPlacementPolicy.Find(display, work, dock, covered.Size, [covered], 14);
    var placed = new Rectangle((int)Math.Round(display.X + position.X * display.Width - covered.Width / 2f),
        (int)Math.Round(display.Y + position.Y * display.Height - covered.Height / 2f), covered.Width, covered.Height);
    Require(work.Contains(placed) && placed.Right < dock.Left && !placed.IntersectsWith(covered),
        "Dock placement stays in the work area and skips occupied space");
    var bounds = new SceneViewport(1440, 810, display).ToOutput(new(720, 405), new(75, 45));
    Require(bounds == new RectangleF(1060, 560, 200, 120), "draw and hit boxes share the DPR and monitor transform");

    var blurSize = GaussianBlurKernel.TargetSize(1920, 1080, 18, out int blurGrid);
    Require(blurSize == new Size(960, 540) && blurGrid == 2, "full-scene blur at 18 covers the background before filtering");
    blurSize = GaussianBlurKernel.TargetSize(7680, 4320, 144, out blurGrid);
    Require(blurSize == new Size(480, 270) && blurGrid == 8, "wide high-DPR blur uses a bounded kernel");

    Span<Vector4> blurKernel = stackalloc Vector4[GaussianBlurKernel.VectorCount];
    GaussianBlurKernel.Write(blurKernel, radius: 9, downsampleGrid: 2);
    float totalWeight = blurKernel[0].X;
    for (int pair = 1; pair <= 5; pair++)
    {
        float firstTexel = pair * 2 - 1;
        var tap = blurKernel[pair];
        Require(tap.Y > 0 && (pair == 5 ? tap.X == firstTexel : tap.X > firstTexel && tap.X < firstTexel + 1),
            $"blur tap pair {pair} covers adjacent texels through the radius");
        totalWeight += 2 * tap.Y;
    }
    Require(blurKernel[0].Y == 5 && MathF.Abs(totalWeight - 1) < .00001f, "full-scene blur preserves flat colors");

    using var lightImage = new Bitmap(128, 128);
    using var lightCanvas = Graphics.FromImage(lightImage);
    lightCanvas.Clear(Color.FromArgb(24, 24, 24));
    lightCanvas.FillRectangle(Brushes.Red, 5, 7, 20, 20);
    lightCanvas.FillRectangle(Brushes.Cyan, 98, 78, 20, 20);
    var lights = new WallpaperLightMap(lightImage);
    var screen = new Vector2(1920, 1080);
    var source = lights.Find(screen, Vector2.One, 1, Vector2.Zero, tile: false);
    Require(source is { } cyan && cyan.Position.X > 1500 && cyan.Position.Y > 650 &&
        cyan.Color.Z > cyan.Color.X + .3f, "visible dominant light preserves its cool color and screen location");
    var cropped = lights.Find(screen, Vector2.One, 2, new(.65f, .65f), tile: false);
    Require(cropped is { } warm && warm.Position.X < 950 && warm.Position.Y < 600 &&
        warm.Color.X > warm.Color.Z + .3f, "cropping excludes the hidden light and remaps the visible warm light");
    var fixture = source!.Value;
    Require(Math.Abs(fixture.At(fixture.Position - new Vector2(200, 0), screen.Y).Angle) < .001 &&
        Math.Abs(Math.Abs(fixture.At(fixture.Position + new Vector2(200, 0), screen.Y).Angle) - MathF.PI) < .001 &&
        fixture.At(fixture.Position, screen.Y).Focus == 0,
        "widgets on opposite sides of the light face opposite directions with a neutral center");
    lightCanvas.Clear(Color.LightGray);
    Require(new WallpaperLightMap(lightImage).Find(screen, Vector2.One, 1, Vector2.Zero, tile: false) is null,
        "flat wallpaper falls back to manual highlight");

    var scene = new WidgetScene(catalog);
    DateTime minute = new(2026, 1, 2, 14, 35, 0);
    scene.Update(saved, isIdle: false, isEditing: false, minute);
    Require(scene.Visible.Single().Instance.Id == added.Id, "new clock remains visible during playback");
    scene.Update(saved, isIdle: false, isEditing: true, minute);
    Require(scene.Visible.Count == 2, "editor shows idle and persistent widgets");
    Require(!scene.Update(saved, isIdle: false, isEditing: true, minute.AddSeconds(20)) &&
        scene.Next(20, minute.AddSeconds(20)) == 60, "idle scene schedules the next minute without redraws");

    var feedback = new WidgetPressFeedback();
    feedback.Begin(added.Id, 2);
    feedback.Animate(.02, 2.02, heldWidget: null, reduceMotion: false);
    Require(feedback.WidgetId == added.Id && feedback.Press > 0 && feedback.IsAnimating,
        "a tap that releases before the next frame still animates");
    for (int frameIndex = 2; frameIndex <= 60; frameIndex++)
        feedback.Animate(.02, 2 + frameIndex * .02, heldWidget: null, reduceMotion: false);
    Require(feedback.Press == 0 && !feedback.IsAnimating, "tap feedback settles without continuous redraws");
    feedback.Begin(id, 4);
    feedback.Animate(.02, 4.02, heldWidget: null, reduceMotion: true);
    var viewport = new SceneViewport(1440, 810, display);
    var cards = new List<GlassCard>();
    var sampleText = new WidgetTestText();
    var stable = WidgetLayer.Collect(scene.Visible, viewport, default, sampleText, null, 0, true, false, cards);
    Require(!cards[0].AutoInvertText && cards[1] is { AutoInvertText: true, Color: manualClockColor },
        "widget rendering receives the saved auto-inversion flag and manual fallback color");
    var rest = cards[0];
    cards.Clear();
    var pressed = WidgetLayer.Collect(scene.Visible, viewport, default, sampleText, id, 1, true, false, cards);
    Require(pressed.SequenceEqual(stable) && cards[0].Half.X > rest.Half.X && cards[1].Press == 0,
        "desktop tap affects only the pressed glass while leaving hit regions stable");
    cards.Clear();
    WidgetLayer.Collect(scene.Visible, viewport, default, sampleText, feedback.WidgetId, feedback.Press, false, false, cards);
    Require(cards[0].Half == rest.Half && cards[0].Press == 1, "reduced motion shows feedback without scaling the widget");

    File.WriteAllText(paths.Wallpaper, """{"FontScale":120,"Horizontal":12,"FullSceneBlur":true,"RenderScale":0.75,"Chromatic":true}""");
    var wallpaper = new WallpaperStore(paths.Wallpaper);
    var legacyWallpaper = wallpaper.Load();
    Require(legacyWallpaper is { FontScale: 120, Horizontal: 12, FullSceneBlur: true, RenderScale: .75 },
        "existing wallpaper settings");
    Require(legacyWallpaper.ToSceneStyle().Glass is { OpticalDepth: true, ContinuousCorners: true, InnerShadow.Alpha: 0 },
        "older wallpaper files use the new lens and corner defaults without enabling inner shadow");

    wallpaper.Save(legacyWallpaper with
    {
        OpticalDepth = false, ContinuousCorners = false, InnerShadow = true,
        InnerShadowAlpha = 1.5, InnerShadowRadius = 40, InnerShadowX = -50, InnerShadowY = 12
    });
    var material = wallpaper.Load().ToSceneStyle();
    Require(material.Glass is { OpticalDepth: false, ContinuousCorners: false, InnerShadow: var inner } &&
        inner == new GlassInnerShadow(1, 32, -24, 12) && material.Lyrics.Depth,
        "glass options persist, clamp inner shadow and leave lyric depth independent");
    Require((legacyWallpaper with { DepthEffect = false }).ToSceneStyle().Glass.OpticalDepth,
        "turning off lyric depth does not change optical depth");
    var legacyStyle = legacyWallpaper.ToSceneStyle();
    Require(legacyStyle.Glass is { Tone.Dispersion: ChromaticMode.Rgb, ProgressiveBlur: false, HueTint: false } &&
        !legacyStyle.Glass.Highlight.FollowWallpaper && legacyStyle.Lyrics.AdaptiveContrast,
        "legacy RGB stays RGB, with manual highlight and adaptive outline defaults");

    wallpaper.Save(legacyWallpaper with
    {
        ProgressiveBlur = true, SpectralDispersion = true, HueTint = true, AdaptiveContrast = false, BehindGlass = true,
        WidgetWallpaperHighlight = true
    });
    var advancedStyle = wallpaper.Load().ToSceneStyle();
    Require(advancedStyle.Glass is { ProgressiveBlur: true, Tone.Dispersion: ChromaticMode.Spectral, HueTint: true,
        Highlight.FollowWallpaper: true } &&
        advancedStyle.Lyrics is { AdaptiveContrast: false, BehindGlass: true },
        "advanced glass options persist and spectral dispersion takes precedence over legacy RGB");
    Require(new WallpaperSettings().Dispersion == ChromaticMode.Off, "spectral dispersion remains opt-in");

    Directory.CreateDirectory(paths.Logs);
    File.WriteAllText(paths.Playback, """{"music":{"enable_netease":false,"enable_apple":false},"extra":{"future":1}}""");
    var playbackSettings = new PlaybackSettingsStore(paths);
    playbackSettings.Edit(settings => PlaybackSettingsStore.Section(settings, "music")["apple_poll_interval"] = .3);
    Require(playbackSettings.Load()["extra"]?["future"]?.GetValue<int>() == 1, "playback settings retain unknown fields");
    using var playback = new LyricsService(paths.Playback, paths.Logs);
    Require(SpinWait.SpinUntil(() => playback.Latest is { HasSong: false }, TimeSpan.FromSeconds(3)),
        "in-process playback service publishes initial state");
    var firstSnapshot = playback.Latest;
    playback.Restart();
    Require(SpinWait.SpinUntil(() => playback.Latest is { HasSong: false } next && !ReferenceEquals(next, firstSnapshot),
        TimeSpan.FromSeconds(3)), "playback configuration restart republishes state");

    var timeline = new LyricTimeline();
    var track = new PlaybackTrack("测试", "歌手", "[00:01]第一句\n[00:04]第二句", "[00:01]译文");
    timeline.Update(new(true, false, 1, track, Stopwatch.GetTimestamp()), 0);
    var frame = timeline.At(0, .1, new WallpaperSettings());
    Require(!frame.Idle && frame.Lines.Any(line => line.Translation == "译文") &&
        timeline.At(31, .1, new WallpaperSettings()).Idle, "lyrics timeline keeps translation and idle transitions");
}
finally
{
    var logs = Path.Combine(folder.FullName, "logs");
    if (Directory.Exists(logs))
    {
        foreach (var file in Directory.EnumerateFiles(logs)) File.Delete(file);
        Directory.Delete(logs);
    }
    foreach (var file in folder.EnumerateFiles()) file.Delete();
    folder.Delete();
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

file sealed class WidgetTestText : ISceneText
{
    public TextGlyph Get(SceneText text) => new(new(190, 76), default);
}
