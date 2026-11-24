using System.Numerics;
using LyricWallpaper.Scene;

namespace LyricWallpaper.Widgets;

internal static class WidgetLayer
{
    public static WidgetLayout[] Collect(IReadOnlyList<WidgetVisual> items, SceneViewport viewport, GlassMaterial glass,
        ISceneText text, Guid? feedback, float press, bool animate, bool editing, List<GlassCard> cards)
    {
        var layouts = new List<WidgetLayout>(items.Count);
        foreach (var item in items)
        {
            var glyph = text.Get(item.Content.Text);
            if (glyph.InkSize == Vector2.Zero) continue;
            float pressure = feedback == item.Instance.Id ? press : 0;
            float feedbackScale = animate ? 1 + .04f * pressure : 1;
            float scale = (float)item.Instance.Placement.Scale * (editing ? feedbackScale : 1);
            float padding = item.Content.Padding * viewport.Height / 1080f * scale;
            Vector2 ink = glyph.InkSize * (viewport.Width * .05f * .87f * scale / ISceneText.FontPixels);
            Vector2 half = ink / 2 + new Vector2(padding);
            float fit = Math.Min(1, Math.Min(viewport.Width / (2 * half.X), viewport.Height / (2 * half.Y)));
            half *= fit;
            ink *= fit;
            padding *= fit;
            var screenSize = new Vector2(viewport.Width, viewport.Height);
            Vector2 center = item.Instance.Placement.Position.InPixels(screenSize, half);
            // Outside editing the glass moves under a fixed hit region so a tap stays clickable at its edges.
            float surfaceScale = editing ? 1 : feedbackScale;
            if (surfaceScale > 1)
            {
                var fittingScale = Vector2.Min(center, screenSize - center) / half;
                surfaceScale = Math.Min(surfaceScale, Math.Min(fittingScale.X, fittingScale.Y));
            }
            cards.Add(new(center, half * surfaceScale, ink * surfaceScale, glyph.Uv,
                item.Content.Color, 1, padding * surfaceScale, pressure, item.Content.AutoInvertText));
            float sy = viewport.Output.Height / (float)viewport.Height;
            int radius = Math.Max(0, (int)(Math.Min(Math.Min(glass.Radius * viewport.Height / 1080f,
                padding * 3.41421356f), Math.Min(half.X, half.Y) - 1) * sy));
            layouts.Add(new(item.Instance.Id, viewport.ToOutput(center, half), radius));
        }
        return layouts.ToArray();
    }
}
