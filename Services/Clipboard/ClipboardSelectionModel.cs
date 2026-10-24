using System.Runtime.InteropServices;

namespace VNotch.Services.Clipboard;

internal static partial class ClipboardSelectionModelNative
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    internal static partial int vn_clipboard_selection_clamp_range(
        int itemCount,
        int first,
        int last,
        out int outStart,
        out int outEnd);

    [LibraryImport(NativeDll)]
    internal static partial int vn_clipboard_selection_resolve_action(
        byte shift,
        byte hasAnchor,
        int anchorIndex,
        int itemIndex,
        byte control,
        byte alreadyInTargets,
        out int outAction,
        out int outStart,
        out int outEnd);
}

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
        _ = ClipboardSelectionModelNative.vn_clipboard_selection_clamp_range(items.Count, first, last, out int start, out int end);
        for (int i = start; i <= end; i++) _targets.Add(items[i]);
    }

    internal IReadOnlySet<T> Select(IList<T> items, IEnumerable<T> selected, T item,
        bool control, bool shift, bool anyModifier)
    {
        _targets.Clear();
        _targets.UnionWith(selected);
        Modified = anyModifier || _targets.Count > 1;
        int anchorIdx = (shift && Anchor != null) ? items.IndexOf(Anchor) : -1;
        int itemIdx = (shift && Anchor != null) ? items.IndexOf(item) : -1;
        _ = ClipboardSelectionModelNative.vn_clipboard_selection_resolve_action(
            shift ? (byte)1 : (byte)0,
            Anchor != null ? (byte)1 : (byte)0,
            anchorIdx,
            itemIdx,
            control ? (byte)1 : (byte)0,
            _targets.Contains(item) ? (byte)1 : (byte)0,
            out int action,
            out int rangeStart,
            out int rangeEnd);
        if (action == 1)
        {
            IncludeRange(items, rangeStart, rangeEnd);
        }
        else if (action == 2)
        {
            if (!_targets.Remove(item)) _targets.Add(item);
        }
        else if (action == 3)
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
