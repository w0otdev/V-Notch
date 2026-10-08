using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    // Shared duration scale: input, state changes, and entrance travel.
    private const int MotionFast = 120;
    private const int MotionStandard = 180;
    private const int MotionEntrance = 380;
    private const int MotionReflow = 240;
    private static readonly CubicEase TrayEase = CreateTrayEase();
    private static CubicEase CreateTrayEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private bool _animateResults = true;
    private int _motionVersion;
    private bool _trayLeaving;
    private int _entranceStaggerVersion;
    private readonly HashSet<ListBoxItem> _entranceAnimatedContainers = [];
    private readonly Dictionary<FrameworkElement, object> _dismissingLayers = [];
    private bool _trayDropBorderVisible = true;
    private int _trayDropBorderMotionVersion;

    private void InitializeComponentMotion()
    {
        foreach (var panel in new FrameworkElement[] { LockPanel, ApprovalPanel, DetailPanel, EmptyText, EmptyDropSilhouette, StatusPanel, DeleteConfirmationPanel })
            panel.IsVisibleChanged += (_, _) =>
            {
                if (panel.IsVisible && !_disposed && !_trayLeaving) AnimateLayer(panel, true, 0, true);
                if (panel == LockPanel && panel.IsVisible) QueuePasscodeFocus();
            };
    }

    private void QueuePasscodeFocus()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed || _trayLeaving || !IsLoaded || !IsVisible || _category != "Personal" ||
                _controller?.Store.IsPersonalUnlocked == true || !LockPanel.IsHitTestVisible ||
                !PasscodeBox.IsVisible || DetailPanel.IsVisible || ApprovalPanel.IsVisible) return;
            Window.GetWindow(this)?.Activate();
            PasscodeBox.Focus();
            Keyboard.Focus(PasscodeBox);
        }));
    }

    private bool _searchHasText;

    private void UpdateSearchMotion()
    {
        bool hasText = SearchBox.Text.Length != 0;
        bool changed = hasText != _searchHasText;
        _searchHasText = hasText;
        SearchHint.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        if (hasText) ShowTransientLayer(ClearSearchButton);
        else DismissTransientLayer(ClearSearchButton);
        if (!changed || !IsLoaded || !IsVisible || _trayLeaving) return;
        // Animate only entering/leaving a query, keeping the caret steady while typing.
        AnimateLayer(SearchTextSurface, true, 0, true, MotionStandard, 3);
        if (hasText) AnimateLayer(ClearSearchButton, true, 0, true, MotionStandard, 3);
    }

    private void UpdateEmptyMessage(bool visible, string message)
    {
        if (!visible)
        {
            DismissTransientLayer(EmptyText);
            return;
        }
        bool changed = EmptyText.Text != message;
        bool alreadyVisible = EmptyText.IsVisible;
        EmptyText.Text = message;
        ShowTransientLayer(EmptyText);
        // IsVisibleChanged handles first appearance; changing the query while
        // results stay empty needs its own transition.
        if (changed && alreadyVisible && !_trayLeaving && !_disposed)
            AnimateLayer(EmptyText, true, 0, true, MotionStandard, 4);
    }

    public void BeginEntrance()
    {
        bool reversing = _trayLeaving && IsVisible && Opacity > 0.01;
        _trayLeaving = false;
        ++_motionVersion;
        DeferEntranceWork();
        // The view entrance already owns the row fade; do not restart it when
        // the initial asynchronous search finishes.
        _animateResults = false;

        // Fluid, organic Apple waterfall fade:
        // Header components land first with subtle upward drift.
        AnimateLayer(SearchSurface, true, 0, !reversing, 300, 6);
        if (ToolbarSurface.Visibility == Visibility.Visible) AnimateLayer(ToolbarSurface, true, 20, !reversing, 300, 6);
        // Category pill track slides in right after the header.
        AnimateLayer(CategoryScroll, true, 40, !reversing, 320, 8);

        // Stagger individual cards for a premium cascading entrance instead of
        // fading the entire ListBox as a single block. Each realized card will
        // pick up a progressive delay in CardContainer_Loaded / StaggerEntranceCards.
        if (!reversing && !AnimationConfig.ReduceMotion)
        {
            _entranceStaggerVersion = _motionVersion;
            _entranceAnimatedContainers.Clear();
            // Make the container itself visible immediately so individual items
            // can control their own opacity independently.
            Cards.BeginAnimation(OpacityProperty, null);
            Cards.Opacity = 1;
            Cards.RenderTransform = Transform.Identity;
            StaggerEntranceCards();

            int version = _motionVersion;
            _ = SafeAsync(async () =>
            {
                await Task.Delay(500);
                if (_entranceStaggerVersion == version)
                {
                    _entranceStaggerVersion = 0;
                    _entranceAnimatedContainers.Clear();
                }
            });
        }
        else
        {
            _entranceStaggerVersion = 0;
            _entranceAnimatedContainers.Clear();
            AnimateLayer(Cards, true, 60, !reversing, 360, 10);
        }

        foreach (var panel in new FrameworkElement[] { LockPanel, ApprovalPanel, DetailPanel, EmptyText, EmptyDropSilhouette, StatusPanel, DeleteConfirmationPanel })
            if (panel.IsVisible) AnimateLayer(panel, true, 60, !reversing, 360, 10);
        if (LockPanel.IsVisible) QueuePasscodeFocus();
        if (_trayDropBorderVisible) AnimateTrayDropBorder(true, !reversing);
    }

    public void BeginExit()
    {
        CancelPendingPersonalDrop(); ResolveDeleteConfirmation(false, immediately: true); ClearStatus(immediately: true);
        CancelCardPositionAnimation();
        _trayLeaving = true;
        StopEntranceWork();
        CancelThumbnailLoads();
        AnimateTrayDropBorder(false);
        _entranceStaggerVersion = 0;
        _entranceAnimatedContainers.Clear();
        ++_motionVersion;
        AnimateLayer(Cards, false, 0);
        AnimateLayer(CategoryScroll, false, 20);
        AnimateLayer(SearchSurface, false, 40);
        if (ToolbarSurface.Visibility == Visibility.Visible) AnimateLayer(ToolbarSurface, false, 40);
        foreach (var panel in new FrameworkElement[] { LockPanel, ApprovalPanel, DetailPanel, EmptyText, EmptyDropSilhouette })
            if (panel.IsVisible) AnimateLayer(panel, false, 0);
    }

    private void ShowTransientLayer(FrameworkElement element)
    {
        bool reversing = _dismissingLayers.Remove(element);
        element.IsHitTestVisible = true;
        if (element == LockPanel) element.IsEnabled = true;
        if (element.Visibility != Visibility.Visible) element.Visibility = Visibility.Visible;
        else if (reversing) AnimateLayer(element, true, 0);
        if (element == LockPanel && reversing) QueuePasscodeFocus();
    }

    private void DismissTransientLayer(FrameworkElement element, Action? completed = null, bool immediately = false)
    {
        if (immediately || _disposed || !element.IsVisible || _trayLeaving)
        {
            _dismissingLayers.Remove(element);
            element.Visibility = Visibility.Collapsed;
            ResetCardMotion(element);
            completed?.Invoke();
            return;
        }
        if (_dismissingLayers.ContainsKey(element)) return;
        var dismissal = new object();
        _dismissingLayers[element] = dismissal;
        element.IsHitTestVisible = false;
        if (element == LockPanel) element.IsEnabled = false;
        AnimateLayer(element, false, 0, completed: () =>
        {
            // A newer message or confirmation may have reversed this fade.
            if (!_dismissingLayers.TryGetValue(element, out var current) || !ReferenceEquals(current, dismissal)) return;
            _dismissingLayers.Remove(element);
            element.Visibility = Visibility.Collapsed;
            ResetCardMotion(element);
            completed?.Invoke();
        });
    }

    private static void AnimateLayer(FrameworkElement element, bool entering, int delay, bool fresh = false, int customDuration = 0, double driftY = 6, Action? completed = null)
    {
        // PIN entry responds immediately; exits retain the panel until the fade finishes.
        bool personalLock = element.Name == "LockPanel";
        if (personalLock) { delay = 0; customDuration = entering ? 380 : 280; driftY = 6; }
        var rendered = element.RenderTransform?.Value ?? Matrix.Identity;
        double opacity = fresh ? 0 : element.Opacity;
        double x = fresh ? 0 : rendered.OffsetX;
        double y = fresh ? driftY : rendered.OffsetY;
        double targetOpacity = entering ? 1 : 0;
        double targetY = entering || AnimationConfig.ReduceMotion ? 0 : driftY;
        element.BeginAnimation(OpacityProperty, null);
        // Base values always describe the settled layout. Detaching or replacing
        // any clock must never restore a stale entrance offset.
        element.Opacity = targetOpacity;
        var translate = new TranslateTransform(0, targetY);
        element.RenderTransform = translate;
        int duration = AnimationConfig.ReduceMotion ? 80 : customDuration > 0 ? customDuration : (entering ? MotionEntrance : MotionStandard);
        var fade = CreateLayerAnimation(opacity, targetOpacity, AnimationConfig.ReduceMotion ? 0 : delay, duration);
        if (personalLock)
            foreach (var frame in fade.KeyFrames.OfType<EasingDoubleKeyFrame>()) frame.EasingFunction = TrayMotion.EaseOut;
        if (completed != null) fade.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        if (AnimationConfig.ReduceMotion)
        {
            return;
        }
        if (x != 0)
            translate.BeginAnimation(TranslateTransform.XProperty, CreateLayerAnimation(x, 0, delay, duration), HandoffBehavior.SnapshotAndReplace);
        if (y != targetY)
            translate.BeginAnimation(TranslateTransform.YProperty, CreateLayerAnimation(y, targetY, delay, duration), HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimationUsingKeyFrames CreateLayerAnimation(double from, double to, int delay, int duration)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration)),
            TrayEase));
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        return animation;
    }

    private static void SetCategoryBackground(Button button, Brush target)
    {
        if (target is not SolidColorBrush destination) { button.Background = target; return; }
        Color current = (button.Background as SolidColorBrush)?.Color ?? destination.Color;
        var brush = new SolidColorBrush(destination.Color);
        button.Background = brush;
        if (AnimationConfig.ReduceMotion || current == destination.Color) return;
        var animation = new ColorAnimation(current, destination.Color, TimeSpan.FromMilliseconds(MotionStandard))
        {
            EasingFunction = TrayEase,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private static void AnimateValue(Animatable target, DependencyProperty property, double value, int milliseconds)
    {
        double current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        if (AnimationConfig.ReduceMotion || Math.Abs(current - value) < 0.001) return;
        var animation = new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = TrayEase,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Button_PointerDown(object sender, MouseButtonEventArgs e) => SetButtonScale(sender, 0.96, MotionFast);
    private void Button_PointerUp(object sender, MouseButtonEventArgs e) => SetButtonScale(sender, 1, MotionStandard);
    private void Button_PointerLeave(object sender, MouseEventArgs e) => SetButtonScale(sender, 1, MotionStandard);
    private void Button_KeyDown(object sender, KeyEventArgs e)
    {
        if (!e.IsRepeat && e.Key is Key.Space or Key.Enter) SetButtonScale(sender, 0.96, MotionFast);
    }
    private void Button_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) SetButtonScale(sender, 1, MotionStandard);
    }
    private void Button_Reset(object sender, RoutedEventArgs e) => SetButtonScale(sender, 1, MotionStandard);
    private void GroupChoice_Motion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { IsLoaded: true } element) return;
        AnimateCardPress(element, false);
        if (!AnimationConfig.ReduceMotion && element.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = scale.ScaleY = 0.96;
            AnimateValue(scale, ScaleTransform.ScaleXProperty, 1, MotionStandard);
            AnimateValue(scale, ScaleTransform.ScaleYProperty, 1, MotionStandard);
        }
    }
    private static void SetButtonScale(object sender, double value, int duration)
    {
        if (sender is not Button button) return;
        if (AnimationConfig.ReduceMotion) value = 1;
        if (button.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            button.RenderTransform = scale;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        AnimateValue(scale, ScaleTransform.ScaleXProperty, value, duration);
        AnimateValue(scale, ScaleTransform.ScaleYProperty, value, duration);
    }

    private int _resultsDirection = 1;
    private readonly HashSet<Guid> _arrivingCards = [];
    private Dictionary<Guid, double>? _pendingCardPositions;
    private Dictionary<Guid, (double X, Transform Motion)>? _capturedCardLayouts;
    private Dictionary<Guid, (double X, Transform Motion)>? _pendingCardLayouts;

    // Recycled containers can be detached from Cards or from any
    // PresentationSource; TranslatePoint throws in that state.
    private bool TryGetPositionInCards(UIElement element, out Point point)
    {
        point = default;
        if (!Cards.IsAncestorOf(element) || PresentationSource.FromVisual(element) == null) return false;
        try { point = element.TranslatePoint(new Point(), Cards); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private Dictionary<Guid, double> CaptureCardPositions()
    {
        var positions = new Dictionary<Guid, double>();
        _capturedCardLayouts = null;
        if (_animateResults || AnimationConfig.ReduceMotion || !Cards.IsLoaded) return positions;
        _capturedCardLayouts = [];
        double scrollOffset = FindScrollViewer(Cards)?.HorizontalOffset ?? 0;
        foreach (var item in _selectionContainers)
            if (item.IsVisible && item.DataContext is VNotch.ViewModels.ClipboardCardViewModel card &&
                TryGetPositionInCards(item, out var origin))
            {
                positions[card.Entry.Id] = origin.X + scrollOffset;
                _capturedCardLayouts[card.Entry.Id] =
                    (positions[card.Entry.Id] - item.RenderTransform.Value.OffsetX, item.RenderTransform);
            }
        return positions;
    }

    private void AnimateCardPositions(Dictionary<Guid, double> positions)
    {
        var layouts = _capturedCardLayouts;
        CancelCardPositionAnimation();
        if (positions.Count == 0 || _disposed || _trayLeaving || !IsLoaded) return;
        _pendingCardPositions = positions;
        _pendingCardLayouts = layouts;
        Cards.LayoutUpdated += ApplyCardPositionsAfterLayout;
    }

    private void ApplyCardPositionsAfterLayout(object? sender, EventArgs e)
    {
        if (!Cards.IsMeasureValid || !Cards.IsArrangeValid) return;
        // The ListBox can be valid while its virtualizing items host still has
        // the old slot positions. Wait for that host before measuring the move.
        foreach (var item in _selectionContainers)
            if (item.IsVisible && VisualTreeHelper.GetParent(item) is UIElement host &&
                (!host.IsMeasureValid || !host.IsArrangeValid)) return;
        var positions = _pendingCardPositions;
        var layouts = _pendingCardLayouts;
        CancelCardPositionAnimation();
        if (positions == null || _disposed || _trayLeaving || !IsVisible || AnimationConfig.ReduceMotion) return;
        double scrollOffset = FindScrollViewer(Cards)?.HorizontalOffset ?? 0;
        foreach (var item in _selectionContainers)
        {
            if (!item.IsVisible || item.DataContext is not VNotch.ViewModels.ClipboardCardViewModel card ||
                !positions.TryGetValue(card.Entry.Id, out double previous) ||
                !TryGetPositionInCards(item, out var origin)) continue;
            var rendered = item.RenderTransform.Value;
            double layoutX = origin.X - rendered.OffsetX + scrollOffset;
            double offset = previous - layoutX;
            // Metadata-only refreshes must not restart an in-flight slide.
            if (layouts != null && layouts.TryGetValue(card.Entry.Id, out var previousLayout) &&
                Math.Abs(layoutX - previousLayout.X) < 0.5 && ReferenceEquals(item.RenderTransform, previousLayout.Motion)) continue;
            var translate = new TranslateTransform(offset, rendered.OffsetY);
            item.RenderTransform = translate;
            AnimateCardValue(translate, TranslateTransform.XProperty, offset, 0, MotionReflow);
            AnimateCardValue(translate, TranslateTransform.YProperty, rendered.OffsetY, 0, MotionReflow);
        }
    }

    private void CancelCardPositionAnimation()
    {
        Cards.LayoutUpdated -= ApplyCardPositionsAfterLayout;
        _pendingCardPositions = null;
        _pendingCardLayouts = null;
        _capturedCardLayouts = null;
    }

    private async Task AnimateCollectionChangesAsync(IReadOnlyList<VNotch.Models.ClipboardEntry> entries, CancellationToken cancellation)
    {
        if (_animateResults || _trayLeaving || AnimationConfig.ReduceMotion) return;
        var desired = entries.Select(entry => entry.Id).ToHashSet();
        var existing = _cards.Select(card => card.Entry.Id).ToHashSet();
        // Large replacements and later viewport arrivals already have a content
        // entrance. Keep the extra insertion animation for small live updates.
        var arriving = entries.Where(entry => !existing.Contains(entry.Id)).Take(33).ToArray();
        if (arriving.Length <= 32)
            foreach (var entry in arriving) _arrivingCards.Add(entry.Id);
        var removing = new List<(ListBoxItem Item, Guid EntryId)>();
        foreach (var item in _selectionContainers)
        {
            if (!item.IsVisible || item.DataContext is not VNotch.ViewModels.ClipboardCardViewModel card || desired.Contains(card.Entry.Id)) continue;
            AnimateCardLifecycle(item, entering: false);
            removing.Add((item, card.Entry.Id));
        }
        if (removing.Count > 0)
        {
            try
            {
                // Survivors move once, after reconciliation, from their last
                // rendered positions. Moving them before layout creates a second
                // slide when the actual item slots change.
                await Task.Delay(160, cancellation);
            }
            catch (OperationCanceledException)
            {
                foreach (var (item, entryId) in removing)
                    if (item.DataContext is VNotch.ViewModels.ClipboardCardViewModel card && card.Entry.Id == entryId)
                    {
                        ResetCardMotion(item);
                    }
                throw;
            }
        }
    }

    private static void AnimateCardLifecycle(FrameworkElement element, bool entering, bool fresh = false)
    {
        var rendered = element.RenderTransform.Value;
        double opacity = fresh ? 0 : element.Opacity;
        double targetOpacity = entering ? 1 : 0;
        int duration = AnimationConfig.ReduceMotion ? 80 : entering ? 260 : 160;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = targetOpacity;
        var fade = new DoubleAnimation(opacity, targetOpacity, TimeSpan.FromMilliseconds(duration))
        { EasingFunction = TrayMotion.EaseOut, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, fade);
        if (AnimationConfig.ReduceMotion)
        {
            element.RenderTransform = Transform.Identity;
            return;
        }

        double targetScale = entering ? 1 : 0.96;
        double targetY = entering ? 0 : 6;
        var scale = new ScaleTransform(targetScale, targetScale);
        var translate = new TranslateTransform(0, targetY);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, translate } };
        AnimateCardValue(scale, ScaleTransform.ScaleXProperty, fresh ? 0.96 : rendered.M11, targetScale, duration);
        AnimateCardValue(scale, ScaleTransform.ScaleYProperty, fresh ? 0.96 : rendered.M22, targetScale, duration);
        AnimateCardValue(translate, TranslateTransform.XProperty, fresh ? 0 : rendered.OffsetX, 0, duration);
        AnimateCardValue(translate, TranslateTransform.YProperty, fresh ? 8 : rendered.OffsetY, targetY, duration);
    }

    private static void AnimateCardValue(Animatable target, DependencyProperty property, double from, double to, int duration)
    {
        target.SetValue(property, to);
        if (AnimationConfig.ReduceMotion || from == to)
        {
            target.BeginAnimation(property, null);
            return;
        }
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration))
        { EasingFunction = TrayMotion.EaseOut, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StaggerEntranceCards()
    {
        // Animate cards that are already realized in the virtualizing panel.
        // Cards loaded later during this entrance will be caught by CardContainer_Loaded.
        int visualIndex = 0;
        foreach (var item in _selectionContainers.OrderBy(item => Cards.ItemContainerGenerator.IndexFromContainer(item)))
        {
            if (!item.IsLoaded) continue;
            if (_entranceAnimatedContainers.Add(item))
            {
                ResetCardMotion(item);
                int delay = Math.Min(visualIndex, 7) * 35 + 60;
                AnimateLayer(item, true, delay, true, 360, 10);
                visualIndex++;
            }
        }
    }

    private void CardContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        RegisterSelectionContainer(item);
        ResetCardMotion(item);
        if (_entranceStaggerVersion == _motionVersion && _entranceStaggerVersion > 0)
        {
            // Card appeared during an active view entrance — apply staggered cascade.
            if (_entranceAnimatedContainers.Add(item))
            {
                int visualIndex = Math.Max(0, _entranceAnimatedContainers.Count - 1);
                int delay = Math.Min(visualIndex, 7) * 35 + 60;
                AnimateLayer(item, true, delay, true, 360, 10);
            }
        }
        else if (item.DataContext is VNotch.ViewModels.ClipboardCardViewModel card && _arrivingCards.Remove(card.Entry.Id))
            AnimateCardLifecycle(item, entering: true, fresh: true);
    }

    private void CardContainer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            UnregisterSelectionContainer(item);
            _entranceAnimatedContainers.Remove(item);
        }
        if (sender is FrameworkElement element) ResetCardMotion(element);
    }

    private async Task PrepareResultsAsync(CancellationToken cancellation)
    {
        if (!_animateResults || _trayLeaving || !Cards.IsVisible || AnimationConfig.ReduceMotion) return;
        double current = Cards.Opacity;
        Cards.BeginAnimation(OpacityProperty, null);
        Cards.Opacity = 0;
        var fade = new DoubleAnimation(current, 0, TimeSpan.FromMilliseconds(MotionFast))
        { FillBehavior = FillBehavior.Stop, EasingFunction = TrayEase };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        Cards.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        await Task.Delay(MotionFast, cancellation);
    }

    private void RevealResults()
    {
        if (!_animateResults || _trayLeaving) return;
        _animateResults = false;
        // Swap while hidden, then reveal the whole row. Never show new containers
        // for a frame before resetting their opacity in a deferred callback.
        Cards.BeginAnimation(OpacityProperty, null);
        Cards.Opacity = 1;
        var translate = new TranslateTransform();
        Cards.RenderTransform = translate;
        if (AnimationConfig.ReduceMotion) return;
        var duration = TimeSpan.FromMilliseconds(MotionEntrance);
        var ease = TrayEase;
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation(14 * _resultsDirection, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
        Cards.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetCardMotion(FrameworkElement element)
    {
        element.RenderTransform = Transform.Identity;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;
    }

    private static void AnimateCardPress(FrameworkElement element, bool pressed)
    {
        if (AnimationConfig.ReduceMotion) pressed = false;
        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        AnimateValue(scale, ScaleTransform.ScaleXProperty, pressed ? 0.95 : 1, pressed ? MotionFast : MotionStandard);
        AnimateValue(scale, ScaleTransform.ScaleYProperty, pressed ? 0.95 : 1, pressed ? MotionFast : MotionStandard);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element) AnimateCardPress(element, false);
    }

    private void SetTrayDropBorderVisible(bool visible)
    {
        if (_trayDropBorderVisible == visible) return;
        _trayDropBorderVisible = visible;
        if (!IsLoaded || !IsVisible || _trayLeaving || _disposed)
        {
            ++_trayDropBorderMotionVersion;
            TrayDropBorder.BeginAnimation(OpacityProperty, null);
            TrayDropBorder.Opacity = visible ? 1 : 0;
            TrayDropBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        AnimateTrayDropBorder(visible, collapseOnExit: !visible);
    }

    private void AnimateTrayDropBorder(bool entering, bool fresh = false, bool collapseOnExit = false)
    {
        if (TrayDropBorder == null || _disposed) return;
        int version = ++_trayDropBorderMotionVersion;
        // Capture the rendered value before removing the clock so rapid reversals
        // continue from the current fade rather than jumping to its old target.
        double fromOpacity = fresh || TrayDropBorder.Visibility != Visibility.Visible ? 0 : TrayDropBorder.Opacity;
        double targetOpacity = entering ? 1 : 0;
        TrayDropBorder.BeginAnimation(OpacityProperty, null);
        TrayDropBorder.Opacity = targetOpacity;
        if (entering) TrayDropBorder.Visibility = Visibility.Visible;

        if (fromOpacity == targetOpacity)
        {
            if (collapseOnExit) TrayDropBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(fromOpacity, targetOpacity,
            TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : MotionStandard))
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        if (collapseOnExit)
            fade.Completed += (_, _) =>
            {
                if (!_disposed && version == _trayDropBorderMotionVersion && !_trayDropBorderVisible)
                    TrayDropBorder.Visibility = Visibility.Collapsed;
            };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        TrayDropBorder.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
}
