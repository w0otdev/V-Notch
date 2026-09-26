using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VNotch.Services;

// A non-activating, click-through visual: native OLE drag/drop keeps owning input.
internal sealed class ScreenshotDragPreview : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly nint _handle;
    private bool _disposed;

    public ScreenshotDragPreview(BitmapSource image, FrameworkElement source)
    {
        double ratio = Math.Min(160.0 / image.PixelWidth, 104.0 / image.PixelHeight);
        double width = Math.Max(1, image.PixelWidth * ratio);
        double height = Math.Max(1, image.PixelHeight * ratio);
        var scale = new ScaleTransform(1, 1);
        var visual = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(8),
            Background = Brushes.Black, RenderTransform = scale,
            RenderTransformOrigin = new Point(0, 0),
            Child = new Image { Source = image, Stretch = Stretch.Uniform,
                Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8) }
        };
        _window = new Window
        {
            Width = width + 16, Height = height + 16,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            IsHitTestVisible = false, Focusable = false,
            Content = new Grid { Children = { visual } }
        };
        visual.HorizontalAlignment = HorizontalAlignment.Left;
        visual.VerticalAlignment = VerticalAlignment.Top;
        var origin = source.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(source);
        _window.Left = origin.X / dpi.DpiScaleX;
        _window.Top = origin.Y / dpi.DpiScaleY;
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x080000A0);
        UpdatePosition();
        _window.Show();
        if (!AnimationConfig.ReduceMotion)
        {
            double startScale = Math.Clamp(source.ActualWidth / width, 0.25, 0.92);
            var lift = new DoubleAnimation(startScale, 1, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, lift);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, lift);
            visual.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.55, 0.95, TimeSpan.FromMilliseconds(160)));
        }
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += FollowCursor;
        _timer.Start();
    }

    private void FollowCursor(object? sender, EventArgs e) => UpdatePosition();

    public void UpdatePosition()
    {
        if (_disposed || !GetCursorPos(out var cursor)) return;
        // Screen coordinates stay in physical pixels across monitor boundaries.
        SetWindowPos(_handle, new nint(-1), cursor.X + 14, cursor.Y + 18, 0, 0,
            0x0010 | 0x0001); // NOACTIVATE | NOSIZE
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= FollowCursor;
        _window.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
}
