using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

// One interruptible highlight per popup. Rows keep their text and hit targets still.
public sealed partial class FluidMenuSurface : Grid
{
    public static readonly DependencyProperty IsRowActiveProperty = DependencyProperty.RegisterAttached(
        "IsRowActive", typeof(bool), typeof(FluidMenuSurface), new PropertyMetadata(false, RowStateChanged));
    public static readonly DependencyProperty KeepsRowActiveProperty = DependencyProperty.RegisterAttached(
        "KeepsRowActive", typeof(bool), typeof(FluidMenuSurface), new PropertyMetadata(false, RowStateChanged));

    public static void SetIsRowActive(DependencyObject row, bool value) => row.SetValue(IsRowActiveProperty, value);
    public static bool GetIsRowActive(DependencyObject row) => (bool)row.GetValue(IsRowActiveProperty);
    public static void SetKeepsRowActive(DependencyObject row, bool value) => row.SetValue(KeepsRowActiveProperty, value);
    public static bool GetKeepsRowActive(DependencyObject row) => (bool)row.GetValue(KeepsRowActiveProperty);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ShapeState
    {
        public double Top, Bottom, TopVelocity, BottomVelocity, WidthScale, WidthVelocity;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ShapeVisual { public double Y, ScaleX, ScaleY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FadeState
    {
        public double Y, YVelocity, Opacity, OpacityVelocity, SelectedOpacity, SelectedVelocity;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_nav_pill")]
    private static partial int StepShape(ref ShapeState state, double target, double height, double seconds,
        byte reduced, out ShapeVisual visual, out byte settled);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_nav_hover")]
    private static partial int StepFade(ref FadeState state, double target, byte visible, byte selected,
        double seconds, byte reduced, out byte settled);

    private static readonly Brush NormalFill = AccentTheme.Brush(Color.FromArgb(24, 218, 220, 227));
    private static readonly Brush NormalRim = AccentTheme.Brush(Color.FromArgb(16, 218, 220, 227));
    private static readonly Brush DangerFill = AccentTheme.Brush(Color.FromArgb(30, 213, 139, 145));
    private static readonly Brush DangerRim = AccentTheme.Brush(Color.FromArgb(24, 213, 139, 145));
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private ShapeState _shape;
    private FadeState _fade;
    private double _targetY, _height = 32;
    private bool _visible, _initialized;
    private long _timestamp;
    private TimeSpan _renderingTime = TimeSpan.MinValue;
    private int _request;

    internal Border Highlight { get; }
    internal Border? ActiveRow { get; private set; }
    internal bool IsAnimating { get; private set; }

    public FluidMenuSurface()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Highlight = new Border
        {
            VerticalAlignment = VerticalAlignment.Top, Height = 32, CornerRadius = new CornerRadius(8),
            Background = NormalFill, BorderBrush = NormalRim, BorderThickness = new Thickness(1),
            IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = new TransformGroup { Children = { _scale, _translate } }
        };
        Children.Add(Highlight);
        MouseLeave += (_, _) =>
        {
            if (ActiveRow == null || !GetKeepsRowActive(ActiveRow)) HideHighlight();
        };
        Unloaded += (_, _) => ResetHighlight();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetHighlight(); };
    }

    private static void RowStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border row) return;
        if (!row.IsLoaded)
        {
            row.Loaded -= RowLoaded;
            row.Loaded += RowLoaded;
            return;
        }
        UpdateRow(row);
    }

    private static void RowLoaded(object sender, RoutedEventArgs e)
    {
        var row = (Border)sender;
        row.Loaded -= RowLoaded;
        UpdateRow(row);
    }

    private static void UpdateRow(Border row)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(row); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FluidMenuSurface surface) continue;
            if (GetIsRowActive(row) || GetKeepsRowActive(row)) surface.SelectRow(row);
            else surface.ReleaseRow(row);
            break;
        }
    }

    internal void SelectRow(Border row)
    {
        if (!row.IsEnabled || row.ActualHeight <= 0) return;
        ++_request;
        ActiveRow = row;
        _targetY = row.TranslatePoint(new Point(), this).Y;
        _height = row.ActualHeight;
        Highlight.Height = _height;
        bool danger = row.TemplatedParent is MenuItem { Tag: "danger" };
        Highlight.SetValue(TrayMotion.BackgroundProperty, danger ? DangerFill : NormalFill);
        Highlight.SetValue(TrayMotion.BorderBrushProperty, danger ? DangerRim : NormalRim);
        if (!_initialized || (!IsAnimating && _fade.Opacity == 0))
        {
            _shape = new ShapeState { Top = _targetY, Bottom = _targetY + _height, WidthScale = 1 };
            _initialized = true;
        }
        _visible = true;
        StartMotion();
        if (IsAnimating) AdvanceMotion(0);
    }

    private void ReleaseRow(Border row)
    {
        if (!ReferenceEquals(ActiveRow, row)) return;
        int request = ++_request;
        // The next row's state arrives in the same input turn. Gaps between rows
        // must not start a fade-out or restart the surface's spring.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (request == _request && !IsMouseOver && !GetKeepsRowActive(row)) HideHighlight();
        });
    }

    private static bool MotionAllowed => !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation &&
        InputManager.Current.MostRecentInputDevice is not KeyboardDevice;

    private void HideHighlight()
    {
        ++_request;
        ActiveRow = null;
        _visible = false;
        StartMotion();
    }

    private void StartMotion()
    {
        if (!IsVisible || !MotionAllowed) { StopMotion(); return; }
        if (IsAnimating) return;
        IsAnimating = true;
        _timestamp = Stopwatch.GetTimestamp();
        _renderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += RenderMotion;
    }

    private void RenderMotion(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (_renderingTime == frame.RenderingTime) return;
            _renderingTime = frame.RenderingTime;
        }
        if (!IsVisible || !MotionAllowed) { StopMotion(); return; }
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_timestamp, now).TotalSeconds;
        if (seconds < .9 / AnimationConfig.TargetFps) return;
        _timestamp = now;
        AdvanceMotion(seconds);
    }

    internal void AdvanceMotion(double seconds)
    {
        if (!IsAnimating) return;
        try
        {
            if (StepShape(ref _shape, _targetY, _height, seconds, 0, out var visual, out byte shapeSettled) != 0 ||
                StepFade(ref _fade, 0, _visible ? (byte)1 : (byte)0, 0, seconds, 0, out byte fadeSettled) != 0)
            { StopMotion(); return; }
            _translate.Y = visual.Y;
            _scale.ScaleX = visual.ScaleX;
            _scale.ScaleY = visual.ScaleY;
            Highlight.Opacity = Math.Clamp(_fade.Opacity, 0, 1);
            if (shapeSettled != 0 && fadeSettled != 0) StopMotion();
        }
        catch (DllNotFoundException) { StopMotion(); }
        catch (EntryPointNotFoundException) { StopMotion(); }
    }

    private void StopMotion()
    {
        CompositionTarget.Rendering -= RenderMotion;
        IsAnimating = false;
        _shape = new ShapeState { Top = _targetY, Bottom = _targetY + _height, WidthScale = 1 };
        _fade = new FadeState { Opacity = _visible ? 1 : 0 };
        _translate.Y = _targetY;
        _scale.ScaleX = _scale.ScaleY = 1;
        Highlight.Opacity = _fade.Opacity;
    }

    private void ResetHighlight()
    {
        ++_request;
        _visible = false;
        ActiveRow = null;
        StopMotion();
        _initialized = false;
    }
}
