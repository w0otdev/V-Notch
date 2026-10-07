using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private readonly System.Windows.Threading.DispatcherTimer _categoryDropTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
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
        if (width <= 0 || x < 0 || x > width) return 0;
        double edge = Math.Min(40, width / 2);
        return x < edge ? -12 * (1 - x / edge) : x > width - edge ? 12 * (1 - (width - x) / edge) : 0;
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
    private long _categoryFrameTime;
    private readonly TranslateTransform _categoryElasticTransform = new();

    private void StartCategoryScrollFrames()
    {
        if (_categoryScrollActive) return;
        _categoryScrollActive = true;
        _categoryFrameTime = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += Category_ScrollFrame;
    }

    private void SetCategoryOverscroll(double excess)
    {
        // A bounded resistance curve keeps repeated wheel input within 20 DIPs.
        _categoryElasticTransform.X = AnimationConfig.ReduceMotion ? 0
            : -20 * Math.Tanh(excess / 120);
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
        CategoryScroll.LostMouseCapture += (_, _) =>
        {
            if (_categoryDragging && Mouse.Captured != CategoryScroll) EndCategoryDrag();
        };
        CategoryScroll.IsVisibleChanged += (_, _) =>
        {
            if (!CategoryScroll.IsVisible) StopCategoryScrolling();
        };
    }

    private void Category_Wheel(object sender, MouseWheelEventArgs e)
    {
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
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Clamp((now - _categoryFrameTime) / (double)Stopwatch.Frequency, 0, 0.05);
        _categoryFrameTime = now;
        _categoryScrollTarget = Math.Clamp(_categoryScrollTarget, 0, CategoryScroll.ScrollableWidth);
        double current = CategoryScroll.HorizontalOffset;
        double remaining = _categoryScrollTarget - current;

        // Smooth critically-damped relaxation of any drag overscroll back to 0
        // (pure exponential decay: never oscillates or rebounds past zero)
        if (Math.Abs(_categoryElasticTransform.X) > 0.05)
        {
            _categoryElasticTransform.X *= Math.Exp(-24 * seconds);
        }
        else
        {
            _categoryElasticTransform.X = 0;
        }

        if ((Math.Abs(remaining) < 0.5 && Math.Abs(_categoryElasticTransform.X) < 0.1) || AnimationConfig.ReduceMotion)
        {
            CategoryScroll.ScrollToHorizontalOffset(_categoryScrollTarget);
            StopCategoryScrollFrames();
            return;
        }
        CategoryScroll.ScrollToHorizontalOffset(current + remaining * (1 - Math.Exp(-22 * seconds)));
    }

    private void Category_PointerDown(object sender, MouseButtonEventArgs e)
    {
        StopCategoryScrollFrames();
        _categoryPress = e.GetPosition(CategoryScroll);
        _categoryPressOffset = CategoryScroll.HorizontalOffset;
        // Let a normal click reach the button. Capture only after drag intent.
    }

    private void Category_PointerMove(object sender, MouseEventArgs e)
    {
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
    private long _cardsFrameTime;
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
        _cardsFrameTime = Stopwatch.GetTimestamp();
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

        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Clamp((now - _cardsFrameTime) / (double)Stopwatch.Frequency, 0, 0.05);
        _cardsFrameTime = now;

        _cardsScrollTarget = Math.Clamp(_cardsScrollTarget, 0, scroll.ScrollableWidth);
        // Layout may round the applied offset. Feeding it back into the easing
        // loses subpixel movement and can stall the tail at high refresh rates.
        double current = Math.Clamp(_cardsScrollPosition, 0, scroll.ScrollableWidth);
        double remaining = _cardsScrollTarget - current;

        if (Math.Abs(remaining) < 0.5 || AnimationConfig.ReduceMotion)
        {
            scroll.ScrollToHorizontalOffset(_cardsScrollTarget);
            StopCardsScrollFrames();
            return;
        }

        _cardsScrollPosition = current + remaining * (1 - Math.Exp(-22 * seconds));
        scroll.ScrollToHorizontalOffset(_cardsScrollPosition);
    }

    private void StopCategoryScrolling()
    {
        _categoryDropTimer.Stop();
        StopCategoryScrollFrames();
        StopCardsScrollFrames();
        EndCategoryDrag();
    }
}
