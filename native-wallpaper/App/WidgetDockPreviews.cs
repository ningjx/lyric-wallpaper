using System.Collections.Immutable;
using LyricWallpaper.Rendering;
using LyricWallpaper.Scene;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.App;

internal sealed class WidgetDockPreviews(WidgetCatalog catalog)
{
    private readonly WidgetScene scene = new(catalog);
    private readonly WidgetDocument samples = new(catalog.Definitions
        .Select(definition => catalog.Create(definition, WidgetPosition.Center)).ToImmutableArray());
    private bool refresh = true;

    public void Invalidate() => refresh = true;

    public double Next(double now, DateTime time) => scene.Next(now, time);

    public WidgetPreviewFrame[]? Render(D3D11Renderer renderer, SceneStyle style, TextAtlas text, DateTime time)
    {
        bool contentChanged = scene.Update(samples, isIdle: false, isEditing: true, time);
        if (!refresh && !contentChanged) return null;

        var viewport = new SceneViewport(renderer.Width, renderer.Height, new(0, 0, renderer.Width, renderer.Height));
        var cards = new List<GlassCard>(1);
        var frames = new List<WidgetPreviewFrame>(scene.Visible.Count);
        foreach (var widget in scene.Visible)
        {
            text.BeginFrame();
            cards.Clear();
            WidgetLayer.Collect([widget], viewport, style.Glass, text, null, 0, false, false, cards);
            if (cards.Count > 0) frames.Add(renderer.DrawWidgetPreview(widget.Instance.Type, style, cards[0]));
        }
        refresh = false;
        return frames.ToArray();
    }
}
