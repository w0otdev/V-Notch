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
            }
        }

        if (_isNavDragging)
        {
            e.Handled = true;

            // Live horizontal displacement
            if (_navDragItem.RenderTransform is TransformGroup group)
            {
                var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (translate != null)
                {
                    translate.X = deltaX;
                }
            }

            CheckAndPerformNavTabSwap(deltaX);
        }
    }

    private void CheckAndPerformNavTabSwap(double deltaX)
    {
        if (_navDragItem == null || NavTabsStackPanel == null) return;

        int currentIndex = NavTabsStackPanel.Children.IndexOf(_navDragItem);
        if (currentIndex < 0) return;

        // Moving right (deadband 0.65 to prevent rapid oscillation)
        if (deltaX > (NavTabSlotWidth * 0.65))
        {
            int nextIndex = -1;
            for (int i = currentIndex + 1; i < NavTabsStackPanel.Children.Count; i++)
            {
                if (NavTabsStackPanel.Children[i].Visibility == Visibility.Visible)
                {
                    nextIndex = i;
                    break;
                }
            }

            if (nextIndex >= 0)
            {
                var neighbor = NavTabsStackPanel.Children[nextIndex] as FrameworkElement;
                NavTabsStackPanel.Children.RemoveAt(currentIndex);
                NavTabsStackPanel.Children.Insert(nextIndex, _navDragItem);
                _navDragStartPoint.X += NavTabSlotWidth;

                if (_navDragItem.RenderTransform is TransformGroup group)
                {
                    var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                    if (translate != null)
                    {
                        translate.X -= NavTabSlotWidth;
                    }
                }

                AnimateNeighborSlide(neighbor, NavTabSlotWidth);
            }
        }
        // Moving left (deadband 0.65 to prevent rapid oscillation)
        else if (deltaX < -(NavTabSlotWidth * 0.65))
        {
            int prevIndex = -1;
            for (int i = currentIndex - 1; i >= 0; i--)
            {
                if (NavTabsStackPanel.Children[i].Visibility == Visibility.Visible)
                {
                    prevIndex = i;
                    break;
                }
            }

            if (prevIndex >= 0)
            {
                var neighbor = NavTabsStackPanel.Children[prevIndex] as FrameworkElement;
                NavTabsStackPanel.Children.RemoveAt(currentIndex);
                NavTabsStackPanel.Children.Insert(prevIndex, _navDragItem);
                _navDragStartPoint.X -= NavTabSlotWidth;

                if (_navDragItem.RenderTransform is TransformGroup group)
                {
                    var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                    if (translate != null)
                    {
                        translate.X += NavTabSlotWidth;
                    }
                }

                AnimateNeighborSlide(neighbor, -NavTabSlotWidth);
            }
        }
    }

    private void AnimateNeighborSlide(FrameworkElement? neighbor, double fromOffset)
    {
        if (neighbor?.RenderTransform is not TransformGroup group) return;
        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        if (translate == null) return;

        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.X = fromOffset;

        var slideAnim = new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        slideAnim.Completed += (s, e) =>
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = 0;
        };
        translate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
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

                Panel.SetZIndex(item, 0);
                AnimateNavDragLift(item, false);
                Mouse.OverrideCursor = null;

                if (saveOrder && wasDragging)
                {
                    PersistNavTabOrderFromUI();
                }
            }

            ResetAllNavTabTransforms(excludeItem: item);
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error("NAV-DRAG", ex, "Error during EndNavDrag");
        }
        finally
        {
            _isEndingNavDrag = false;
        }
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
                var animScale = new DoubleAnimation(1.18, new Duration(TimeSpan.FromMilliseconds(180)))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            }

            // Lift upward slightly (-2px)
            if (translate != null)
            {
                var animY = new DoubleAnimation(-2.0, new Duration(TimeSpan.FromMilliseconds(180)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                translate.BeginAnimation(TranslateTransform.YProperty, animY);
            }

            // Ensure dragged item is clearly visible while dragging
            item.BeginAnimation(UIElement.OpacityProperty, null);
            item.Opacity = 1.0;

            // Deepen black drop shadow for elevation
            if (shadow != null)
            {
                if (shadow.IsFrozen)
                {
                    shadow = shadow.Clone();
                    item.Effect = shadow;
                }
                shadow.Color = Colors.Black;
                shadow.BlurRadius = 12;
                shadow.ShadowDepth = 2;
                shadow.Opacity = 0.95;
            }
        }
        else
        {
            // Settle scale back to 1.0
            if (scale != null)
            {
                var animScale = new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(200)))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
                };
                animScale.Completed += (s, e) =>
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleX = 1.0;
                    scale.ScaleY = 1.0;
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            }

            // Settle Y back to 0
            if (translate != null)
            {
                var animY = new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(200)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                animY.Completed += (s, e) =>
                {
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
                    translate.Y = 0;
                };
                translate.BeginAnimation(TranslateTransform.YProperty, animY);

                // Settle residual X offset back to 0
                var animX = new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(200)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                animX.Completed += (s, e) =>
                {
                    translate.BeginAnimation(TranslateTransform.XProperty, null);
                    translate.X = 0;
                };
                translate.BeginAnimation(TranslateTransform.XProperty, animX);
            }

            // Restore normal black drop shadow
            if (shadow != null)
            {
                if (shadow.IsFrozen)
                {
                    shadow = shadow.Clone();
                    item.Effect = shadow;
                }
                shadow.Color = Colors.Black;
                shadow.BlurRadius = 8;
                shadow.ShadowDepth = 0;
                shadow.Opacity = 0.9;
            }

            // Clear any opacity animations on item
            item.BeginAnimation(UIElement.OpacityProperty, null);

            // Re-apply correct active/inactive opacities across all tabs
            UpdateNavIconsActiveState();
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

        // Reorder children in NavTabsStackPanel
        NavTabsStackPanel.Children.Clear();
        foreach (var token in orderTokens)
        {
            if (elementsByTag.TryGetValue(token, out var element))
            {
                NavTabsStackPanel.Children.Add(element);
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
