using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private ScreenshotCaptureService? _screenshotCapture;
    private ScreenshotFolderService? _screenshotFolders;
    private ScreenshotTray? _screenshotTray;
    private Grid? _screenshotHost;
    private Grid? _screenshotCompact;
    private Image? _screenshotOutgoingContent;
    private int _screenshotHandoffVersion;
    private System.Windows.Shapes.Rectangle? _screenshotThumbnail;
    private RotateTransform? _screenshotSpinner;
    private BitmapSource? _activeScreenshot;
    private int _screenshotSlotToken;

    private readonly ScaleTransform _screenshotThumbnailScale = new();
    private Point? _screenshotDragStart;

    private bool IsScreenshotPillActive => _screenshotSlotToken != 0;
    private DispatcherTimer? _screenshotTimer;
    private BitmapSource? _pendingScreenshot;
    private string? _pendingScreenshotHash;
    private string? _activeScreenshotHash;
    private DateTime _pendingScreenshotAt;
    private DateTime _screenshotExpiresAt;
    private bool _screenshotClosing;
    private bool _screenshotReturnPending;

    private readonly ScreenshotFileStore _screenshotStore = new();
    private readonly ScreenshotContentDeduplicator _screenshotDeduplicator = new();
    private int _screenshotReadToken;
    private int _lastAcceptedScreenshotReadToken;

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
            hash = await Task.Run(() => ScreenshotContentDeduplicator.Fingerprint(image));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        { return; }
        if (_cleanedUp || !_settings.EnableScreenshotTray || !IsEffectivelyNotchVisible) return;
        // A duplicate finishing later must not invalidate an earlier distinct capture.
        bool fresh = _screenshotDeduplicator.TryAccept(hash, DateTime.UtcNow);
        if (!fresh || hash == _activeScreenshotHash || hash == _pendingScreenshotHash ||
            token <= _lastAcceptedScreenshotReadToken) return;
        _lastAcceptedScreenshotReadToken = token;
        _pendingScreenshot = image;
        _pendingScreenshotHash = hash;
        _pendingScreenshotAt = DateTime.UtcNow;
        _screenshotTimer?.Start();
        ScreenshotTimer_Tick(null, EventArgs.Empty);
    }

    private void ScreenshotTimer_Tick(object? sender, EventArgs e)
    {
        if (_cleanedUp || !_settings.EnableScreenshotTray || !IsEffectivelyNotchVisible || ShouldStayOnDesktopLayer)
        {
            _pendingScreenshot = null;
            _pendingScreenshotHash = null;
            CloseScreenshotTray(immediate: true);
            _screenshotTimer?.Stop();
            return;
        }
        if (_screenshotTray?.IsBusy == true)
        {
            _screenshotExpiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.ScreenshotTrayDurationSeconds, 2, 120));
            return;
        }
        if (_screenshotReturnPending) ReturnScreenshotToWaiting();
        if (_pendingScreenshot != null && DateTime.UtcNow - _pendingScreenshotAt > TimeSpan.FromSeconds(15))
        {
            _pendingScreenshot = null;
            _pendingScreenshotHash = null;
        }
        if (IsScreenshotPillActive && !_screenshotClosing)
        {
            if (_screenshotHost!.IsMouseOver || _screenshotTray!.IsKeyboardFocusWithin)
            {
                _screenshotExpiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.ScreenshotTrayDurationSeconds, 2, 120));
            }
            else if (DateTime.UtcNow >= _screenshotExpiresAt)
                CloseScreenshotTray();
        }
        if (_pendingScreenshot != null && !_screenshotClosing && !_isExpanded && !_isAnimating &&
            !_isDraggingVolume && !_isDraggingProgress && !_isDraggingVolumeIndicator &&
            Mouse.LeftButton != MouseButtonState.Pressed &&
            _screenshotHost?.IsMouseOver != true && _screenshotTray?.IsKeyboardFocusWithin != true)
        {
            EnsureScreenshotSurface();
            // Capture before acquiring ownership: the arbiter immediately hides
            // the live media controls, but their last frame can fade out safely.
            var outgoingContent = CaptureScreenshotCompactHandoff();
            if (!IsScreenshotPillActive)
            {
                if (!TryAcquireCompactSlot(CompactPillSlot.Screenshot, out _screenshotSlotToken)) return;

                _hoverThumbnailDelayTimer.Stop();
                _hoverCollapseTimer.Stop();
                AnimateNotchHover(false);
            }
            _activeScreenshot = _pendingScreenshot;
            _activeScreenshotHash = _pendingScreenshotHash;
            _pendingScreenshot = null;
            _pendingScreenshotHash = null;
            _screenshotThumbnail!.Fill = new ImageBrush(_activeScreenshot) { Stretch = Stretch.UniformToFill };
            _screenshotTray!.Present(_activeScreenshot);
            _screenshotHost!.Visibility = Visibility.Visible;
            ShowScreenshotCompactContent();
            StartScreenshotCompactHandoff(outgoingContent);
            _screenshotExpiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.ScreenshotTrayDurationSeconds, 2, 120));
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
        _screenshotHost = new Grid
        {
            Visibility = Visibility.Collapsed,
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _screenshotHost.SizeChanged += (_, _) => UpdateNotchClip();
        Panel.SetZIndex(_screenshotHost, 1000);
        NotchContent.Children.Add(_screenshotHost);
        _screenshotOutgoingContent = new Image
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Stretch = Stretch.Fill
        };
        _screenshotHost.Children.Add(_screenshotOutgoingContent);
        _screenshotCompact = new Grid
        {
            Margin = new Thickness(12, 0, 12, 0),
            Height = _collapsedHeight,
            VerticalAlignment = VerticalAlignment.Top
        };
        _screenshotThumbnail = new System.Windows.Shapes.Rectangle
        {
            Width = 22,
            Height = 22,
            RadiusX = 6,
            RadiusY = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Match the moving overlay and expanded preview at the final handoff.
        // WPF's default low-quality downsampling otherwise changes sharpness here.
        RenderOptions.SetBitmapScalingMode(_screenshotThumbnail, BitmapScalingMode.HighQuality);

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
                OpenScreenshotFromClick();
        };
        _screenshotThumbnail.LostMouseCapture += (_, _) => _screenshotDragStart = null;
        _screenshotCompact.Children.Add(_screenshotThumbnail);
        var ring = new Grid
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(.5, .5)
        };
        ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), StrokeThickness = 2 });
        ring.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Stroke = UiPalette.PrimaryBrush,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 7, 20 },
            StrokeDashCap = PenLineCap.Round
        });
        _screenshotSpinner = new RotateTransform();
        ring.RenderTransform = _screenshotSpinner;
        _screenshotCompact.Children.Add(ring);
        _screenshotHost.Children.Add(_screenshotCompact);
        _screenshotTray = new ScreenshotTray
        {
            KeepRequested = KeepScreenshotAsync,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        _screenshotTray.DismissRequested += () => CloseScreenshotTray();
        _screenshotTray.DragCancelled += () => CloseScreenshotTray();
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

    private void OpenScreenshotFromClick()
    {
        if (!IsScreenshotPillActive || _screenshotClosing || _screenshotTray?.IsBusy == true) return;
        _screenshotReturnPending = false;
        _hoverCollapseTimer.Stop();
        _compactThumbnailHoverLeaveTimer.Stop();
        _transitionCoordinator.RequestView(VNotch.Models.NotchView.Media, "ScreenshotExplicitOpen");
    }

    private void ReturnScreenshotToWaiting()
    {
        if (!IsScreenshotPillActive || _screenshotClosing) return;
        // Keep the dismissal intent until compact is reached, including while a
        // drag/export is finishing. Late hover or mouse-up events cannot reopen it.
        _screenshotReturnPending = true;
        _screenshotDragStart = null;
        _screenshotThumbnail?.ReleaseMouseCapture();
        _screenshotExpiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.ScreenshotTrayDurationSeconds, 2, 120));
        _hoverThumbnailDelayTimer.Stop();
        _hoverCollapseTimer.Stop();
        _compactThumbnailHoverLeaveTimer.Stop();
        if (_screenshotTray?.IsBusy == true) return;
        if (_screenshotTray?.IsKeyboardFocusWithin == true) Keyboard.ClearFocus();
        // A request can still be queued at input priority before the visual flags
        // change. Supersede that opening request on the first outside click too.
        // The coordinator is authoritative: visual flags can already be compact
        // while a queued/reversed transition still owns the expanded view.
        bool requested = _transitionCoordinator.RequestCollapse("ScreenshotOutsideClick");
        if (!requested && !_transitionCoordinator.IsTransitionActive && !_screenshotMorphReturning)
        {
            if (_isCompactThumbnailHovered) SetCompactThumbnailHover(false);
            _screenshotReturnPending = false;
        }
    }

    private void LeaveScreenshotHover()
    {
        if (!IsScreenshotPillActive || _screenshotClosing) return;
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
        if (!IsScreenshotPillActive || _screenshotClosing || _screenshotReturnPending || _screenshotMorphReturning || _isExpanded || _isAnimating ||
            _isCompactThumbnailHovered || Mouse.LeftButton == MouseButtonState.Pressed) return;
        SetCompactThumbnailHover(true);
    }

    private void ShowScreenshotCompactContent()
    {
        if (_screenshotMorphReturning && _screenshotMorphRunning)
        {
            _screenshotCompactHandoffPending = true;
            return;
        }
        bool returningFromPreview = _screenshotMorphReturning;
        AlignScreenshotCompactThumbnail();
        _screenshotReturnPending = false;
        EndScreenshotThumbnailMorph();
        ClearScreenshotCompactHandoff();
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
        _screenshotCompact.RenderTransform = null;
        if (!AnimationConfig.ReduceMotion && !returningFromPreview)
            AnimateScreenshotContentArrival(_screenshotCompact);
        _screenshotCompact.IsHitTestVisible = true;
        _screenshotSpinner!.BeginAnimation(RotateTransform.AngleProperty, null);
        if (!AnimationConfig.ReduceMotion)
            _screenshotSpinner.BeginAnimation(RotateTransform.AngleProperty, WithFps(new DoubleAnimation(0, 360,
                TimeSpan.FromMilliseconds(1600))
            { RepeatBehavior = RepeatBehavior.Forever }));
        EnforceCompactPresentationOwner();
    }

    private BitmapSource? CaptureScreenshotCompactHandoff()
    {
        if (AnimationConfig.ReduceMotion || _cleanedUp) return null;
        var size = NotchContent.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return null;
        var dpi = VisualTreeHelper.GetDpi(NotchContent);
        double width = Math.Ceiling(size.Width * dpi.DpiScaleX);
        double height = Math.Ceiling(size.Height * dpi.DpiScaleY);
        if (!double.IsFinite(width * height) || width * height > 4_000_000) return null;
        try
        {
            var snapshot = new RenderTargetBitmap((int)width, (int)height,
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            snapshot.Render(NotchContent);
            snapshot.Freeze();
            return snapshot;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private void StartScreenshotCompactHandoff(BitmapSource? snapshot)
    {
        ClearScreenshotCompactHandoff();
        if (snapshot == null || AnimationConfig.ReduceMotion) return;
        int version = _screenshotHandoffVersion;
        var outgoing = _screenshotOutgoingContent!;
        outgoing.Source = snapshot;
        outgoing.Width = snapshot.Width;
        outgoing.Height = snapshot.Height;
        outgoing.Opacity = 1;
        outgoing.Visibility = Visibility.Visible;
        // Fade both layers on the same accelerating/decelerating curve so the
        // old content remains present while the screenshot gently picks up speed.
        var fade = MakeScreenshotEntrance(1, 0);
        fade.Completed += (_, _) =>
        {
            if (version == _screenshotHandoffVersion) ClearScreenshotCompactHandoff();
        };
        outgoing.BeginAnimation(OpacityProperty, fade);
    }

    private void ClearScreenshotCompactHandoff()
    {
        ++_screenshotHandoffVersion;
        if (_screenshotOutgoingContent == null) return;
        _screenshotOutgoingContent.BeginAnimation(OpacityProperty, null);
        _screenshotOutgoingContent.Opacity = 0;
        _screenshotOutgoingContent.Visibility = Visibility.Collapsed;
        _screenshotOutgoingContent.Source = null;
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
        _compactThumbnailHoverLeaveTimer.Stop();
        _screenshotDragStart = null;
        _screenshotThumbnail?.ReleaseMouseCapture();
        if (_screenshotTray?.IsKeyboardFocusWithin == true) Keyboard.ClearFocus();
        _screenshotHost!.IsHitTestVisible = false;
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

    private static DoubleAnimation MakeScreenshotEntrance(double from, double to) =>
        MakeAnim(from, to, _dur500, _easeAppleInOut);

    private static void AnimateScreenshotContentArrival(FrameworkElement content)
    {
        // Keep the notification slide, with a gradual start rather than the
        // fast initial velocity used by Bluetooth/charging feedback.
        var translate = new TranslateTransform(0, 6);
        content.RenderTransform = translate;
        content.Opacity = 0;
        content.BeginAnimation(OpacityProperty, MakeScreenshotEntrance(0, 1));
        translate.BeginAnimation(TranslateTransform.YProperty, MakeScreenshotEntrance(6, 0));
    }

    private void FinishScreenshotDismissal(bool animate = true)
    {
        _screenshotReturnPending = false;
        EndScreenshotThumbnailMorph();
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
            ClearScreenshotCompactHandoff();
            _screenshotHost.Visibility = Visibility.Collapsed;
            _screenshotHost.BeginAnimation(OpacityProperty, null);
            _screenshotHost.Opacity = 1;
            _screenshotHost.RenderTransform = null;
            _screenshotHost.IsHitTestVisible = true;
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(_screenshotCompact!);
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(_screenshotTray!);
            _screenshotTray!.Clear();
            _screenshotThumbnail!.Fill = null;
            _activeScreenshot = null;
            _activeScreenshotHash = null;
            _isCompactThumbnailHovered = false;
            _screenshotThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _screenshotThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _screenshotThumbnailScale.ScaleX = _screenshotThumbnailScale.ScaleY = 1;
            _screenshotCompact!.BeginAnimation(HeightProperty, null);
            _screenshotCompact.Height = _collapsedHeight;

            // Settle the image layers while screenshot ownership still hides media.
            // Resetting only the border leaves a paused morph transparent/blurred,
            // and screenshot collapse skips the normal pending-thumbnail handoff.
            CancelThumbnailSwitchAnimations(_pendingFlipThumbnail);
            _pendingFlipThumbnail = null;
            ResetAnimationThumbnailOverlay();
            // Re-measure the actual media layout on its next expansion; the tray
            // can have a different width/height from the last media surface.
            _cachedThumbnailExpandTarget = null;
            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 1;
            _compactPillArbiter.Release(token);
            _screenshotSlotToken = 0;
            _screenshotClosing = false;

            MusicCompactContent.BeginAnimation(OpacityProperty, null);
            CollapsedContent.BeginAnimation(OpacityProperty, null);
            MusicCompactContent.Opacity = CollapsedContent.Opacity = 1;
            MusicCompactContent.RenderTransform = CollapsedContent.RenderTransform = null;
            MusicCompactContentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
            CollapsedContentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
            MusicCompactContentBlur.Radius = CollapsedContentBlur.Radius = 0;
            ResetCompactThumbnailRestingState();
            HideCompactSurface(CompactHoverInfo);
            MusicCompactContent.Visibility = _isMusicCompactMode ? Visibility.Visible : Visibility.Collapsed;
            CollapsedContent.Visibility = _isMusicCompactMode ? Visibility.Collapsed : Visibility.Visible;
            RestoreCompactMediaPresentation();
            RestorePrivacyDotVisibility();
            if (animate && !_isExpanded && !_isAnimating && _compactPillArbiter.ActiveSlot == CompactPillSlot.None)
            {
                FrameworkElement compact = _isMusicCompactMode ? MusicCompactContent : CollapsedContent;
                compact.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
            }
            EnforceCompactPresentationOwner();
            if (!_cleanedUp && _pendingScreenshot != null) _screenshotTimer?.Start();
            else _screenshotTimer?.Stop();
        }

        if (!animate || (_screenshotCompact!.Visibility != Visibility.Visible &&
                         _screenshotTray!.Visibility != Visibility.Visible))
        {
            CompleteDismissal();
            return;
        }

        var exitTranslate = new TranslateTransform();
        _screenshotHost.RenderTransform = exitTranslate;
        exitTranslate.BeginAnimation(TranslateTransform.YProperty, MakeCompactNotificationExit(0, -4));
        var fadeOut = MakeCompactNotificationExit(_screenshotHost.Opacity, 0);
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
        _pendingScreenshotHash = null;
        CloseScreenshotTray(immediate: true);
    }
}
