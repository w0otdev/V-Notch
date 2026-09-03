using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Models;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private FrameworkElement? _navDragItem;
    private Point _navDragStartPoint;
    private bool _isNavDragging = false;
    private bool _hasCapturedNavMouse = false;
    private DateTime _navMouseDownTime = DateTime.MinValue;
    private const double NavTabSlotWidth = 26.0; // 22 width + 4 margin
    private static readonly TimeSpan HoldToDragThreshold = TimeSpan.FromMilliseconds(160);

    private bool _isEndingNavDrag = false;
    private int _dragInitialSlot = -1;
    private int _dragTargetSlot = -1;
    private readonly Dictionary<FrameworkElement, double> _neighborTargetOffsets = new();

    #region Hold-to-Drag Reordering Handlers

    private void NavIcon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            _navDragItem = fe;
            _navDragStartPoint = e.GetPosition(NavTabsStackPanel);
            _navMouseDownTime = DateTime.UtcNow;
            _isNavDragging = false;
            _hasCapturedNavMouse = false;
            _dragInitialSlot = -1;
            _dragTargetSlot = -1;
            _neighborTargetOffsets.Clear();
            e.Handled = true;
        }
    }

    private void NavIcon_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_navDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;

        Point current = e.GetPosition(NavTabsStackPanel);
        double deltaX = current.X - _navDragStartPoint.X;

        if (!_isNavDragging)
        {
            bool movedPastThreshold = Math.Abs(deltaX) > 4;
            bool heldPastThreshold = (DateTime.UtcNow - _navMouseDownTime) >= HoldToDragThreshold && Math.Abs(deltaX) > 2;

            if (movedPastThreshold || heldPastThreshold)
            {
                _isNavDragging = true;
                _hasCapturedNavMouse = _navDragItem.CaptureMouse();
                Panel.SetZIndex(_navDragItem, 1000);
                AnimateNavDragLift(_navDragItem, true);
                Mouse.OverrideCursor = Cursors.SizeWE;

                if (NavTabsStackPanel != null)
                {
                    var visibleChildren = NavTabsStackPanel.Children
                        .OfType<FrameworkElement>()
                        .Where(c => c.Visibility == Visibility.Visible)
                        .ToList();
                    _dragInitialSlot = visibleChildren.IndexOf(_navDragItem);
                    _dragTargetSlot = _dragInitialSlot;
                }
            }
        }

        if (_isNavDragging)
        {
            e.Handled = true;

            // Live horizontal displacement tracking cursor directly
            if (_navDragItem.RenderTransform is TransformGroup group)
            {
                var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (translate != null)
                {
                    translate.X = deltaX;
                }
            }

            UpdateNeighborSlotDisplacements();
        }
    }

    private void UpdateNeighborSlotDisplacements()
    {
        if (_navDragItem == null || NavTabsStackPanel == null) return;

        var visibleChildren = NavTabsStackPanel.Children
            .OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible)
            .ToList();

        int totalSlots = visibleChildren.Count;
        if (totalSlots <= 1) return;

        int initialSlot = visibleChildren.IndexOf(_navDragItem);
        if (initialSlot < 0) return;

        _dragInitialSlot = initialSlot;

        // Calculate visual position of dragged item
        double currentTranslateX = 0.0;
        if (_navDragItem.RenderTransform is TransformGroup group)
        {
            var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (translate != null)
            {
                currentTranslateX = translate.X;
            }
        }

        double visualX = (initialSlot * NavTabSlotWidth) + currentTranslateX;
        int targetSlot = CalculateNavTargetSlotWithHysteresis(_dragTargetSlot, visualX, NavTabSlotWidth, totalSlots);
        _dragTargetSlot = targetSlot;

        // Smoothly glide neighbors to open a gap for the dragged item
        for (int i = 0; i < totalSlots; i++)
        {
            var child = visibleChildren[i];
            if (child == _navDragItem) continue;

            double desiredOffset = 0.0;

            if (targetSlot < initialSlot)
            {
                // Dragged to the left: items between targetSlot and initialSlot-1 shift right (+NavTabSlotWidth)
                if (i >= targetSlot && i < initialSlot)
                {
                    desiredOffset = NavTabSlotWidth;
                }
            }
            else if (targetSlot > initialSlot)
            {
                // Dragged to the right: items between initialSlot+1 and targetSlot shift left (-NavTabSlotWidth)
                if (i > initialSlot && i <= targetSlot)
                {
                    desiredOffset = -NavTabSlotWidth;
                }
            }

            AnimateElementToX(child, desiredOffset);
        }
    }

    private static int CalculateNavTargetSlotWithHysteresis(int currentTarget, double visualPos, double pitch, int totalSlots)
    {
        double currentSlotCenter = currentTarget * pitch;
        double diff = visualPos - currentSlotCenter;

        int proposedSlot = currentTarget;
        if (diff > pitch * 0.55)
        {
            proposedSlot = (int)Math.Floor((visualPos + pitch * 0.45) / pitch);
        }
        else if (diff < -pitch * 0.55)
        {
            proposedSlot = (int)Math.Ceiling((visualPos - pitch * 0.45) / pitch);
        }

        return Math.Clamp(proposedSlot, 0, totalSlots - 1);
    }

    private void AnimateElementToX(FrameworkElement element, double targetX)
    {
        if (element.RenderTransform is not TransformGroup group) return;
        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        if (translate == null) return;

        if (_neighborTargetOffsets.TryGetValue(element, out double currentTarget) &&
            Math.Abs(currentTarget - targetX) < 0.5)
        {
            return;
        }

        _neighborTargetOffsets[element] = targetX;

        var anim = new DoubleAnimation
        {
            To = targetX,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        translate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void NavIcon_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool wasDragging = _isNavDragging;
        var clickedItem = _navDragItem;

        EndNavDrag(saveOrder: wasDragging);

        if (!wasDragging && clickedItem != null && clickedItem.Tag is string tagStr)
        {
            if (Enum.TryParse<NotchView>(tagStr, true, out var targetView))
            {
                NavigateToNotchView(targetView);
            }
        }
    }

    private void NavIcon_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isNavDragging)
        {
            EndNavDrag(saveOrder: true);
        }
        else
        {
            EndNavDrag(saveOrder: false);
        }
    }

    private void EndNavDrag(bool saveOrder)
    {
        if (_isEndingNavDrag) return;
        _isEndingNavDrag = true;

        try
        {
            var item = _navDragItem;
            bool wasDragging = _isNavDragging;

            _isNavDragging = false;
            _navDragItem = null;

            if (item != null)
            {
                if (_hasCapturedNavMouse)
                {
                    _hasCapturedNavMouse = false;
                    try
                    {
                        item.ReleaseMouseCapture();
                    }
                    catch { }
                }

                Mouse.OverrideCursor = null;

                if (wasDragging && _dragTargetSlot >= 0 && _dragInitialSlot >= 0 && _dragTargetSlot != _dragInitialSlot)
                {
                    double finalTranslateX = (_dragTargetSlot - _dragInitialSlot) * NavTabSlotWidth;
                    AnimateNavDragDropSettle(item, finalTranslateX, onCompleted: () =>
                    {
                        CommitFinalTabOrder();
                        if (saveOrder) PersistNavTabOrderFromUI();
                    });
                }
                else
                {
                    AnimateNavDragDropSettle(item, 0.0, onCompleted: () =>
                    {
                        ResetAllNavTabTransforms();
                        UpdateNavIconsActiveState();
                    });
                }
            }
            else
            {
                ResetAllNavTabTransforms();
                UpdateNavIconsActiveState();
            }
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error("NAV-DRAG", ex, "Error during EndNavDrag");
            ResetAllNavTabTransforms();
            UpdateNavIconsActiveState();
        }
        finally
        {
            _isEndingNavDrag = false;
            _neighborTargetOffsets.Clear();
        }
    }

    private void CommitFinalTabOrder()
    {
        if (NavTabsStackPanel == null || _dragInitialSlot < 0 || _dragTargetSlot < 0 || _dragInitialSlot == _dragTargetSlot)
        {
            ResetAllNavTabTransforms();
            UpdateNavIconsActiveState();
            return;
        }

        var visibleChildren = NavTabsStackPanel.Children
            .OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible)
            .ToList();

        if (_dragInitialSlot < visibleChildren.Count && _dragTargetSlot < visibleChildren.Count)
        {
            var dragged = visibleChildren[_dragInitialSlot];
            visibleChildren.RemoveAt(_dragInitialSlot);
            visibleChildren.Insert(_dragTargetSlot, dragged);

            var allChildren = NavTabsStackPanel.Children.OfType<FrameworkElement>().ToList();
            NavTabsStackPanel.Children.Clear();

            foreach (var child in visibleChildren)
            {
                NavTabsStackPanel.Children.Add(child);
            }

            foreach (var child in allChildren)
            {
                if (child.Visibility != Visibility.Visible)
                {
                    NavTabsStackPanel.Children.Add(child);
                }
            }
        }

        ResetAllNavTabTransforms();
        UpdateNavIconsActiveState();
    }

    private void AnimateNavDragLift(FrameworkElement? item, bool lifted)
    {
        if (item?.RenderTransform is not TransformGroup group) return;

        var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        var shadow = item.Effect as DropShadowEffect;

        if (lifted)
        {
            // Scale up slightly to 1.18x
            if (scale != null)
            {
                var animScale = new DoubleAnimation(1.18, new Duration(TimeSpan.FromMilliseconds(160)))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            }

            // Lift upward slightly (-2px)
            if (translate != null)
            {
                var animY = new DoubleAnimation(-2.0, new Duration(TimeSpan.FromMilliseconds(160)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                translate.BeginAnimation(TranslateTransform.YProperty, animY);
            }

            // Ensure dragged item is clearly visible
            item.BeginAnimation(UIElement.OpacityProperty, null);
            item.Opacity = 1.0;

            if (shadow != null && !shadow.IsFrozen)
            {
                shadow.Color = Colors.Black;
                shadow.BlurRadius = 12;
                shadow.ShadowDepth = 2;
                shadow.Opacity = 0.95;
            }
        }
    }

    private void AnimateNavDragDropSettle(FrameworkElement item, double targetX, Action? onCompleted = null)
    {
        Panel.SetZIndex(item, 1000);

        if (item.RenderTransform is not TransformGroup group)
        {
            Panel.SetZIndex(item, 0);
            onCompleted?.Invoke();
            return;
        }

        var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        var shadow = item.Effect as DropShadowEffect;

        if (shadow != null && !shadow.IsFrozen)
        {
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 8;
            shadow.ShadowDepth = 0;
            shadow.Opacity = 0.9;
        }

        item.BeginAnimation(UIElement.OpacityProperty, null);

        // Settle scale back to 1.0
        if (scale != null)
        {
            var animScale = new DoubleAnimation
            {
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(animScale, VNotch.Services.AnimationConfig.TargetFps);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
        }

        // Settle Y back to 0
        if (translate != null)
        {
            var animY = new DoubleAnimation
            {
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
            translate.BeginAnimation(TranslateTransform.YProperty, animY);

            // Settle X into slot
            var animX = new DoubleAnimation
            {
                To = targetX,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
            animX.Completed += (s, e) =>
            {
                Panel.SetZIndex(item, 0);
                onCompleted?.Invoke();
            };
            translate.BeginAnimation(TranslateTransform.XProperty, animX);
        }
        else
        {
            Panel.SetZIndex(item, 0);
            onCompleted?.Invoke();
        }
    }

    private void ResetAllNavTabTransforms(FrameworkElement? excludeItem = null)
    {
        if (NavTabsStackPanel == null) return;

        foreach (var child in NavTabsStackPanel.Children.OfType<FrameworkElement>())
        {
            if (child == excludeItem) continue;

            if (child.RenderTransform is TransformGroup group)
            {
                var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (translate != null)
                {
                    translate.BeginAnimation(TranslateTransform.XProperty, null);
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
                    translate.X = 0;
                    translate.Y = 0;
                }

                var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
                if (scale != null)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleX = 1.0;
                    scale.ScaleY = 1.0;
                }
            }

            child.BeginAnimation(UIElement.OpacityProperty, null);
            Panel.SetZIndex(child, 0);

            if (child.Effect is DropShadowEffect shadow && !shadow.IsFrozen)
            {
                shadow.Color = Colors.Black;
                shadow.BlurRadius = 8;
                shadow.ShadowDepth = 0;
                shadow.Opacity = 0.9;
            }
        }
    }

    private void PersistNavTabOrderFromUI()
    {
        if (NavTabsStackPanel == null) return;

        var orderedTags = NavTabsStackPanel.Children
            .OfType<FrameworkElement>()
            .Select(f => f.Tag?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        if (orderedTags.Count > 0)
        {
            string newOrder = string.Join(",", orderedTags);
            if (!string.Equals(_settings.NavTabOrder, newOrder, StringComparison.OrdinalIgnoreCase))
            {
                _settings.NavTabOrder = newOrder;
                _settingsService.Save(_settings);
            }
        }
    }

    #endregion

    #region Tab Sequence & Universal Navigation

    public void ApplyNavTabOrderAndVisibility()
    {
        if (NavTabsStackPanel == null) return;

        // Build mapping of Tag -> UIElement
        var elementsByTag = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        foreach (UIElement child in NavTabsStackPanel.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string tag)
            {
                elementsByTag[tag] = fe;
            }
        }

        // Determine configured order
        var orderTokens = (_settings.NavTabOrder ?? "Media,Secondary,Timer,AudioMixer")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Ensure all known elements are included
        foreach (var key in elementsByTag.Keys)
        {
            if (!orderTokens.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                orderTokens.Add(key);
            }
        }

        // Reorder children in NavTabsStackPanel only if the order actually changed
        var currentChildrenTags = NavTabsStackPanel.Children
            .OfType<FrameworkElement>()
            .Select(f => f.Tag?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        if (!currentChildrenTags.SequenceEqual(orderTokens, StringComparer.OrdinalIgnoreCase))
        {
            NavTabsStackPanel.Children.Clear();
            foreach (var token in orderTokens)
            {
                if (elementsByTag.TryGetValue(token, out var element))
                {
                    NavTabsStackPanel.Children.Add(element);
                }
            }
        }

        // Apply visibility from VisibleNavTabs
        var visibleTokens = new HashSet<string>(
            (_settings.VisibleNavTabs ?? "Media,Secondary,Timer,AudioMixer")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        // Home/Media is always kept visible
        visibleTokens.Add("Media");

        foreach (UIElement child in NavTabsStackPanel.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string tag)
            {
                fe.Visibility = visibleTokens.Contains(tag) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // If the active view's tab was just disabled, return gracefully to Home
        if (_isAudioView && !visibleTokens.Contains("AudioMixer"))
        {
            NavigateToNotchView(NotchView.Media);
        }
        else if (_isTimerView && !visibleTokens.Contains("Timer"))
        {
            NavigateToNotchView(NotchView.Media);
        }
        else if (_isSecondaryView && !visibleTokens.Contains("Secondary"))
        {
            NavigateToNotchView(NotchView.Media);
        }

        ResetAllNavTabTransforms();
        UpdateNavIconsActiveState();
    }

    public List<NotchView> GetActiveTabSequence()
    {
        var sequence = new List<NotchView>();
        if (NavTabsStackPanel == null)
        {
            sequence.Add(NotchView.Media);
            return sequence;
        }

        foreach (UIElement child in NavTabsStackPanel.Children)
        {
            if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible && fe.Tag is string tagStr)
            {
                if (Enum.TryParse<NotchView>(tagStr, true, out var view))
                {
                    sequence.Add(view);
                }
            }
        }

        if (sequence.Count == 0)
        {
            sequence.Add(NotchView.Media);
        }

        return sequence;
    }

    public void NavigateToNotchView(NotchView targetView)
    {
        if (_isAnimating) return;

        NotchView currentView = _isAudioView ? NotchView.AudioMixer
                              : _isTimerView ? NotchView.Timer
                              : _isSecondaryView ? NotchView.Secondary
                              : NotchView.Media;

        if (currentView == targetView) return;

        switch (targetView)
        {
            case NotchView.Media:
                if (_isAudioView)
                {
                    SwitchFromAudioToPrimaryView();
                }
                else if (_isTimerView)
                {
                    SwitchFromTimerToPrimaryView();
                }
                else if (_isSecondaryView)
                {
                    StopCameraPreviewForViewExit();
                    SwitchToPrimaryView();
                }
                break;

            case NotchView.Secondary:
                if (_isAudioView)
                {
                    SwitchFromAudioToSecondaryView();
                }
                else if (_isTimerView)
                {
                    SwitchFromTimerToSecondaryView();
                }
                else if (!_isSecondaryView)
                {
                    SwitchToSecondaryView();
                }
                break;

            case NotchView.Timer:
                if (_isAudioView)
                {
                    SwitchFromAudioToTimerView();
                }
                else if (_isSecondaryView)
                {
                    StopCameraPreviewForViewExit();
                    SwitchFromSecondaryToTimerView();
                }
                else if (!_isTimerView)
                {
                    SwitchToTimerView();
                }
                break;

            case NotchView.AudioMixer:
                SwitchToAudioView();
                break;
        }
    }

    #endregion
}
