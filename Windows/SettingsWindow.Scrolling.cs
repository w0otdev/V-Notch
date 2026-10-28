using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Smooth Scroll

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_ui_smooth_scroll_step(
        double currentOffset,
        double targetOffset,
        double velocity,
        double maxScroll,
        double friction,
        double minVelocity,
        double dtSeconds,
        out double outNextOffset,
        out double outNextVelocity,
        out byte outSettled);

    private double _scrollVelocity;
    private double _scrollTarget;
    private bool _isScrollAnimating;
    private long _lastScrollTickTimestamp;
    private const double ScrollFriction = 0.82;
    private const double ScrollSensitivity = 1.2;
    private const double ScrollMinVelocity = 0.3;

    private bool IsAnyComboBoxDropDownOpen()
    {
        if (WidgetCombo?.IsDropDownOpen == true ||
            MonitorCombo?.IsDropDownOpen == true ||
            LanguageCombo?.IsDropDownOpen == true ||
            CameraCombo?.IsDropDownOpen == true ||
            VisualizerAudioCombo?.IsDropDownOpen == true ||
            SkinCombo?.IsDropDownOpen == true ||
            GlassPresetCombo?.IsDropDownOpen == true ||
            ProcessPriorityCombo?.IsDropDownOpen == true ||
            GpuPreferenceCombo?.IsDropDownOpen == true)
        {
            return true;
        }

        return FindVisualChildren<ComboBox>(this).Any(c => c.IsDropDownOpen);
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsAnyComboBoxDropDownOpen())
        {
            // A ComboBox popup is logically connected to this window, so this
            var dropdownScrollViewer = FindVisualAncestor<ScrollViewer>(e.OriginalSource as DependencyObject);
            if (dropdownScrollViewer != null && !ReferenceEquals(dropdownScrollViewer, SettingsScrollViewer))
            {
                double target = CalculateDropDownWheelTarget(
                    dropdownScrollViewer.VerticalOffset,
                    dropdownScrollViewer.ScrollableHeight,
                    e.Delta);
                dropdownScrollViewer.ScrollToVerticalOffset(target);
            }

            // Never scroll the settings page behind an open dropdown.
            e.Handled = true;
            return;
        }

        e.Handled = true;

        double delta = -e.Delta * ScrollSensitivity;
        double maxScroll = SettingsScrollViewer.ScrollableHeight;

        if (!_isScrollAnimating)
        {
            _scrollTarget = SettingsScrollViewer.VerticalOffset;
        }

        _scrollVelocity += delta * 0.3;
        _scrollTarget = Math.Clamp(_scrollTarget + delta, 0, maxScroll);

        if (!_isScrollAnimating)
        {
            _isScrollAnimating = true;
            _lastScrollTickTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering -= SmoothScroll_Tick;
            CompositionTarget.Rendering += SmoothScroll_Tick;
        }
    }

    internal void StopSmoothScroll()
    {
        _scrollVelocity = 0;
        _scrollTarget = 0;
        if (!_isScrollAnimating) return;
        _isScrollAnimating = false;
        CompositionTarget.Rendering -= SmoothScroll_Tick;
    }

    internal static double CalculateDropDownWheelTarget(double currentOffset, double scrollableHeight, int wheelDelta)
    {
        const double wheelScale = 0.35;
        return Math.Clamp(currentOffset - wheelDelta * wheelScale, 0, Math.Max(0, scrollableHeight));
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
                return match;

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SmoothScroll_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || SettingsScrollViewer is not { IsVisible: true })
        {
            StopSmoothScroll();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double dtSeconds = _lastScrollTickTimestamp > 0
            ? (now - _lastScrollTickTimestamp) / (double)Stopwatch.Frequency
            : 1.0 / 60.0;
        _lastScrollTickTimestamp = now;

        double maxScroll = SettingsScrollViewer.ScrollableHeight;
        _scrollTarget = Math.Clamp(_scrollTarget, 0, maxScroll);
        double current = SettingsScrollViewer.VerticalOffset;

        _ = vn_ui_smooth_scroll_step(
            current,
            _scrollTarget,
            _scrollVelocity,
            maxScroll,
            ScrollFriction,
            ScrollMinVelocity,
            dtSeconds,
            out double nextOffset,
            out double nextVelocity,
            out byte settled);

        _scrollVelocity = nextVelocity;
        SettingsScrollViewer.ScrollToVerticalOffset(nextOffset);

        if (settled != 0)
        {
            _isScrollAnimating = false;
            CompositionTarget.Rendering -= SmoothScroll_Tick;
        }
    }

    #endregion
}
