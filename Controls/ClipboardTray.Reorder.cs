using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    private Button? _categoryReorderSource;
    private Point _categoryReorderStart;
    private Point _categoryReorderGrip;
    private double _categoryReorderSourceOpacity;
    private bool _categoryReorderToolTipEnabled;
    private bool _categoryReordering;
    private bool _categoryReorderValid;
    private string? _categoryReorderBeforeId;
    private int _pendingCategoryWrites;
    private string[]? _optimisticCategoryOrder;
    private long _categoryReorderFrameTime;

    private void CategoryReorder_Press(Button button, Point viewportPoint)
    {
        CancelCategoryReorder();
        if (_disposed || _trayLeaving || _controller == null || button.DataContext is not ClipboardCategoryViewModel) return;
        _categoryReorderSource = button;
        _categoryReorderStart = viewportPoint;
        _categoryReorderGrip = CategoryScroll.TranslatePoint(viewportPoint, button);
        _categoryReorderSourceOpacity = button.Opacity;
        _categoryReorderToolTipEnabled = ToolTipService.GetIsEnabled(button);
        // Button keeps the press until the system drag threshold is crossed.
    }

    private bool TryBeginCategoryReorder(Point point)
    {
        if (_categoryReorderSource == null) return false;
        if (Math.Abs(point.X - _categoryReorderStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _categoryReorderStart.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        if (!Mouse.Capture(CategoryScroll, CaptureMode.Element)) { CancelCategoryReorder(); return false; }
        _categoryReordering = true;
        _categoryPress = null;
        StopCategoryScrollFrames();
        BeginCategoryReorderVisuals();
        CategoryScroll.ForceCursor = true;
        _categoryReorderFrameTime = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += CategoryReorder_Frame;
        UpdateCategoryReorder(point);
        return true;
    }

    private Button? CategoryButton(ClipboardCategoryViewModel category) =>
        CategoryPanel.ItemContainerGenerator.ContainerFromItem(category) is ContentPresenter presenter &&
        VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as Button : null;

    private void UpdateCategoryReorder(Point viewportPoint)
    {
        if (!_categoryReordering || _categoryReorderSource?.DataContext is not ClipboardCategoryViewModel source) return;
        _categoryReorderValid = !_disposed && !_trayLeaving && IsVisible && CategoryScroll.IsVisible &&
            CategoryPanel.IsAncestorOf(_categoryReorderSource) &&
            viewportPoint.X >= 0 && viewportPoint.X <= CategoryScroll.ActualWidth &&
            viewportPoint.Y >= 0 && viewportPoint.Y <= CategoryScroll.ActualHeight;
        Canvas.SetLeft(CategoryReorderGhost, viewportPoint.X - _categoryReorderGrip.X);
        Canvas.SetTop(CategoryReorderGhost, Math.Max(0, (CategoryScroll.ActualHeight - _categoryReorderSource.ActualHeight) / 2));
        CategoryReorderGhost.Opacity = _categoryReorderValid ? 1 : 0.5;
        CategoryScroll.Cursor = _categoryReorderValid ? Cursors.SizeAll : Cursors.No;
        CategoryReorderMarker.Visibility = _categoryReorderValid ? Visibility.Visible : Visibility.Collapsed;
        if (!_categoryReorderValid) return;

        _categoryReorderBeforeId = null;
        // Stable original geometry avoids jitter when crossing variable-width tags.
        foreach (var category in _categoryItems)
        {
            if (category.Name == source.Name || CategoryButton(category) is not { } button) continue;
            var origin = CategorySlotOrigin(category);
            if (viewportPoint.X < origin.X + button.ActualWidth / 2)
            {
                _categoryReorderBeforeId = category.Name;
                break;
            }
        }
        UpdateCategoryReorderPreview(source);
    }

    private void CategoryReorder_Frame(object? sender, EventArgs e)
    {
        if (!_categoryReordering) return;
        if (_disposed || !IsLoaded || !IsVisible || _trayLeaving || Mouse.Captured != CategoryScroll ||
            Mouse.LeftButton != MouseButtonState.Pressed)
        { CancelCategoryReorder(); return; }
        long now = Stopwatch.GetTimestamp();
        double elapsed = Math.Clamp((now - _categoryReorderFrameTime) / (double)Stopwatch.Frequency, 0, 0.05);
        _categoryReorderFrameTime = now;
        var point = Mouse.GetPosition(CategoryScroll);
        UpdateCategoryReorder(point);
        if (_categoryReorderValid)
            CategoryScroll.ScrollToHorizontalOffset(Math.Clamp(CategoryScroll.HorizontalOffset +
                CategoryDropSpeed(point.X, CategoryScroll.ActualWidth) * 50 * elapsed, 0, CategoryScroll.ScrollableWidth));
    }

    private async Task CompleteCategoryReorderAsync(Point point)
    {
        // Re-evaluate the release position, including scroll since the last mouse move.
        UpdateCategoryReorder(point);
        string? id = (_categoryReorderSource?.DataContext as ClipboardCategoryViewModel)?.Name;
        string? beforeId = _categoryReorderBeforeId;
        bool commit = _categoryReordering && _categoryReorderValid && id != null;
        var ghostOrigin = new Point(Canvas.GetLeft(CategoryReorderGhost), Canvas.GetTop(CategoryReorderGhost));
        bool land = commit && _categoryReorderVisualsReady;
        if (commit) CancelCategoryReorder();
        else CancelCategoryReorderAnimated();
        if (commit)
        {
            Task save = CommitCategoryReorderAsync(id!, beforeId);
            if (land) BeginCategoryReorderLanding(id!, ghostOrigin);
            await save;
        }
    }

    private void CancelCategoryReorder()
    {
        bool wasDragging = _categoryReordering;
        _categoryReordering = false;
        CompositionTarget.Rendering -= CategoryReorder_Frame;
        ResetCategoryReorderVisuals();
        if (_categoryReorderSource is { } source)
        {
            source.Opacity = _categoryReorderSourceOpacity;
            ToolTipService.SetIsEnabled(source, _categoryReorderToolTipEnabled);
        }
        _categoryReorderSource = null;
        _categoryReorderValid = false;
        _categoryReorderBeforeId = null;
        _categoryPress = null;
        CategoryReorderOverlay.Visibility = Visibility.Collapsed;
        CategoryReorderMarker.Visibility = Visibility.Collapsed;
        CategoryReorderGhost.DataContext = null;
        if (wasDragging) CategoryPanel_LayoutUpdated(this, EventArgs.Empty);
        if (!wasDragging) return;
        CategoryScroll.ClearValue(CursorProperty);
        CategoryScroll.ClearValue(ForceCursorProperty);
        if (Mouse.Captured == CategoryScroll) Mouse.Capture(null);
    }

    private void CancelCategoryReorderAnimated()
    {
        string? id = (_categoryReorderSource?.DataContext as ClipboardCategoryViewModel)?.Name;
        bool returnToSource = _categoryReordering && _categoryReorderVisualsReady && id != null;
        var origin = new Point(Canvas.GetLeft(CategoryReorderGhost), Canvas.GetTop(CategoryReorderGhost));
        CancelCategoryReorder();
        if (returnToSource) BeginCategoryReorderLanding(id!, origin);
    }

    private async Task CommitCategoryReorderAsync(string id, string? beforeId)
    {
        if (_controller == null || _disposed) return;
        var order = _categoryItems.Select(category => category.Name).ToList();
        if (!ApplyCategoryMove(order, id, beforeId)) return;
        bool restoreFocus = CategoryPanel.IsKeyboardFocusWithin;
        _pendingCategoryWrites++;
        _optimisticCategoryOrder = order.ToArray();
        try
        {
            // Publish immediately; queued disk writes must not delay the drop or
            // steal focus from whatever the user does next. Refreshes retain the
            // latest preview until all serialized writes finish.
            SynchronizeLabels();
            CategoryPanel.UpdateLayout();
            var moved = _categoryItems.FirstOrDefault(category => category.Name == id);
            if (moved != null && CategoryButton(moved) is { } button && IsVisible && !_trayLeaving)
            {
                button.BringIntoView();
                if (restoreFocus) button.Focus();
                // BringIntoView and focus can queue scroll commands. Settle them
                // before the landing ghost measures its destination in the viewport.
                CategoryScroll.UpdateLayout();
            }
            await _controller.Store.ReorderCategoryAsync(id, beforeId);
        }
        finally
        {
            if (--_pendingCategoryWrites == 0) _optimisticCategoryOrder = null;
            if (!_disposed) RebuildCategories();
        }
    }

    private static bool ApplyCategoryMove(List<string> order, string id, string? beforeId)
    {
        int source = order.IndexOf(id);
        if (source < 0 || beforeId == id || beforeId != null && !order.Contains(beforeId)) return false;
        int destination = beforeId == null ? order.Count - 1 : order.IndexOf(beforeId);
        if (beforeId != null && destination > source) destination--;
        if (source == destination) return false;
        order.RemoveAt(source);
        order.Insert(destination, id);
        return true;
    }

    private async void CategoryReorder_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Left or Key.Right) ||
            sender is not Button { DataContext: ClipboardCategoryViewModel category }) return;
        e.Handled = true;
        if (_categoryReordering) return;
        int index = Array.FindIndex(_categoryItems, item => item.Name == category.Name);
        int destination = index + (key == Key.Left ? -1 : 1);
        if (index < 0 || destination < 0 || destination >= _categoryItems.Length) return;
        string? rightTargetId = destination + 1 < _categoryItems.Length ? _categoryItems[destination + 1].Name : null;
        string? beforeId = key == Key.Left ? _categoryItems[destination].Name : rightTargetId;

        CancelCategoryReorder();
        await SafeAsync(() => CommitCategoryReorderAsync(category.Name, beforeId));
    }
}
