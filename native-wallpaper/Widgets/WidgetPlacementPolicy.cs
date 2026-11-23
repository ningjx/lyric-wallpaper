namespace LyricWallpaper.Widgets;

internal static class WidgetPlacementPolicy
{
    public static WidgetPosition Find(Rectangle display, Rectangle workingArea, Rectangle dock, Size requested,
        IReadOnlyList<Rectangle> occupied, int margin)
    {
        // Prefer the region immediately to the left of the Dock.
        int right = Math.Min(workingArea.Right, dock.Left - margin);
        if (right - workingArea.Left < requested.Width + margin) right = workingArea.Right;
        var area = Rectangle.FromLTRB(workingArea.Left, workingArea.Top, right, workingArea.Bottom);
        if (area.Width <= 0 || area.Height <= 0) return WidgetPosition.Center;
        int width = Math.Min(requested.Width, area.Width), height = Math.Min(requested.Height, area.Height);
        int xStep = Math.Max(1, width / 3), yStep = Math.Max(1, height / 3);
        int middle = Math.Clamp(workingArea.Top + (workingArea.Height - height) / 2, area.Top, area.Bottom - height);
        var best = new Rectangle(area.Right - width, middle, width, height);
        long lowestOverlap = long.MaxValue;

        for (int x = area.Right - width; ; x = Math.Max(area.Left, x - xStep))
        {
            foreach (int y in Rows(middle, area.Top, area.Bottom - height, yStep))
            {
                var candidate = new Rectangle(x, y, width, height);
                long overlap = Overlap(candidate, dock);
                foreach (var other in occupied) overlap += Overlap(candidate, other);
                if (overlap >= lowestOverlap) continue;
                best = candidate;
                lowestOverlap = overlap;
                if (overlap == 0) return Normalize(best, display);
            }
            if (x == area.Left) break;
        }
        return Normalize(best, display);
    }

    private static IEnumerable<int> Rows(int middle, int top, int bottom, int step)
    {
        yield return middle;
        for (int distance = step; middle - distance > top || middle + distance < bottom; distance += step)
        {
            if (middle - distance > top) yield return middle - distance;
            if (middle + distance < bottom) yield return middle + distance;
        }
        if (top != middle) yield return top;
        if (bottom != middle && bottom != top) yield return bottom;
    }

    private static long Overlap(Rectangle first, Rectangle second)
    {
        var overlap = Rectangle.Intersect(first, second);
        return (long)overlap.Width * overlap.Height;
    }

    private static WidgetPosition Normalize(Rectangle card, Rectangle display) => new(
        Math.Clamp((card.Left + card.Width / 2f - display.Left) / display.Width, 0, 1),
        Math.Clamp((card.Top + card.Height / 2f - display.Top) / display.Height, 0, 1));
}
