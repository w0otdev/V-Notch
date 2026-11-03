using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_selection_scroll_speed(
        double x,
        double width,
        out double outSpeed);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_selection_compute_lasso_range(
        int cardCount,
        int refIndex,
        double containerWidth,
        double marginLeft,
        double marginRight,
        double originX,
        double originY,
        double containerHeight,
        double scrollOffset,
        double rectLeft,
        double rectTop,
        double rectRight,
        double rectBottom,
        double rowTop,
        double rowBottom,
        byte swept,
        out int outFirst,
        out int outLast,
        out byte outHitsRow,
        out byte outExpandVertical);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_selection_filter_ordered_indices(
        ReadOnlySpan<int> selectedIndices,
        int selectedCount,
        int cardCount,
        Span<byte> scratchMask,
        Span<int> outOrderedIndices,
        out int outCount);

    private (string Category, string Query)? _pendingSelectAll;

    private bool SelectAllTrayItems(DependencyObject? source)
    {
        if (_disposed || _trayLeaving || !IsVisible || LockPanel.IsVisible || DetailPanel.IsVisible ||
            ApprovalPanel.IsVisible || DeleteConfirmationPanel.IsVisible) return false;
        for (var current = source; current != null && current != this;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBoxBase or PasswordBox) return false;

        EndSelection();
        _pendingLassoStart = null;
        _pressedCard = null;
        _dragStart = null;
        _copyOnRelease = false;
        Cards.Focus();
        // Select data items, including those with no realized container or thumbnail.
        Cards.SelectAll();
        _pendingSelectAll = _controller != null && _queryDirty ? (_category, SearchBox.Text) : null;
        if (_pendingSelectAll != null) QueueRefresh();
        return true;
    }

    private void CompletePendingSelectAll()
    {
        if (_pendingSelectAll is not { } scope) return;
        _pendingSelectAll = null;
        if (scope.Category == _category && scope.Query == SearchBox.Text)
            Cards.SelectAll();
    }
    private Point? _selectionStart;
    private Point? _pendingLassoStart;
    private readonly ClipboardSelectionModel<ClipboardCardViewModel> _selection = new();
    private readonly HashSet<ListBoxItem> _selectionContainers = [];
    private Point _selectionPointer;
    private bool _selectionBoundsDirty;
    private bool _selectionUpdatePending;
    private bool _selectionHasMoved;
    private double _selectionStartOffset;
    private long _selectionFrameTime;
    private int _lastLassoFirst = -2;
    private int _lastLassoLast = -2;
    private bool _lastLassoHitsRow;
    private bool _lassoTargetsApplied;

    private void InitializeSelection()
    {
        PreviewMouseDown += (_, _) => _pendingSelectAll = null;
        SelectionSurface.PreviewMouseLeftButtonDown += Selection_Down;
        SelectionSurface.PreviewMouseMove += Selection_Move;
        SelectionSurface.PreviewMouseLeftButtonUp += Selection_Up;
        SelectionSurface.SizeChanged += Selection_LayoutChanged;
        Cards.SizeChanged += Selection_LayoutChanged;
        Cards.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Selection_ScrollChanged));
        SelectionSurface.LostMouseCapture += (_, e) =>
        {
            if (e.OriginalSource == SelectionSurface && _selectionStart != null && Mouse.Captured != SelectionSurface)
                EndSelection();
        };
        SelectionSurface.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _selectionStart == null) return;
            EndSelection();
            e.Handled = true;
        };
    }

    private void Selection_Down(object sender, MouseButtonEventArgs e)
    {
        if (Cards.Visibility != Visibility.Visible || LockPanel.IsVisible || DetailPanel.IsVisible ||
            ApprovalPanel.IsVisible || DeleteConfirmationPanel.IsVisible || LabelEditorPanel.IsVisible) return;
        if (IsSelectionControl(e.OriginalSource as DependencyObject)) return;
        if (ItemsControl.ContainerFromElement(Cards, e.OriginalSource as DependencyObject) is ListBoxItem item &&
            (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            // Selected cards keep their file drag. Ctrl/Shift lets the user
            // extend a lasso from any card without finding an empty gutter.
            bool extendSelection = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            _pendingLassoStart = (!item.IsSelected || extendSelection) && e.ClickCount == 1
                ? e.GetPosition(SelectionSurface) : null;
            return;
        }
        BeginSelection(e.GetPosition(SelectionSurface));
        e.Handled = true;
    }

    private bool IsSelectionControl(DependencyObject? source)
    {
        while (source != null && source != SelectionSurface)
        {
            if (source == SearchSurface || source == CategoryScroll ||
                source is ButtonBase or TextBoxBase or PasswordBox or ScrollBar or MenuItem) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void BeginSelection(Point start)
    {
        _searchCancellation?.Cancel();
        _pendingLassoStart = null;
        _dragStart = null;
        _pressedCard = null;
        _copyOnRelease = false;
        CompositionTarget.Rendering -= Selection_Rendering;
        Cards.Focus();
        _selectionStart = start;
        StopCardsScrollFrames();
        _selectionStartOffset = FindScrollViewer(Cards)?.HorizontalOffset ?? 0;
        _selectionFrameTime = Stopwatch.GetTimestamp();
        bool extend = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _selection.BeginLasso(ExtractSelectedCardViewModels(), extend);
        if (!extend) Cards.ApplySelection(Array.Empty<ClipboardCardViewModel>());
        _selectionPointer = start;
        _selectionHasMoved = false;
        _selectionBoundsDirty = true;
        _selectionUpdatePending = true;
        _lastLassoFirst = -2;
        _lastLassoLast = -2;
        _lastLassoHitsRow = false;
        _lassoTargetsApplied = false;
        SelectionSurface.Cursor = Cursors.Cross;
        if (!Mouse.Capture(SelectionSurface)) { EndSelection(); return; }
        CompositionTarget.Rendering += Selection_Rendering;
    }

    private ClipboardCardViewModel[] ExtractSelectedCardViewModels()
    {
        int count = Cards.SelectedItems.Count;
        if (count == 0) return [];
        var result = new ClipboardCardViewModel[count];
        int writeIdx = 0;
        foreach (var obj in Cards.SelectedItems)
        {
            if (obj is ClipboardCardViewModel vm)
                result[writeIdx++] = vm;
        }
        return writeIdx == result.Length ? result : result[..writeIdx];
    }

    private bool AreSelectionTargetsEqualToSelectedItems()
    {
        var targets = _selection.Targets;
        if (targets.Count != Cards.SelectedItems.Count) return false;
        foreach (var obj in Cards.SelectedItems)
        {
            if (obj is not ClipboardCardViewModel vm || !targets.Contains(vm))
                return false;
        }
        return true;
    }

    private void Selection_Move(object sender, MouseEventArgs e)
    {
        if (_pendingLassoStart is Point pending && e.LeftButton == MouseButtonState.Pressed)
        {
            var pointer = e.GetPosition(SelectionSurface);
            if (Math.Abs(pointer.X - pending.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pointer.Y - pending.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            BeginSelection(pending);
        }
        if (_selectionStart == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndSelection(); return; }
        QueueSelectionPointer(e.GetPosition(SelectionSurface));
        e.Handled = true;
    }

    private void Selection_Up(object sender, MouseButtonEventArgs e)
    {
        _pendingLassoStart = null;
        if (_selectionStart == null) return;
        // Commit the release position even if the next render frame has not run.
        QueueSelectionPointer(e.GetPosition(SelectionSurface));
        ApplyPendingSelection();
        EndSelection();
        e.Handled = true;
    }

    private void QueueSelectionPointer(Point current)
    {
        _selectionPointer = new Point(Math.Clamp(current.X, 0, SelectionSurface.ActualWidth),
            Math.Clamp(current.Y, 0, SelectionSurface.ActualHeight));
        if (_selectionStart is Point start &&
            (Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance))
            _selectionHasMoved = true;
        _selectionUpdatePending = true;
    }

    private void Selection_Rendering(object? sender, EventArgs e)
    {
        if (_disposed || !IsLoaded || !IsVisible || Mouse.Captured != SelectionSurface || Mouse.LeftButton != MouseButtonState.Pressed)
        { EndSelection(); return; }
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Clamp((now - _selectionFrameTime) / (double)Stopwatch.Frequency, 0, 0.05);
        _selectionFrameTime = now;
        ScrollSelection(seconds);
        ApplyPendingSelection();
    }

    internal static double SelectionScrollSpeed(double x, double width)
    {
        _ = vn_clipboard_selection_scroll_speed(x, width, out double speed);
        return speed;
    }

    private void ScrollSelection(double seconds)
    {
        if (_selectionStart == null || !_selectionHasMoved) return;
        var scroll = FindScrollViewer(Cards);
        if (scroll == null) return;
        double speed = SelectionScrollSpeed(_selectionPointer.X, SelectionSurface.ActualWidth);
        double next = Math.Clamp(scroll.HorizontalOffset + speed * seconds, 0, scroll.ScrollableWidth);
        if (Math.Abs(next - scroll.HorizontalOffset) < 0.01) return;
        scroll.ScrollToHorizontalOffset(next);
        // Realize the new edge before hit-testing; the pointer may be stationary.
        Cards.UpdateLayout();
        _selectionBoundsDirty = _selectionUpdatePending = true;
    }

    private void ApplyPendingSelection()
    {
        if (_selectionStart is not Point start || (!_selectionUpdatePending && !_selectionBoundsDirty)) return;
        _selectionBoundsDirty = false;
        _selectionUpdatePending = false;
        double offset = FindScrollViewer(Cards)?.HorizontalOffset ?? 0;
        var rectangle = new Rect(new Point(start.X + _selectionStartOffset, start.Y),
            new Point(_selectionPointer.X + offset, _selectionPointer.Y));
        bool swept = rectangle.Width >= SystemParameters.MinimumHorizontalDragDistance ||
            rectangle.Height >= SystemParameters.MinimumVerticalDragDistance;
        Rect row = CardsViewport.TransformToAncestor(SelectionSurface).TransformBounds(new Rect(CardsViewport.RenderSize));
        bool foundContainer = false;
        bool rangeChanged = true;
        // Cards use a uniform horizontal slot. Infer content geometry from a
        // realized container so recycled/offscreen cards stay selected, and can
        // be deselected again when the user shrinks the lasso back across them.
        foreach (var container in _selectionContainers)
        {
            if (container.DataContext is not ClipboardCardViewModel card || !container.IsVisible || container.ActualWidth <= 0) continue;
            int index = card.SlotIndex;
            if (index < 0 || index >= _cards.Count || !ReferenceEquals(_cards[index], card))
                index = _cards.IndexOf(card);
            if (index < 0 || !SelectionSurface.IsAncestorOf(container)) continue;
            foundContainer = true;
            var origin = container.TranslatePoint(new Point(), SelectionSurface);
            _ = vn_clipboard_selection_compute_lasso_range(
                _cards.Count,
                index,
                container.ActualWidth,
                container.Margin.Left,
                container.Margin.Right,
                origin.X,
                origin.Y,
                container.ActualHeight,
                offset,
                rectangle.Left,
                rectangle.Top,
                rectangle.Right,
                rectangle.Bottom,
                row.Top,
                row.Bottom,
                swept ? (byte)1 : (byte)0,
                out int first,
                out int last,
                out byte hitsRowByte,
                out byte expandVerticalByte);
            bool hitsRow = hitsRowByte != 0;
            if (expandVerticalByte != 0)
            {
                // A horizontal sweep through the top/bottom gutter targets the
                // whole row. Expand its visible band so the selected area agrees
                // with the card highlights even when the pointer never enters a card.
                rectangle.Union(new Rect(rectangle.Left, origin.Y, rectangle.Width, container.ActualHeight));
            }
            int normFirst = hitsRow && first <= last ? first : -1;
            int normLast = hitsRow && first <= last ? last : -2;
            if (_lassoTargetsApplied && hitsRow == _lastLassoHitsRow && normFirst == _lastLassoFirst && normLast == _lastLassoLast)
            {
                rangeChanged = false;
            }
            else
            {
                _lastLassoHitsRow = hitsRow;
                _lastLassoFirst = normFirst;
                _lastLassoLast = normLast;
                _lassoTargetsApplied = true;
                _selection.ResetLassoTargets();
                if (hitsRow) _selection.IncludeRange(_cards, first, last);
            }
            break;
        }
        if (!foundContainer)
        {
            _selection.ResetLassoTargets();
            _lassoTargetsApplied = true;
        }
        var visible = rectangle;
        visible.Offset(-offset, 0);
        visible.Intersect(new Rect(SelectionSurface.RenderSize));
        Canvas.SetLeft(SelectionRectangle, visible.IsEmpty ? 0 : visible.Left);
        Canvas.SetTop(SelectionRectangle, visible.IsEmpty ? 0 : visible.Top);
        SelectionRectangle.Width = visible.IsEmpty ? 0 : visible.Width;
        SelectionRectangle.Height = visible.IsEmpty ? 0 : visible.Height;
        SelectionRectangle.Visibility = swept ? Visibility.Visible : Visibility.Collapsed;
        if (rangeChanged && !AreSelectionTargetsEqualToSelectedItems())
        {
            Cards.ApplySelection(_selection.Targets);
        }
    }

    private void RegisterSelectionContainer(ListBoxItem container)
    {
        if (!_selectionContainers.Add(container)) return;
        container.DataContextChanged += Selection_ContainerChanged;
        container.SizeChanged += Selection_LayoutChanged;
        _selectionBoundsDirty = true;
    }

    private void UnregisterSelectionContainer(ListBoxItem container)
    {
        if (!_selectionContainers.Remove(container)) return;
        container.DataContextChanged -= Selection_ContainerChanged;
        container.SizeChanged -= Selection_LayoutChanged;
        _selectionBoundsDirty = true;
    }

    private void Selection_ContainerChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _selectionBoundsDirty = true;
        _lassoTargetsApplied = false;
    }

    private void Selection_LayoutChanged(object sender, SizeChangedEventArgs e) { _selectionBoundsDirty = true; _lassoTargetsApplied = false; }
    private void Selection_ScrollChanged(object sender, ScrollChangedEventArgs e) { _selectionBoundsDirty = true; _lassoTargetsApplied = false; }

    private void ClearSelectionContainers()
    {
        EndSelection();
        if (_selectionContainers.Count == 0) return;
        var copy = new ListBoxItem[_selectionContainers.Count];
        _selectionContainers.CopyTo(copy);
        for (int i = 0; i < copy.Length; i++)
            UnregisterSelectionContainer(copy[i]);
    }

    private void EndSelection()
    {
        _pendingLassoStart = null;
        _selectionStart = null;
        CompositionTarget.Rendering -= Selection_Rendering;
        _selectionUpdatePending = false;
        _selectionHasMoved = false;
        _selection.EndLasso();
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionSurface.ClearValue(CursorProperty);
        if (Mouse.Captured == SelectionSurface) Mouse.Capture(null);
    }

    private void SelectCard(ClipboardCardViewModel card)
    {
        var modifiers = Keyboard.Modifiers;
        Cards.ApplySelection(_selection.Select(_cards, ExtractSelectedCardViewModels(), card,
            (modifiers & ModifierKeys.Control) != 0, (modifiers & ModifierKeys.Shift) != 0, modifiers != ModifierKeys.None));
        Cards.Focus();
    }

    private ClipboardCardViewModel[] SelectedCards()
    {
        int selCount = Cards.SelectedItems.Count;
        if (selCount == 0) return [];
        if (selCount == 1 && Cards.SelectedItems[0] is ClipboardCardViewModel single) return [single];
        int cardCount = _cards.Count;
        if (cardCount == 0) return [];
        if (selCount >= cardCount)
        {
            var all = new ClipboardCardViewModel[cardCount];
            for (int i = 0; i < cardCount; i++) all[i] = _cards[i];
            return all;
        }

        int[]? rentedSel = selCount > 512 ? ArrayPool<int>.Shared.Rent(selCount) : null;
        byte[]? rentedMask = cardCount > 1024 ? ArrayPool<byte>.Shared.Rent(cardCount) : null;
        int[]? rentedOrdered = cardCount > 512 ? ArrayPool<int>.Shared.Rent(cardCount) : null;
        Span<int> selIndices = rentedSel != null ? rentedSel.AsSpan(0, selCount) : stackalloc int[selCount];
        Span<byte> scratchMask = rentedMask != null ? rentedMask.AsSpan(0, cardCount) : stackalloc byte[cardCount];
        Span<int> orderedIndices = rentedOrdered != null ? rentedOrdered.AsSpan(0, cardCount) : stackalloc int[cardCount];

        try
        {
            int idxCount = 0;
            foreach (var obj in Cards.SelectedItems)
            {
                if (obj is not ClipboardCardViewModel vm) continue;
                int slot = vm.SlotIndex;
                if (slot < 0 || slot >= cardCount || !ReferenceEquals(_cards[slot], vm))
                    slot = _cards.IndexOf(vm);
                if (slot >= 0)
                    selIndices[idxCount++] = slot;
            }

            _ = vn_clipboard_selection_filter_ordered_indices(
                selIndices[..idxCount], idxCount, cardCount, scratchMask, orderedIndices, out int outCount);
            if (outCount <= 0) return [];
            var result = new ClipboardCardViewModel[outCount];
            for (int i = 0; i < outCount; i++)
                result[i] = _cards[orderedIndices[i]];
            return result;
        }
        finally
        {
            if (rentedSel != null) ArrayPool<int>.Shared.Return(rentedSel);
            if (rentedMask != null) ArrayPool<byte>.Shared.Return(rentedMask);
            if (rentedOrdered != null) ArrayPool<int>.Shared.Return(rentedOrdered);
        }
    }

    private static ClipboardEntry[] ExtractEntries(ClipboardCardViewModel[] cards)
    {
        if (cards.Length == 0) return [];
        var entries = new ClipboardEntry[cards.Length];
        for (int i = 0; i < cards.Length; i++)
            entries[i] = cards[i].Entry;
        return entries;
    }

    private async Task CopySelectionAsync()
    {
        if (_controller == null) return;
        var cards = SelectedCards();
        if (cards.Length == 1) { await CopyAsync(cards[0]); return; }
        await SafeAsync(() => _controller.CopyManyAsync(ExtractEntries(cards)));
    }
}
