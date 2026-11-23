namespace LyricWallpaper.Widgets;

internal sealed record WidgetEditSnapshot(WidgetDocument Document, Guid? Selected);

internal sealed class WidgetEditSession(WidgetDocument document, Guid? selected, Action<WidgetDocument, bool> apply)
{
    private readonly Stack<WidgetEditSnapshot> history = new(), future = new();
    public WidgetEditSnapshot State { get; private set; } = new(document,
        selected ?? document.Items.LastOrDefault(VisibleInEditor)?.Id);

    public WidgetInstance? Selected => State.Document.Items.FirstOrDefault(item => item.Id == State.Selected);
    public bool CanEdit => State.Document.ReadOnlyReason is null;
    public bool CanUndo => CanEdit && history.Count > 0;
    public bool CanRedo => CanEdit && future.Count > 0;

    public static bool VisibleInEditor(WidgetInstance item) => item.Options is not UnknownWidgetOptions;

    public void Select(Guid id)
    {
        if (State.Document.Items.Any(item => item.Id == id && VisibleInEditor(item)))
            State = State with { Selected = id };
    }

    public void SelectAdjacent(int offset)
    {
        var items = State.Document.Items.Where(VisibleInEditor).ToArray();
        if (items.Length == 0) return;
        int index = Array.FindIndex(items, item => item.Id == State.Selected);
        Select(items[(Math.Max(0, index) + offset + items.Length) % items.Length].Id);
    }

    public WidgetEditSnapshot WithPlacement(WidgetPlacement placement) =>
        WithSelected(item => item with { Placement = placement.Normalize() });

    public WidgetEditSnapshot WithOptions(WidgetOptions options) => WithSelected(item => item with { Options = options });

    public WidgetEditSnapshot WithVisibility(WidgetVisibility visibility) => WithSelected(item => item with { Visibility = visibility });

    private WidgetEditSnapshot WithSelected(Func<WidgetInstance, WidgetInstance> change)
    {
        if (Selected is not { } item) return State;
        var next = change(item);
        return item == next ? State : State with { Document = State.Document with { Items = State.Document.Items.Replace(item, next) } };
    }

    public void Add(WidgetInstance item) =>
        Commit(State, new(State.Document with { Items = State.Document.Items.Add(item) }, item.Id));

    public void RemoveSelected()
    {
        if (Selected is not { } item) return;
        var source = State.Document.Items;
        int index = source.IndexOf(item);
        var items = source.RemoveAt(index);
        Guid? next = (items.Skip(index).FirstOrDefault(VisibleInEditor) ??
            items.Take(index).LastOrDefault(VisibleInEditor))?.Id;
        Commit(State, new(State.Document with { Items = items }, next));
    }

    public void Preview(WidgetEditSnapshot next)
    {
        if (!CanEdit || State == next) return;
        apply(next.Document, false);
        State = next;
    }

    public void Commit(WidgetEditSnapshot before, WidgetEditSnapshot next)
    {
        if (!CanEdit || before.Document == next.Document) return;
        Save(next);
        history.Push(before);
        future.Clear();
    }

    public void Undo() => Restore(history, future);
    public void Redo() => Restore(future, history);

    private void Restore(Stack<WidgetEditSnapshot> from, Stack<WidgetEditSnapshot> to)
    {
        if (!CanEdit || !from.TryPeek(out var next)) return;
        var before = State;
        Save(next);
        from.Pop();
        to.Push(before);
    }

    private void Save(WidgetEditSnapshot next)
    {
        apply(next.Document, true);
        State = next;
    }
}
