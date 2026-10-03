using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Navigation Tabs Drag Reordering in Settings

    private FrameworkElement? _settingsNavDragRow = null;
    private Point _settingsNavDragStartPoint;
    private bool _isSettingsNavRowDragging = false;
    private bool _hasCapturedSettingsNavMouse = false;
    private int _settingsNavInitialSlot = -1;
    private int _settingsNavTargetSlot = -1;
    private double _settingsNavRowPitch = 44.0;
    private readonly Dictionary<FrameworkElement, double> _settingsNavNeighborOffsets = new();

    private void PopulateNavTabsSettings()
    {
        if (NavTabsSettingsContainer == null) return;
        NavTabsSettingsContainer.Children.Clear();

        var tabMetadata = new Dictionary<string, (string title, string iconPath)>
        {
            [NavTabMedia] = ("Home", "M8 0L0 6V8H1V15H4V10H7V15H15V8H16V6L14 4.5V1H11V2.25L8 0ZM9 10H12V13H9V10Z"),
            ["Secondary"] = ("File Shelf", "M479.66,268.7l-32-151.81C441.48,83.77,417.68,64,384,64H128c-16.8,0-31,4.69-42.1,13.94s-18.37,22.31-21.58,38.89l-32,151.87A16.65,16.65,0,0,0,32,272V384a64,64,0,0,0,64,64H416a64,64,0,0,0,64-64V272A16.65,16.65,0,0,0,479.66,268.7Zm-384-145.4c0-.1,0-.19,0-.28,3.55-18.43,13.81-27,32.29-27H384c18.61,0,28.87,8.55,32.27,26.91,0,.13.05.26.07.39l26.93,127.88a4,4,0,0,1-3.92,4.82H320a15.92,15.92,0,0,0-16,15.82,48,48,0,1,1-96,0A15.92,15.92,0,0,0,192,256H72.65a4,4,0,0,1-3.92-4.82Z"),
            ["Timer"] = ("Clock & Timer", "M2 12C2 6.47715 6.47715 2 12 2C17.5228 2 22 6.47715 22 12C22 17.5228 17.5228 22 12 22C6.47715 22 2 17.5228 2 12ZM15.8321 14.5547C15.5257 15.0142 14.9048 15.1384 14.4453 14.8321L11.8451 13.0986C11.3171 12.7466 11 12.1541 11 11.5196V11.5V7C11 6.44772 11.4477 6 12 6C12.5523 6 13 6.44772 13 7V11.4648L15.5547 13.1679C16.0142 13.4743 16.1384 14.0952 15.8321 14.5547Z"),
            ["AudioMixer"] = ("Audio Mixer", "M13.5 2.5C13.5 2.1 13.05 1.86 12.72 2.09L6.8 6.2H3.5C2.95 6.2 2.5 6.65 2.5 7.2V12.8C2.5 13.35 2.95 13.8 3.5 13.8H6.8L12.72 17.91C13.05 18.14 13.5 17.9 13.5 17.5V2.5ZM16.04 6.05C15.74 5.79 15.28 5.82 15.02 6.13C14.76 6.43 14.79 6.89 15.1 7.15C16.0 7.93 16.5 8.93 16.5 10C16.5 11.07 16.0 12.07 15.1 12.85C14.79 13.11 14.76 13.57 15.02 13.87C15.28 14.18 15.74 14.21 16.04 13.95C17.25 12.91 18 11.5 18 10C18 8.5 17.25 7.09 16.04 6.05Z")
        };

        var orderTokens = (_settings.NavTabOrder ?? DefaultNavTabs)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        orderTokens.AddRange(tabMetadata.Keys.Where(key => !orderTokens.Contains(key, StringComparer.OrdinalIgnoreCase)));

        var visibleTokens = new HashSet<string>(
            (_settings.VisibleNavTabs ?? DefaultNavTabs)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        visibleTokens.Add(NavTabMedia);

        for (int i = 0; i < orderTokens.Count; i++)
        {
            string token = orderTokens[i];
            if (!tabMetadata.TryGetValue(token, out var meta)) continue;

            var rowBorder = new Border
            {
                Tag = token,
                Height = 38,
                Background = CardBackgroundBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 3, 0, 3),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup
                {
                    Children = { new TranslateTransform(), new ScaleTransform() }
                }
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left side: CheckBox + Icon + Title
            var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var check = new CheckBox
            {
                IsChecked = visibleTokens.Contains(token),
                IsEnabled = !string.Equals(token, NavTabMedia, StringComparison.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            string capturedToken = token;
            check.Checked += (s, e) =>
            {
                visibleTokens.Add(capturedToken);
                _settings.VisibleNavTabs = string.Join(",", visibleTokens);
                _settingsAppService.ApplyAsync(_settings).SafeFireAndForget("SETTINGS-NAVTABS");
                (Application.Current.MainWindow as MainWindow)?.ApplyNavTabOrderAndVisibility();
            };
            check.Unchecked += (s, e) =>
            {
                visibleTokens.Remove(capturedToken);
                _settings.VisibleNavTabs = string.Join(",", visibleTokens);
                _settingsAppService.ApplyAsync(_settings).SafeFireAndForget("SETTINGS-NAVTABS");
                (Application.Current.MainWindow as MainWindow)?.ApplyNavTabOrderAndVisibility();
            };
            leftStack.Children.Add(check);

            var iconBox = new Viewbox { Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            iconBox.Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(meta.iconPath), Fill = VNotch.Services.UiPalette.PrimaryBrush };
            leftStack.Children.Add(iconBox);

            var titleText = new TextBlock { Text = meta.title, Style = (Style)FindResource("ValueText"), VerticalAlignment = VerticalAlignment.Center };
            leftStack.Children.Add(titleText);

            rowGrid.Children.Add(leftStack);
            Grid.SetColumn(leftStack, 0);

            // Right side: Drag Handle Grip (≡)
            var dragHandle = new Border
            {
                Width = 34,
                Height = 26,
                Background = DragHandleBackgroundBrush,
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.SizeNS,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = Loc.Get("settings.tab.drag")
            };

            var gripIcon = new Viewbox
            {
                Width = 14,
                Height = 10,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            gripIcon.Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M2 2.2h16v1.6H2zm0 3.8h16v1.6H2zm0 3.8h16v1.6H2z"),
                Fill = DragHandleGlyphBrush
            };
            dragHandle.Child = gripIcon;

            dragHandle.MouseEnter += (s, e) =>
            {
                if (!_isSettingsNavRowDragging)
                    dragHandle.Background = DragHandleHoverBrush;
            };
            dragHandle.MouseLeave += (s, e) =>
            {
                if (!_isSettingsNavRowDragging)
                    dragHandle.Background = DragHandleBackgroundBrush;
            };

            rowGrid.Children.Add(dragHandle);
            Grid.SetColumn(dragHandle, 1);

            rowBorder.Child = rowGrid;

            // Wire drag-and-drop exclusively to the dragHandle so CheckBox has zero interference
            dragHandle.PreviewMouseLeftButtonDown += (s, e) => StartSettingsNavRowDrag(rowBorder, e);

            rowBorder.PreviewMouseMove += SettingsNavRow_PreviewMouseMove;
            rowBorder.PreviewMouseLeftButtonUp += SettingsNavRow_PreviewMouseLeftButtonUp;
            rowBorder.LostMouseCapture += SettingsNavRow_LostMouseCapture;

            NavTabsSettingsContainer.Children.Add(rowBorder);
        }
    }

    private void StartSettingsNavRowDrag(FrameworkElement rowBorder, MouseButtonEventArgs e)
    {
        if (NavTabsSettingsContainer == null) return;

        _settingsNavDragRow = rowBorder;
        _settingsNavDragStartPoint = e.GetPosition(NavTabsSettingsContainer);
        _isSettingsNavRowDragging = false;
        _hasCapturedSettingsNavMouse = rowBorder.CaptureMouse();
        _settingsNavInitialSlot = NavTabsSettingsContainer.Children.IndexOf(rowBorder);
        _settingsNavTargetSlot = _settingsNavInitialSlot;
        _settingsNavNeighborOffsets.Clear();
        if (rowBorder.RenderTransform is TransformGroup group)
        {
            var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            translate?.BeginAnimation(TranslateTransform.YProperty, null);
            translate?.BeginAnimation(TranslateTransform.XProperty, null);
        }

        e.Handled = true;
    }

    private void SettingsNavRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_settingsNavDragRow == null || e.LeftButton != MouseButtonState.Pressed || NavTabsSettingsContainer == null) return;

        Point current = e.GetPosition(NavTabsSettingsContainer);
        double deltaY = current.Y - _settingsNavDragStartPoint.Y;

        if (!_isSettingsNavRowDragging && Math.Abs(deltaY) > 3)
        {
            BeginSettingsNavDrag();
        }

        if (_isSettingsNavRowDragging)
        {
            e.Handled = true;

            // Directly track vertical displacement
            if (_settingsNavDragRow.RenderTransform is TransformGroup group)
            {
                var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (translate != null)
                {
                    translate.Y = deltaY;
                }
            }

            UpdateSettingsNavNeighborDisplacements();
        }
    }

    private void BeginSettingsNavDrag()
    {
        if (_settingsNavDragRow == null) return;

        _isSettingsNavRowDragging = true;
        if (!_hasCapturedSettingsNavMouse)
        {
            _hasCapturedSettingsNavMouse = _settingsNavDragRow.CaptureMouse();
        }
        Panel.SetZIndex(_settingsNavDragRow, 100);

        if (_settingsNavDragRow is Border border)
        {
            border.Background = DraggingBackgroundBrush;
            border.BorderBrush = DraggingBorderBrush;
            border.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.55
            };
        }

        if (_settingsNavDragRow.RenderTransform is TransformGroup group)
        {
            var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            if (scale != null)
            {
                var animScale = new DoubleAnimation
                {
                    To = 1.025,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Timeline.SetDesiredFrameRate(animScale, AnimationConfig.TargetFps);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            }
        }

        Mouse.OverrideCursor = Cursors.SizeNS;
    }

    private void UpdateSettingsNavNeighborDisplacements()
    {
        if (_settingsNavDragRow == null || NavTabsSettingsContainer == null) return;

        int totalRows = NavTabsSettingsContainer.Children.Count;
        if (totalRows <= 1 || _settingsNavInitialSlot < 0) return;

        double currentTranslateY = 0.0;
        if (_settingsNavDragRow.RenderTransform is TransformGroup group)
        {
            var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (translate != null)
            {
                currentTranslateY = translate.Y;
            }
        }

        double visualY = (_settingsNavInitialSlot * _settingsNavRowPitch) + currentTranslateY;
        int targetSlot = CalculateSettingsTargetSlotWithHysteresis(_settingsNavTargetSlot, visualY, _settingsNavRowPitch, totalRows);
        _settingsNavTargetSlot = targetSlot;

        for (int i = 0; i < totalRows; i++)
        {
            var child = NavTabsSettingsContainer.Children[i] as FrameworkElement;
            if (child == null || child == _settingsNavDragRow) continue;

            double desiredOffset = CalculateNeighborDesiredOffset(i, _settingsNavInitialSlot, targetSlot, _settingsNavRowPitch);
            AnimateSettingsRowToY(child, desiredOffset);
        }
    }

    private static double CalculateNeighborDesiredOffset(int index, int initialSlot, int targetSlot, double pitch)
    {
        if (targetSlot < initialSlot && index >= targetSlot && index < initialSlot)
        {
            // Dragged UP: rows between targetSlot and initialSlot - 1 shift DOWN (+pitch)
            return pitch;
        }

        if (targetSlot > initialSlot && index > initialSlot && index <= targetSlot)
        {
            // Dragged DOWN: rows between initialSlot + 1 and targetSlot shift UP (-pitch)
            return -pitch;
        }

        return 0.0;
    }

    private static int CalculateSettingsTargetSlotWithHysteresis(int currentTarget, double visualPos, double pitch, int totalSlots)
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

    private void AnimateSettingsRowToY(FrameworkElement element, double targetY)
    {
        if (element.RenderTransform is not TransformGroup group) return;
        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        if (translate == null) return;

        if (_settingsNavNeighborOffsets.TryGetValue(element, out double currentTarget) &&
            Math.Abs(currentTarget - targetY) < 0.5)
        {
            return;
        }

        _settingsNavNeighborOffsets[element] = targetY;

        var anim = new DoubleAnimation
        {
            To = targetY,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        translate.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void SettingsNavRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSettingsNavRowDragging || _settingsNavDragRow != null)
        {
            e.Handled = true;
            EndSettingsNavRowDrag();
        }
    }

    private void SettingsNavRow_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isSettingsNavRowDragging)
        {
            EndSettingsNavRowDrag();
        }
    }

    private void EndSettingsNavRowDrag()
    {
        var row = _settingsNavDragRow;
        if (row == null) return;

        bool wasDragging = _isSettingsNavRowDragging;
        _isSettingsNavRowDragging = false;
        _settingsNavDragRow = null;

        if (_hasCapturedSettingsNavMouse)
        {
            _hasCapturedSettingsNavMouse = false;
            try { row.ReleaseMouseCapture(); }
            catch (Exception)
            {
                // Ignore capture release exceptions during drag end
            }
        }

        Mouse.OverrideCursor = null;

        if (wasDragging && _settingsNavTargetSlot >= 0 && _settingsNavInitialSlot >= 0 && _settingsNavTargetSlot != _settingsNavInitialSlot)
        {
            double finalOffsetY = (_settingsNavTargetSlot - _settingsNavInitialSlot) * _settingsNavRowPitch;
            AnimateSettingsRowDropSettle(row, finalOffsetY, onCompleted: () =>
            {
                CommitSettingsNavTabOrder();
            });
        }
        else
        {
            AnimateSettingsRowDropSettle(row, 0.0, onCompleted: () =>
            {
                ResetAllSettingsNavRowTransforms();
            });
        }
    }

    private void CommitSettingsNavTabOrder()
    {
        if (NavTabsSettingsContainer == null || _settingsNavInitialSlot < 0 || _settingsNavTargetSlot < 0 || _settingsNavInitialSlot == _settingsNavTargetSlot)
        {
            ResetAllSettingsNavRowTransforms();
            return;
        }

        var orderTokens = (_settings.NavTabOrder ?? DefaultNavTabs)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_settingsNavInitialSlot < orderTokens.Count && _settingsNavTargetSlot < orderTokens.Count)
        {
            string movedToken = orderTokens[_settingsNavInitialSlot];
            orderTokens.RemoveAt(_settingsNavInitialSlot);
            orderTokens.Insert(_settingsNavTargetSlot, movedToken);

            _settings.NavTabOrder = string.Join(",", orderTokens);
            _settingsAppService.ApplyAsync(_settings).SafeFireAndForget("SETTINGS-NAVORDER");
            (Application.Current.MainWindow as MainWindow)?.ApplyNavTabOrderAndVisibility();
        }

        ResetAllSettingsNavRowTransforms();
        PopulateNavTabsSettings();
    }

    private void AnimateSettingsRowDropSettle(FrameworkElement row, double targetY, Action? onCompleted = null)
    {
        Panel.SetZIndex(row, 100);

        if (row is Border border)
        {
            border.Background = CardBackgroundBrush;
            border.BorderBrush = CardBorderBrush;
            border.Effect = null;
        }

        if (row.RenderTransform is not TransformGroup group)
        {
            Panel.SetZIndex(row, 0);
            onCompleted?.Invoke();
            return;
        }

        var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();

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

        if (translate != null)
        {
            var animY = new DoubleAnimation
            {
                To = targetY,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
            animY.Completed += (s, e) =>
            {
                Panel.SetZIndex(row, 0);
                onCompleted?.Invoke();
            };
            translate.BeginAnimation(TranslateTransform.YProperty, animY);
        }
        else
        {
            Panel.SetZIndex(row, 0);
            onCompleted?.Invoke();
        }
    }

    private void ResetAllSettingsNavRowTransforms()
    {
        if (NavTabsSettingsContainer == null) return;

        foreach (var child in NavTabsSettingsContainer.Children.OfType<FrameworkElement>())
        {
            if (child.RenderTransform is TransformGroup group)
            {
                var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (translate != null)
                {
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
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

            Panel.SetZIndex(child, 0);

            if (child is Border border)
            {
                border.Background = CardBackgroundBrush;
                border.BorderBrush = CardBorderBrush;
                border.Effect = null;
            }
        }
    }

    #endregion
}
