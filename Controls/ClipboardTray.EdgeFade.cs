using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private readonly Dictionary<FrameworkElement, (bool Left, bool Right, double Width)> _edgeFadeStates = [];
    private bool _searchFadeLeftActive;
    private bool _searchFadeRightActive;
    private LinearGradientBrush? _searchMask;
    private GradientStop? _searchMaskLeftStop;
    private GradientStop? _searchMaskRightStop;

    private void InitializeSearchEdgeFade()
    {
        SearchBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(SearchBox_ScrollChanged));
        SearchBox.SelectionChanged += (_, _) => UpdateSearchEdgeFade();
        SearchBox.SizeChanged += (_, _) => UpdateSearchEdgeFade();
        SearchBox.Loaded += (_, _) => UpdateSearchEdgeFade();
    }

    private void SearchBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateSearchEdgeFade(e.HorizontalOffset, Math.Max(0, e.ExtentWidth - e.ViewportWidth));
    }

    private void UpdateSearchEdgeFade()
    {
        var scroll = FindScrollViewer(SearchBox);
        if (scroll != null)
        {
            UpdateSearchEdgeFade(scroll.HorizontalOffset, scroll.ScrollableWidth);
        }
        else
        {
            UpdateSearchEdgeFade(0, 0);
        }
    }

    private void UpdateSearchEdgeFade(double offset, double scrollableWidth)
    {
        bool left = offset > 1.0;
        bool right = scrollableWidth > 1.0 && offset < scrollableWidth - 1.0;

        if (_searchFadeLeftActive != left)
        {
            _searchFadeLeftActive = left;
            AnimateSearchFade(SearchFadeLeft, left);
        }
        if (_searchFadeRightActive != right)
        {
            _searchFadeRightActive = right;
            AnimateSearchFade(SearchFadeRight, right);
        }

        UpdateSearchMask(left, right);
    }

    private void AnimateSearchFade(FrameworkElement? fadeElement, bool active)
    {
        if (fadeElement == null) return;
        double targetOpacity = active ? 1.0 : 0.0;
        if (AnimationConfig.ReduceMotion || !IsLoaded)
        {
            fadeElement.BeginAnimation(UIElement.OpacityProperty, null);
            fadeElement.Opacity = targetOpacity;
            return;
        }

        var anim = new DoubleAnimation(fadeElement.Opacity, targetOpacity, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = TrayEase,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        fadeElement.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void EnsureSearchMask()
    {
        if (_searchMask != null) return;
        _searchMaskLeftStop = new GradientStop(Colors.Black, 0.0);
        _searchMaskRightStop = new GradientStop(Colors.Black, 1.0);
        _searchMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        _searchMask.GradientStops.Add(_searchMaskLeftStop);
        _searchMask.GradientStops.Add(new GradientStop(Colors.Black, 0.08));
        _searchMask.GradientStops.Add(new GradientStop(Colors.Black, 0.92));
        _searchMask.GradientStops.Add(_searchMaskRightStop);
        SearchBox.OpacityMask = _searchMask;
    }

    private void UpdateSearchMask(bool left, bool right)
    {
        EnsureSearchMask();
        if (_searchMaskLeftStop == null || _searchMaskRightStop == null) return;

        if (SearchBox.ActualWidth > 0 && _searchMask != null)
        {
            double edge = Math.Min(0.12, 22.0 / SearchBox.ActualWidth);
            _searchMask.GradientStops[1].Offset = edge;
            _searchMask.GradientStops[2].Offset = 1.0 - edge;
        }

        AnimateMaskColor(_searchMaskLeftStop, left ? Colors.Transparent : Colors.Black);
        AnimateMaskColor(_searchMaskRightStop, right ? Colors.Transparent : Colors.Black);
    }

    private void AnimateMaskColor(GradientStop stop, Color target)
    {
        if (stop.Color == target) return;
        if (AnimationConfig.ReduceMotion || !IsLoaded)
        {
            stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.Color = target;
            return;
        }

        var anim = new ColorAnimation(stop.Color, target, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = TrayEase,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        stop.BeginAnimation(GradientStop.ColorProperty, anim);
    }

    private void TrayScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (sender is FrameworkElement element) UpdateEdgeFade(element);
        if (ReferenceEquals(sender, Cards)) QueueCardContentRefresh();
    }

    private void TrayScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element) UpdateEdgeFade(element);
        if (ReferenceEquals(sender, Cards)) QueueCardContentRefresh();
    }

    private void UpdateEdgeFade(FrameworkElement element)
    {
        var scroll = element as ScrollViewer ?? FindScrollViewer(element);
        if (scroll == null || element.ActualWidth <= 0) return;
        bool left = scroll.HorizontalOffset > 0.5;
        bool right = scroll.HorizontalOffset < scroll.ScrollableWidth - 0.5;
        var state = (left, right, element.ActualWidth);
        bool hadState = _edgeFadeStates.TryGetValue(element, out var previous);
        if (hadState && previous == state) return;
        _edgeFadeStates[element] = state;
        if (ReferenceEquals(element, Cards))
        {
            // Masking the entire scrolling list causes hardware-compositor
            // seams on hover. Paint only the two edge strips instead.
            element.OpacityMask = null;
            if (!hadState || previous.Left != left) AnimateSearchFade(CardsFadeLeft, left);
            if (!hadState || previous.Right != right) AnimateSearchFade(CardsFadeRight, right);
            return;
        }
        double edge = Math.Min(0.12, 22 / element.ActualWidth);
        if (element.OpacityMask is not LinearGradientBrush mask || mask.IsFrozen)
        {
            mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
            mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
            element.OpacityMask = mask;
            return;
        }

        mask.GradientStops[1].Offset = edge;
        mask.GradientStops[2].Offset = 1 - edge;
        // Keep the mask alive at the endpoints so each edge can fade out fully.
        // Only state changes start clocks; scroll frames must not restart them.
        if (!hadState || previous.Left != left)
            AnimateMaskColor(mask.GradientStops[0], left ? Colors.Transparent : Colors.Black);
        if (!hadState || previous.Right != right)
            AnimateMaskColor(mask.GradientStops[3], right ? Colors.Transparent : Colors.Black);
    }
}
