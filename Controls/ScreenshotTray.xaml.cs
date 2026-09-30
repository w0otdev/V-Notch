using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;


namespace VNotch.Controls;

public partial class ScreenshotTray : UserControl
{
    private BitmapSource? _image;
    private int _presentationVersion;
    private Point? _dragStart;
    private readonly ScreenshotFileStore _store = new();
    public bool IsBusy { get; private set; }
    public bool IsDragging { get; private set; }
    public event Action? DismissRequested;
    public event Action? DragCancelled;
    public Func<BitmapSource, Task<bool>>? KeepRequested { get; set; }

    public ScreenshotTray()
    {
        InitializeComponent();
        PreviewFrame.MouseLeftButtonUp += (_, e) =>
        {
            _dragStart = null;
            PreviewFrame.ReleaseMouseCapture();
            e.Handled = true;
        };
        PreviewFrame.LostMouseCapture += (_, _) => _dragStart = null;
    }

    public Size ConfigureInline()
    {
        // A stable viewport keeps the island the same size for every capture.
        Surface.Width = 304;
        Surface.Background = Brushes.Transparent;
        Surface.BorderThickness = new Thickness(0);
        Surface.CornerRadius = new CornerRadius(0);
        PreviewFrame.Height = 136;
        // Collapsed controls report an empty DesiredSize. Measure without painting
        // the preview, then restore visibility before the shared transition starts.
        var previousVisibility = Visibility;
        try
        {
            if (previousVisibility == Visibility.Collapsed)
                Visibility = Visibility.Hidden;
            InvalidateMeasure();
            Measure(new Size(Surface.Width, double.PositiveInfinity));
            Arrange(new Rect(0, 0, Surface.Width, DesiredSize.Height));
            return new Size(Surface.Width, DesiredSize.Height);
        }
        finally
        {
            Visibility = previousVisibility;
        }
    }

    internal Rect PreviewBounds
    {
        get
        {
            var size = PreviewImage.RenderSize;
            if (_image == null || size.Width <= 0 || size.Height <= 0) return Rect.Empty;
            double scale = Math.Min(size.Width / _image.Width, size.Height / _image.Height);
            var fitted = new Rect((size.Width - _image.Width * scale) / 2,
                (size.Height - _image.Height * scale) / 2, _image.Width * scale, _image.Height * scale);
            return PreviewImage.TransformToAncestor(this).TransformBounds(fitted);
        }
    }

    internal void SetPreviewMorphing(bool morphing) => PreviewImage.Opacity = morphing ? 0 : 1;

    public void Present(BitmapSource image)
    {
        ++_presentationVersion;
        _image = image;
        _dragStart = null;
        PreviewImage.Source = image;
        TitleText.Text = Loc.Get("screenshot.title");
        DimensionsText.Text = $"{image.PixelWidth} × {image.PixelHeight}";
        HintText.Text = Loc.Get("screenshot.drag");
        KeepButton.Content = Loc.Get("screenshot.keep");
        DismissButton.ToolTip = Loc.Get("screenshot.dismiss");
        System.Windows.Automation.AutomationProperties.SetName(DismissButton, Loc.Get("screenshot.dismiss"));
        System.Windows.Automation.AutomationProperties.SetName(PreviewImage, Loc.Get("screenshot.drag"));
        Surface.BeginAnimation(OpacityProperty, null);
        EntryScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        EntryScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EntryOffset.BeginAnimation(TranslateTransform.YProperty, null);
        Surface.Opacity = 1;
        EntryScale.ScaleX = EntryScale.ScaleY = 1;
        EntryOffset.Y = 0;
    }

    private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PreviewImage.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);

    public void Clear()
    {
        ++_presentationVersion;
        _image = null;
        _dragStart = null;
        PreviewFrame.ReleaseMouseCapture();
        PreviewImage.Source = null;
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        KeepButton.IsEnabled = DismissButton.IsEnabled = !busy;
    }

    private async void Keep_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null || IsBusy || KeepRequested == null) return;
        var image = _image;
        int version = _presentationVersion;
        bool kept = false;
        SetBusy(true);
        try
        {
            kept = await KeepRequested(image);
            if (!kept && version == _presentationVersion) HintText.Text = Loc.Get("screenshot.full");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            if (version == _presentationVersion) HintText.Text = Loc.Get("screenshot.error");
        }
        finally { SetBusy(false); }
        // The host can collapse/release ownership only after the operation has settled.
        if (kept && version == _presentationVersion) DismissRequested?.Invoke();
    }

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsBusy) return;
        _dragStart = PointToScreen(e.GetPosition(this));
        PreviewFrame.CaptureMouse();
        e.Handled = true;
    }

    private async void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = null; return; }
        if (_dragStart is not Point start || _image == null || IsBusy) return;
        Point current = PointToScreen(e.GetPosition(this));
        if (!HasDragDistance(start, current, this)) return;
        _dragStart = null;
        PreviewFrame.ReleaseMouseCapture();
        await DragImageAsync(PreviewImage);
    }

    public async Task DragImageAsync(FrameworkElement source)
    {
        if (_image == null || IsBusy) return;
        var image = _image;
        int version = _presentationVersion;
        SetBusy(true);
        double sourceOpacity = source.Opacity;
        bool dropped = false;
        bool failed = false;
        try
        {
            string path = await Task.Run(() => _store.Save(image, keep: false));
            if (version != _presentationVersion || Mouse.LeftButton != MouseButtonState.Pressed || !source.IsVisible) return;
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });
            data.SetData(DataFormats.Bitmap, image);
            using var preview = new ScreenshotDragPreview(image, source);
            source.Opacity = 0.35;
            GiveFeedbackEventHandler feedback = (_, _) => preview.UpdatePosition();
            source.GiveFeedback += feedback;
            DragDropEffects result;
            try
            {
                IsDragging = true;
                result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
            }
            finally
            {
                IsDragging = false;
                source.GiveFeedback -= feedback;
            }
            dropped = result != DragDropEffects.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            failed = true;
            if (version == _presentationVersion) HintText.Text = Loc.Get("screenshot.error");
        }
        finally
        {
            source.Opacity = sourceOpacity;
            SetBusy(false);
            if (version == _presentationVersion)
            {
                if (dropped) DismissRequested?.Invoke();
                else if (!failed) DragCancelled?.Invoke();
            }
        }
    }

    internal static bool HasDragDistance(Point start, Point current, Visual source)
    {
        var dpi = VisualTreeHelper.GetDpi(source);
        return Math.Abs(current.X - start.X) >= Math.Max(10, SystemParameters.MinimumHorizontalDragDistance) * dpi.DpiScaleX ||
               Math.Abs(current.Y - start.Y) >= Math.Max(10, SystemParameters.MinimumVerticalDragDistance) * dpi.DpiScaleY;
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();
    private void Tray_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || IsBusy) return;
        DismissRequested?.Invoke();
        e.Handled = true;
    }
}
