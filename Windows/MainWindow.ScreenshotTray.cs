using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using static VNotch.Services.AnimationPrimitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private ScreenshotCaptureService? _screenshotCapture;
    private ScreenshotFolderService? _screenshotFolders;
    private ScreenshotTray? _screenshotTray;
    private Grid? _screenshotHost;
    private Grid? _screenshotCompact;
    private Image? _screenshotThumbnail;
    private RotateTransform? _screenshotSpinner;
    private BitmapSource? _activeScreenshot;
    private int _screenshotSlotToken;

    private bool _screenshotWasBusy;
    private readonly ScaleTransform _screenshotThumbnailScale = new();
    private Point? _screenshotDragStart;
    private bool _screenshotWaitingAfterInteraction;

    private bool IsScreenshotPillActive => _screenshotSlotToken != 0;
    private DispatcherTimer? _screenshotTimer;
    private BitmapSource? _pendingScreenshot;
    private DateTime _pendingScreenshotAt;
    private DateTime _screenshotExpiresAt;
    private bool _screenshotClosing;

    private readonly ScreenshotFileStore _screenshotStore = new();
    private string? _lastScreenshotHash;
    private DateTime _lastScreenshotAt;
    private int _screenshotReadToken;

    private void InitializeScreenshotTray()
    {
        _screenshotCapture = new ScreenshotCaptureService();
        _screenshotCapture.Captured += QueueScreenshot;
        _screenshotFolders = new ScreenshotFolderService(() => _settings.EnableScreenshotTray,
            () => _settings.ScreenshotFolders ?? string.Empty);
        _screenshotFolders.Captured += QueueScreenshot;
        _screenshotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _screenshotTimer.Tick += ScreenshotTimer_Tick;
        _ = Task.Run(_screenshotStore.CleanExpiredExports);
    }

    private void HandleScreenshotClipboardUpdated()
    {
        // Observe every clipboard sequence, before the normal peek's cooldown.
        if (_settings.EnableScreenshotTray && IsEffectivelyNotchVisible &&
            _screenshotCapture?.NotifyClipboardUpdated() == true) return;
        _clipboardListener.NotifyClipboardUpdated();
    }

    private async void QueueScreenshot(BitmapSource image)
    {
        if (_cleanedUp || !_settings.EnableScreenshotTray || !IsEffectivelyNotchVisible) return;
        if ((long)image.PixelWidth * image.PixelHeight > 64_000_000) return;
        int token = ++_screenshotReadToken;
        string hash;
        try
        {
            hash = await Task.Run(() =>
            {
                var normalized = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int stride = checked(normalized.PixelWidth * 4);
                var pixels = new byte[checked(stride * normalized.PixelHeight)];
                normalized.CopyPixels(pixels, stride, 0);
                return $"{image.PixelWidth}:{image.PixelHeight}:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        { return; }
        if (_cleanedUp || token != _screenshotReadToken || !_settings.EnableScreenshotTray || !IsEffectivelyNotchVisible) return;
        // Snipping Tool may publish the same capture to both clipboard and disk.
        if (hash == _lastScreenshotHash && DateTime.UtcNow - _lastScreenshotAt < TimeSpan.FromSeconds(5)) return;
        _lastScreenshotHash = hash;
        _lastScreenshotAt = DateTime.UtcNow;
        _pendingScreenshot = image;
        _pendingScreenshotAt = DateTime.UtcNow;
        _screenshotTimer?.Start();
        ScreenshotTimer_Tick(null, EventArgs.Empty);
    }

    private void ScreenshotTimer_Tick(object? sender, EventArgs e)
    {
        if (_cleanedUp || !_settings.EnableScreenshotTray || !IsEffectivelyNotchVisible || ShouldStayOnDesktopLayer)
        {
            _pendingScreenshot = null;
            CloseScreenshotTray(immediate: true);
            _screenshotTimer?.Stop();
            return;
        }
        if (_screenshotTray?.IsBusy == true)
        {
            _screenshotWasBusy = true;
            _screenshotExpiresAt = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        if (_screenshotWasBusy)
        {
            _screenshotWasBusy = false;
            if (IsScreenshotPillActive && _screenshotHost?.IsMouseOver != true) LeaveScreenshotHover();
        }
        if (_pendingScreenshot != null && DateTime.UtcNow - _pendingScreenshotAt > TimeSpan.FromSeconds(15))
            _pendingScreenshot = null;
        if (IsScreenshotPillActive && !_screenshotClosing)
        {
            if (_screenshotHost!.IsMouseOver || _screenshotTray!.IsKeyboardFocusWithin)
            {
                _screenshotExpiresAt = DateTime.UtcNow.AddSeconds(8);
            }
            else if (!_screenshotWaitingAfterInteraction && DateTime.UtcNow >= _screenshotExpiresAt)
                CloseScreenshotTray();
        }
        if (_pendingScreenshot != null && !_screenshotClosing && !_isExpanded && !_isAnimating &&
            !_isDraggingVolume && !_isDraggingProgress && !_isDraggingVolumeIndicator &&
            Mouse.LeftButton != MouseButtonState.Pressed &&
            _screenshotHost?.IsMouseOver != true && _screenshotTray?.IsKeyboardFocusWithin != true)
        {
            EnsureScreenshotSurface();
            if (!IsScreenshotPillActive)
            {
                if (!TryAcquireCompactSlot(CompactPillSlot.Screenshot, out _screenshotSlotToken)) return;

                _hoverThumbnailDelayTimer.Stop();
                _hoverCollapseTimer.Stop();
                AnimateNotchHover(false);
            }
            _activeScreenshot = _pendingScreenshot;
            _screenshotWaitingAfterInteraction = false;
            _pendingScreenshot = null;
            _screenshotThumbnail!.Source = _activeScreenshot;
            _screenshotTray!.Present(_activeScreenshot);
            _screenshotHost!.Visibility = Visibility.Visible;
            ShowScreenshotCompactContent();
            _screenshotExpiresAt = DateTime.UtcNow.AddSeconds(8);
            SuppressPrivacyDot();
        }
        if (_pendingScreenshot == null && !IsScreenshotPillActive) _screenshotTimer?.Stop();
    }

    private void EnsureScreenshotSurface()
    {
        if (_screenshotHost != null) return;
        // Intercept before shelf children or the wrapper can auto-open a view.
        NotchWrapper.PreviewDragEnter += SuppressScreenshotSelfDrop;
        NotchWrapper.PreviewDragOver += SuppressScreenshotSelfDrop;
        NotchWrapper.PreviewDragLeave += SuppressScreenshotSelfDrop;
        NotchWrapper.PreviewDrop += SuppressScreenshotSelfDrop;
        _screenshotHost = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent,
            ClipToBounds = true };
        _screenshotHost.SizeChanged += (_, _) => UpdateNotchClip();
        Panel.SetZIndex(_screenshotHost, 1000);
        NotchContent.Children.Add(_screenshotHost);
        _screenshotCompact = new Grid { Margin = new Thickness(12, 0, 12, 0),
            Height = _collapsedHeight, VerticalAlignment = VerticalAlignment.Top };
        _screenshotThumbnail = new Image { Width = 22, Height = 22, Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        _screenshotThumbnail.Clip = new RectangleGeometry(new Rect(0, 0, 22, 22), 6, 6);
        _screenshotThumbnail.RenderTransform = _screenshotThumbnailScale;
        _screenshotThumbnail.RenderTransformOrigin = new Point(0, 0.5);
        _screenshotThumbnail.MouseEnter += CompactThumbnailBorder_MouseEnter;
        _screenshotThumbnail.MouseLeave += CompactThumbnailBorder_MouseLeave;
        _screenshotThumbnail.MouseLeftButtonDown += (_, e) =>
        {
            if (_isAnimating || _screenshotClosing || _screenshotTray?.IsBusy == true) return;
            _screenshotDragStart = NotchBorder.PointToScreen(e.GetPosition(NotchBorder));
            _hoverThumbnailDelayTimer.Stop();
            _compactThumbnailHoverLeaveTimer.Stop();
            _screenshotThumbnail.CaptureMouse();
            e.Handled = true;
        };
        _screenshotThumbnail.MouseMove += async (_, e) =>
        {
            if (_screenshotDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var current = NotchBorder.PointToScreen(e.GetPosition(NotchBorder));
            if (!ScreenshotTray.HasDragDistance(start, current, NotchBorder)) return;
            _screenshotDragStart = null;
            _screenshotThumbnail.ReleaseMouseCapture();
            e.Handled = true;
            await _screenshotTray!.DragImageAsync(_screenshotThumbnail);
        };
        _screenshotThumbnail.MouseLeftButtonUp += (_, e) =>
        {
            var current = NotchBorder.PointToScreen(e.GetPosition(NotchBorder));
            bool click = _screenshotDragStart is Point start &&
                !ScreenshotTray.HasDragDistance(start, current, NotchBorder) &&
                new Rect(_screenshotThumbnail.RenderSize).Contains(e.GetPosition(_screenshotThumbnail));
            _screenshotDragStart = null;
            _screenshotThumbnail.ReleaseMouseCapture();
            e.Handled = true;
            if (click && _screenshotThumbnail.IsMouseOver && !_isAnimating && !_screenshotClosing)
                ExpandNotch();
        };
        _screenshotThumbnail.LostMouseCapture += (_, _) => _screenshotDragStart = null;
        _screenshotCompact.Children.Add(_screenshotThumbnail);
        var ring = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(.5, .5) };
        ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), StrokeThickness = 2 });
        ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = UiPalette.PrimaryBrush, StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 7, 20 }, StrokeDashCap = PenLineCap.Round });
        _screenshotSpinner = new RotateTransform();
        ring.RenderTransform = _screenshotSpinner;
        _screenshotCompact.Children.Add(ring);
        _screenshotHost.Children.Add(_screenshotCompact);
        _screenshotTray = new ScreenshotTray { KeepRequested = KeepScreenshotAsync,
            Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top };
        _screenshotTray.DismissRequested += () => CloseScreenshotTray();
        _screenshotHost.Children.Add(_screenshotTray);
        _screenshotHost.MouseEnter += (_, _) => _hoverCollapseTimer.Stop();
        _screenshotHost.MouseLeave += (_, _) => LeaveScreenshotHover();
    }

    private void SuppressScreenshotSelfDrop(object sender, DragEventArgs e)
    {
        if (_screenshotTray?.IsDragging != true) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ReturnScreenshotToWaiting()
    {
        if (!IsScreenshotPillActive || _screenshotClosing || _screenshotTray?.IsBusy == true) return;
        _screenshotWaitingAfterInteraction = true;
        _hoverThumbnailDelayTimer.Stop();
        _hoverCollapseTimer.Stop();
        if (_screenshotTray?.IsKeyboardFocusWithin == true) Keyboard.ClearFocus();
        if (_isExpanded || _isAnimating) CollapseNotch();
    }

    private void LeaveScreenshotHover()
    {
        _hoverThumbnailDelayTimer.Stop();
        if (!_isExpanded)
        {
            AnimateNotchHover(false);
            _compactThumbnailHoverLeaveTimer.Stop();
            _compactThumbnailHoverLeaveTimer.Start();
            return;
        }
        if (_settings.DisableMouseLeaveAutoClose || _screenshotTray?.IsBusy == true) return;
        _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelay);
        _hoverCollapseTimer.Start();
    }

    private void CollapseScreenshotPreview(bool fromClick = false)
    {
        if (_screenshotClosing || _screenshotTray?.IsBusy == true ||
            (!fromClick && (_screenshotTray?.IsKeyboardFocusWithin == true || Mouse.LeftButton == MouseButtonState.Pressed))) return;
        if (_isExpanded) CollapseNotch();
        else if (_isCompactThumbnailHovered && !_isAnimating)
        {
            SetCompactThumbnailHover(false);
        }
    }

    private void ExpandScreenshotPreview()
    {
        if (!IsScreenshotPillActive || _screenshotClosing || _isExpanded || _isAnimating ||
            _isCompactThumbnailHovered || Mouse.LeftButton == MouseButtonState.Pressed) return;
        SetCompactThumbnailHover(true);
    }

    private void ShowScreenshotCompactContent()
    {
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(_screenshotTray!);
        _isCompactThumbnailHovered = false;
        _compactThumbnailHoverLeaveTimer.Stop();
        _screenshotThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _screenshotThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _screenshotThumbnailScale.ScaleX = _screenshotThumbnailScale.ScaleY = 1;
        _screenshotCompact!.BeginAnimation(HeightProperty, null);
        _screenshotCompact.Height = _collapsedHeight;
        _screenshotCompact.BeginAnimation(OpacityProperty, null);
        _screenshotCompact.Visibility = Visibility.Visible;
        _screenshotCompact.Opacity = 1;
        if (!AnimationConfig.ReduceMotion)
            _screenshotCompact.BeginAnimation(OpacityProperty, MakeAnim(0, 1, _dur200, _easeQuadOut));
        _screenshotCompact.IsHitTestVisible = true;
        _screenshotSpinner!.BeginAnimation(RotateTransform.AngleProperty, null);
        if (!AnimationConfig.ReduceMotion)
            _screenshotSpinner.BeginAnimation(RotateTransform.AngleProperty, WithFps(new DoubleAnimation(0, 360,
                TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever }));
        EnforceCompactPresentationOwner();
    }
    private async Task<bool> KeepScreenshotAsync(BitmapSource image)
    {
        if (_fileShelf.IsFull) return false;
        string path = await Task.Run(() => _screenshotStore.Save(image, keep: true));
        if (_cleanedUp || _fileShelf.IsFull)
        {
            File.Delete(path);
            return false;
        }
        _fileShelf.EnqueueFiles(new[] { path });
        return true;
    }

    private void CloseScreenshotTray(bool immediate = false)
    {
        if (!IsScreenshotPillActive || (_screenshotClosing && !immediate)) return;
        if (_isCompactThumbnailHovered && !_isExpanded && !_isAnimating)
        {
            SetCompactThumbnailHover(false);
        }
        _screenshotClosing = true;
        _hoverThumbnailDelayTimer.Stop();
        _hoverCollapseTimer.Stop();
        _screenshotSpinner?.BeginAnimation(RotateTransform.AngleProperty, null);
        if (!_cleanedUp && (_isExpanded || _isAnimating))
        {
            // Let the normal coordinator settle the shell before releasing the content.
            CollapseNotch();
            return;
        }
        FinishScreenshotDismissal(animate: !immediate && !_cleanedUp);
    }

    private int _screenshotDismissVersion;

    private void FinishScreenshotDismissal(bool animate = true)
    {
        int version = ++_screenshotDismissVersion;
        int token = _screenshotSlotToken;
        animate &= !AnimationConfig.ReduceMotion && !_cleanedUp;
        _screenshotHost!.IsHitTestVisible = false;
        // Keep ownership until the outgoing layer is completely hidden. Restoring
        // compact content during this fade causes two layouts to occupy the pill.
        EnforceCompactPresentationOwner();

        void CompleteDismissal()
        {
            if (version != _screenshotDismissVersion || token != _screenshotSlotToken) return;
            _screenshotHost.Visibility = Visibility.Collapsed;
            _screenshotHost.BeginAnimation(OpacityProperty, null);
            _screenshotHost.Opacity = 1;
            _screenshotHost.IsHitTestVisible = true;
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(_screenshotCompact!);
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(_screenshotTray!);
            _screenshotTray!.Clear();
            _screenshotThumbnail!.Source = null;
            _activeScreenshot = null;
            _compactPillArbiter.Release(token);
            _screenshotSlotToken = 0;
            _screenshotClosing = false;

            MusicCompactContent.BeginAnimation(OpacityProperty, null);
            CollapsedContent.BeginAnimation(OpacityProperty, null);
            MusicCompactContent.Opacity = CollapsedContent.Opacity = 1;
            MusicCompactContent.Visibility = _isMusicCompactMode ? Visibility.Visible : Visibility.Collapsed;
            CollapsedContent.Visibility = _isMusicCompactMode ? Visibility.Collapsed : Visibility.Visible;
            RestoreCompactMediaPresentation();
            RestorePrivacyDotVisibility();
            if (animate && !_isExpanded && !_isAnimating && _compactPillArbiter.ActiveSlot == CompactPillSlot.None)
            {
                FrameworkElement compact = _isMusicCompactMode ? MusicCompactContent : CollapsedContent;
                compact.BeginAnimation(OpacityProperty, MakeAnim(0, 1, _dur200, _easePowerOut3));
            }
            EnforceCompactPresentationOwner();
            if (!_cleanedUp && _pendingScreenshot != null) _screenshotTimer?.Start();
        }

        if (!animate || (_screenshotCompact!.Visibility != Visibility.Visible &&
                         _screenshotTray!.Visibility != Visibility.Visible))
        {
            CompleteDismissal();
            return;
        }

        var fadeOut = MakeAnim(_screenshotHost.Opacity, 0, _dur200, _easeQuadOut);
        fadeOut.Completed += (_, _) => CompleteDismissal();
        _screenshotHost.BeginAnimation(OpacityProperty, fadeOut);
    }
    private void DisposeScreenshotTray()
    {
        NotchWrapper.PreviewDragEnter -= SuppressScreenshotSelfDrop;
        NotchWrapper.PreviewDragOver -= SuppressScreenshotSelfDrop;
        NotchWrapper.PreviewDragLeave -= SuppressScreenshotSelfDrop;
        NotchWrapper.PreviewDrop -= SuppressScreenshotSelfDrop;
        _screenshotCapture?.Dispose();
        _screenshotFolders?.Dispose();
        _screenshotTimer?.Stop();
        if (_screenshotTimer != null) _screenshotTimer.Tick -= ScreenshotTimer_Tick;
        _pendingScreenshot = null;
        CloseScreenshotTray(immediate: true);
    }
}
