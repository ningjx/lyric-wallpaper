namespace LyricWallpaper.Widgets;

internal readonly record struct WidgetVisual(WidgetInstance Instance, WidgetContent Content);

internal sealed class WidgetScene(WidgetCatalog catalog)
{
    private WidgetDocument? previous;
    private bool idle, editing;
    private DateTime lastRead, refreshAt;
    private readonly List<WidgetVisual> visible = [];
    private readonly Dictionary<(string Type, WidgetOptions Options), WidgetContent> cached = new();
    public IReadOnlyList<WidgetVisual> Visible => visible;

    public bool Update(WidgetDocument document, bool isIdle, bool isEditing, DateTime time)
    {
        if (previous == document && idle == isIdle && editing == isEditing && time >= lastRead && time < refreshAt) return false;
        bool timeChanged = time < lastRead || time >= refreshAt;
        var previousDocument = previous;
        (previous, idle, editing, lastRead) = (document, isIdle, isEditing, time);
        if (timeChanged || previousDocument != document) cached.Clear();
        bool changed = false;
        int count = 0;
        refreshAt = DateTime.MaxValue;
        foreach (var item in document.Items)
        {
            if (!editing && !idle && item.Visibility != WidgetVisibility.Always) continue;
            if (item.Options is UnknownWidgetOptions || catalog.Find(item.Type) is not { } definition) continue;
            var key = (item.Type, item.Options);
            if (!cached.TryGetValue(key, out var content)) cached[key] = content = definition.Read(item.Options, time);
            if (content.RefreshAt < refreshAt) refreshAt = content.RefreshAt;
            var visual = new WidgetVisual(item, content);
            if (count == visible.Count) { visible.Add(visual); changed = true; }
            else if (visible[count] != visual) { visible[count] = visual; changed = true; }
            count++;
        }
        if (visible.Count > count)
        {
            visible.RemoveRange(count, visible.Count - count);
            changed = true;
        }
        return changed;
    }

    public double Next(double now, DateTime time) =>
        refreshAt == DateTime.MaxValue ? double.PositiveInfinity : now + Math.Max(0, (refreshAt - time).TotalSeconds);
}
