using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    [LibraryImport("VNotch.Native")]
    private static partial int vn_clipboard_scroll_category_drop_speed(
        double x, double width, out double outSpeed);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_clipboard_scroll_category_overscroll(
        double excess, byte reduceMotion, out double outElasticX);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_clipboard_scroll_step_category_frame(
        double scrollTarget,
        double scrollableWidth,
        double currentOffset,
        double currentElasticX,
        double seconds,
        byte reduceMotion,
        out double outClampedTarget,
        out double outNextOffset,
        out double outNextElasticX,
        out byte outStop);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_clipboard_scroll_step_cards_frame(
        double scrollTarget,
        double scrollableWidth,
        double currentPos,
        double seconds,
        byte reduceMotion,
        out double outClampedTarget,
        out double outNextPos,
        out byte outStop);

    private readonly UiScheduledTimer _categoryDropTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private double _categoryDropSpeed;
    private long _categoryDropLastSeen;

    private void CategoryDrop_Over(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(CategoryScroll);
        bool supported = VNotch.Services.Clipboard.ClipboardDropReader.Supports(e.Data) ||
            _controller?.IsDragging == true && e.Data.GetDataPresent(VNotch.Controllers.ClipboardHistoryController.EntryDragFormat);
        _categoryDropSpeed = supported && point.Y >= 0 && point.Y <= CategoryScroll.ActualHeight
            ? CategoryDropSpeed(point.X, CategoryScroll.ActualWidth) : 0;
        _categoryDropLastSeen = Environment.TickCount64;
        if (_categoryDropSpeed == 0) _categoryDropTimer.Stop();
        else { StopCategoryScrollFrames(); _categoryDropTimer.Start(); }
    }

    internal static double CategoryDropSpeed(double x, double width)
    {
        _ = vn_clipboard_scroll_category_drop_speed(x, width, out double speed);
        return speed;
    }

    private void CategoryDrop_Tick(object? sender, EventArgs e)
    {
        if (_disposed || !IsLoaded || !IsVisible || Environment.TickCount64 - _categoryDropLastSeen > 250)
        { _categoryDropTimer.Stop(); return; }
        CategoryScroll.ScrollToHorizontalOffset(Math.Clamp(CategoryScroll.HorizontalOffset + _categoryDropSpeed, 0, CategoryScroll.ScrollableWidth));
    }

    private Point? _categoryPress;
    private double _categoryPressOffset;
    private double _categoryScrollTarget;
    private bool _categoryDragging;
    private bool _categoryScrollActive;
    private AnimationFrameClock _categoryFrameClock;
    private readonly TranslateTransform _categoryElasticTransform = new();

    private void StartCategoryScrollFrames()
    {
        if (_categoryScrollActive) return;
        _categoryScrollActive = true;
        _categoryFrameClock.Reset(Stopwatch.GetTimestamp());
        CompositionTarget.Rendering += Category_ScrollFrame;
    }

    private void SetCategoryOverscroll(double excess)
    {
        // A bounded resistance curve keeps repeated wheel input within 20 DIPs.
        _ = vn_clipboard_scroll_category_overscroll(
            excess,
            AnimationConfig.ReduceMotion ? (byte)1 : (byte)0,
            out double elasticX);
        _categoryElasticTransform.X = elasticX;
    }

    private void InitializeCategoryScrolling()
    {
        CategoryPanel.RenderTransform = _categoryElasticTransform;
        CategoryScroll.AllowDrop = true;
        CategoryScroll.AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(CategoryDrop_Over), true);
        CategoryScroll.AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler((_, _) => _categoryDropTimer.Stop()), true);
        CategoryScroll.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, e) =>
        {
            var p = e.GetPosition(CategoryScroll);
            if (p.X <= 0 || p.X >= CategoryScroll.ActualWidth || p.Y <= 0 || p.Y >= CategoryScroll.ActualHeight) _categoryDropTimer.Stop();
        }), true);
        _categoryDropTimer.Tick += CategoryDrop_Tick;
        CategoryScroll.PreviewMouseLeftButtonDown += Category_PointerDown;
        CategoryScroll.PreviewMouseMove += Category_PointerMove;
        CategoryScroll.PreviewMouseLeftButtonUp += Category_PointerUp;
        CategoryScroll.LostMouseCapture += (_, e) =>
        {
            if (e.OriginalSource == CategoryScroll && _categoryReordering && Mouse.Captured != CategoryScroll)
                CancelCategoryReorder();
            if (_categoryDragging && Mouse.Captured != CategoryScroll) EndCategoryDrag();
        };
        CategoryScroll.IsVisibleChanged += (_, _) =>
        {
            if (!CategoryScroll.IsVisible) StopCategoryScrolling();
        };
    }

    private void Category_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_categoryReordering) { e.Handled = true; return; }
        e.Handled = true;
        if (_disposed || !IsLoaded || !IsVisible || _categoryPress != null) return;
        if (!_categoryScrollActive) _categoryScrollTarget = CategoryScroll.HorizontalOffset;
        double requested = _categoryScrollTarget - e.Delta;
        double newTarget = Math.Clamp(requested, 0, CategoryScroll.ScrollableWidth);

        // If already at boundary and continuing to scroll past it, do not jerk or restart frames
        if (!_categoryScrollActive && Math.Abs(newTarget - CategoryScroll.HorizontalOffset) < 0.5 &&
            Math.Abs(_categoryElasticTransform.X) < 0.1)
        {
            return;
        }

        _categoryScrollTarget = newTarget;

        if (AnimationConfig.ReduceMotion)
        {
            CategoryScroll.ScrollToHorizontalOffset(_categoryScrollTarget);
            StopCategoryScrollFrames();
            return;
        }
        StartCategoryScrollFrames();
    }

    private void Category_ScrollFrame(object? sender, EventArgs e)
    {
        if (_disposed || !IsLoaded || !IsVisible) { StopCategoryScrolling(); return; }
        double seconds;
        bool advance = e is RenderingEventArgs frame
            ? _categoryFrameClock.TryAdvance(frame.RenderingTime, AnimationConfig.TargetFps, out seconds)
            : _categoryFrameClock.TryAdvance(Stopwatch.GetTimestamp(), AnimationConfig.TargetFps, out seconds);
        if (!advance) return;

        // Smooth critically-damped relaxation of any drag overscroll back to 0
        // (pure exponential decay: never oscillates or rebounds past zero)
        _ = vn_clipboard_scroll_step_category_frame(
            _categoryScrollTarget,
            CategoryScroll.ScrollableWidth,
            CategoryScroll.HorizontalOffset,
            _categoryElasticTransform.X,
            seconds,
            AnimationConfig.ReduceMotion ? (byte)1 : (byte)0,
            out _categoryScrollTarget,
            out double nextOffset,
            out double nextElasticX,
            out byte stop);
        _categoryElasticTransform.X = nextElasticX;

        if (stop != 0)
        {
            CategoryScroll.ScrollToHorizontalOffset(_categoryScrollTarget);
            StopCategoryScrollFrames();
            return;
        }
        CategoryScroll.ScrollToHorizontalOffset(nextOffset);
    }

    private void Category_PointerDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _categoryReordering) return;
        StopCategoryScrollFrames();
        _categoryPress = e.GetPosition(CategoryScroll);
        _categoryPressOffset = CategoryScroll.HorizontalOffset;
        if (ItemsControl.ContainerFromElement(CategoryPanel, e.OriginalSource as DependencyObject) is ContentPresenter presenter &&
            VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is Button button)
            CategoryReorder_Press(button, _categoryPress.Value);
        // Let a normal click reach the button. Capture only after drag intent.
    }

    private void Category_PointerMove(object sender, MouseEventArgs e)
    {
        if (_categoryReorderSource != null)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { CancelCategoryReorder(); return; }
            var point = e.GetPosition(CategoryScroll);
            if (_categoryReordering || TryBeginCategoryReorder(point))
            {
                UpdateCategoryReorder(point);
                e.Handled = true;
            }
            return;
        }
        if (_categoryPress is not Point start) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndCategoryDrag(); return; }
        double distance = e.GetPosition(CategoryScroll).X - start.X;
        if (!_categoryDragging)
        {
            if (Math.Abs(distance) < SystemParameters.MinimumHorizontalDragDistance) return;
            _categoryDragging = true;
            foreach (var category in _categoryItems)
                if (CategoryPanel.ItemContainerGenerator.ContainerFromItem(category) is ContentPresenter presenter &&
                    VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is Button button)
                    SetButtonScale(button, 1, 120);
            Mouse.Capture(CategoryScroll, CaptureMode.Element);
            CategoryScroll.Cursor = Cursors.ScrollWE;
        }
        double requested = _categoryPressOffset - distance;
        double offset = Math.Clamp(requested, 0, CategoryScroll.ScrollableWidth);
        CategoryScroll.ScrollToHorizontalOffset(offset);
        SetCategoryOverscroll(requested - offset);
        e.Handled = true;
    }

    private void Category_PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_categoryReorderSource != null)
        {
            if (_categoryReordering)
            {
                e.Handled = true;
                _ = SafeAsync(() => CompleteCategoryReorderAsync(e.GetPosition(CategoryScroll)));
            }
            else CancelCategoryReorder();
            EndCategoryDrag();
            return;
        }
        bool dragged = _categoryDragging;
        EndCategoryDrag();
        if (dragged) e.Handled = true;
    }

    private void EndCategoryDrag()
    {
        _categoryPress = null;
        _categoryDragging = false;
        CategoryScroll.ClearValue(CursorProperty);
        if (Mouse.Captured == CategoryScroll) Mouse.Capture(null);
        if (!_disposed && IsLoaded && IsVisible && !AnimationConfig.ReduceMotion
            && Math.Abs(_categoryElasticTransform.X) > 0.1)
        {
            _categoryScrollTarget = CategoryScroll.HorizontalOffset;
            StartCategoryScrollFrames();
        }
    }

    private void StopCategoryScrollFrames()
    {
        _categoryElasticTransform.X = 0;
        if (!_categoryScrollActive) return;
        CompositionTarget.Rendering -= Category_ScrollFrame;
        _categoryScrollActive = false;
    }

    private double _cardsScrollTarget;
    private double _cardsScrollPosition;
    private bool _cardsScrollActive;
    private AnimationFrameClock _cardsFrameClock;
    private ScrollViewer? _cardsFrameScroll;

    private void Cards_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_selectionStart != null) return;
        if (_disposed || !IsLoaded || !IsVisible) return;
        var scroll = FindScrollViewer(Cards);
        if (scroll == null) return;

        if (!_cardsScrollActive)
        {
            _cardsScrollTarget = scroll.HorizontalOffset;
            _cardsScrollPosition = scroll.HorizontalOffset;
        }

        // Multiply delta slightly for responsive, silky-smooth horizontal tracking
        double delta = e.Delta * 1.25;
        double newTarget = Math.Clamp(_cardsScrollTarget - delta, 0, scroll.ScrollableWidth);

        // If already at boundary and continuing to scroll further into it, do not jerk or restart frames
        if (!_cardsScrollActive && Math.Abs(newTarget - scroll.HorizontalOffset) < 0.5)
        {
            return;
        }

        _cardsScrollTarget = newTarget;

        if (AnimationConfig.ReduceMotion)
        {
            scroll.ScrollToHorizontalOffset(_cardsScrollTarget);
            StopCardsScrollFrames();
            return;
        }

        StartCardsScrollFrames();
    }

    private void StartCardsScrollFrames()
    {
        if (_cardsScrollActive) return;
        _cardsScrollActive = true;
        _cardsFrameScroll = FindScrollViewer(Cards);
        _cardsFrameClock.Reset(Stopwatch.GetTimestamp());
        CompositionTarget.Rendering += Cards_ScrollFrame;
    }

    private void StopCardsScrollFrames()
    {
        if (!_cardsScrollActive) return;
        CompositionTarget.Rendering -= Cards_ScrollFrame;
        _cardsScrollActive = false;
        _cardsFrameScroll = null;
    }

    private void Cards_ScrollFrame(object? sender, EventArgs e)
    {
        if (_disposed || !IsLoaded || !IsVisible) { StopCardsScrollFrames(); return; }
        var scroll = _cardsFrameScroll;
        if (scroll == null) { StopCardsScrollFrames(); return; }

        double seconds;
        bool advance = e is RenderingEventArgs frame
            ? _cardsFrameClock.TryAdvance(frame.RenderingTime, AnimationConfig.TargetFps, out seconds)
            : _cardsFrameClock.TryAdvance(Stopwatch.GetTimestamp(), AnimationConfig.TargetFps, out seconds);
        if (!advance) return;

        // Layout may round the applied offset. Feeding it back into the easing
        // loses subpixel movement and can stall the tail at high refresh rates.
        _ = vn_clipboard_scroll_step_cards_frame(
            _cardsScrollTarget,
            scroll.ScrollableWidth,
            _cardsScrollPosition,
            seconds,
            AnimationConfig.ReduceMotion ? (byte)1 : (byte)0,
            out _cardsScrollTarget,
            out double nextPos,
            out byte stop);

        if (stop != 0)
        {
            scroll.ScrollToHorizontalOffset(_cardsScrollTarget);
            StopCardsScrollFrames();
            return;
        }

        _cardsScrollPosition = nextPos;
        scroll.ScrollToHorizontalOffset(_cardsScrollPosition);
    }

    private void StopCategoryScrolling()
    {
        CancelCategoryReorder();
        _categoryDropTimer.Stop();
        StopCategoryScrollFrames();
        StopCardsScrollFrames();
        EndCategoryDrag();
    }
}
