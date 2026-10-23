namespace VNotch.Services.Clipboard;

/// <summary>Selection policy independent of realized WPF card containers.</summary>
internal sealed class ClipboardSelectionModel<T> where T : class
{
    private readonly HashSet<T> _seed = [];
    private readonly HashSet<T> _targets = [];
    internal T? Anchor { get; private set; }
    internal bool Modified { get; private set; }
    internal IReadOnlySet<T> Targets => _targets;

    internal void BeginLasso(IEnumerable<T> selected, bool extend)
    {
        _seed.Clear();
        if (extend) _seed.UnionWith(selected);
        ResetLassoTargets();
    }

    internal void ResetLassoTargets()
    {
        _targets.Clear();
        _targets.UnionWith(_seed);
    }

    internal void IncludeRange(IList<T> items, int first, int last)
    {
        for (int i = Math.Max(0, first); i <= Math.Min(items.Count - 1, last); i++) _targets.Add(items[i]);
    }

    internal IReadOnlySet<T> Select(IList<T> items, IEnumerable<T> selected, T item,
        bool control, bool shift, bool anyModifier)
    {
        _targets.Clear();
        _targets.UnionWith(selected);
        Modified = anyModifier || _targets.Count > 1;
        if (shift && Anchor != null)
        {
            int start = items.IndexOf(Anchor), end = items.IndexOf(item);
            if (start >= 0 && end >= 0) IncludeRange(items, Math.Min(start, end), Math.Max(start, end));
        }
        else if (control)
        {
            if (!_targets.Remove(item)) _targets.Add(item);
        }
        else if (!_targets.Contains(item))
        {
            _targets.Clear();
            _targets.Add(item);
        }
        Anchor = item;
        return _targets;
    }

    internal void EndLasso() { _seed.Clear(); _targets.Clear(); }
    internal void Reset() { EndLasso(); Anchor = null; Modified = false; }
}
