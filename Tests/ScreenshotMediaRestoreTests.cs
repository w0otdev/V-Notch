using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ScreenshotMediaRestoreTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ScreenshotOutsideClickClosesCoordinatorViewEvenWithStaleVisualFlags() => WithWindow(window =>
    {
        object Field(string name) => typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
        void Invoke(string name) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, null);
        Invoke("EnsureScreenshotSurface");
        var arbiter = (CompactPillArbiter)Field("_compactPillArbiter");
        typeof(MainWindow).GetField("_screenshotSlotToken", PrivateInstance)!.SetValue(window,
            arbiter.TryAcquire(CompactPillSlot.Screenshot).Token);
        var coordinator = (NotchTransitionCoordinator)Field("_transitionCoordinator");
        for (int cycle = 0; cycle < 30; cycle++)
        {
            Assert.True(coordinator.RequestView(NotchView.Media, "SpamOpen"));
            coordinator.CompleteTransition(coordinator.ActiveTransitionId);
            Invoke("ReturnScreenshotToWaiting");
            Assert.True(coordinator.IsTransitionActive);
            long collapse = coordinator.ActiveTransitionId;
            Invoke("ReturnScreenshotToWaiting");
            Assert.Equal(collapse, coordinator.ActiveTransitionId);
            Assert.False(coordinator.RequestView(NotchView.Media, "LateOpen"));
            Invoke("OpenScreenshotFromClick");
            Assert.True(coordinator.ActiveTransitionId > collapse);
            Invoke("ReturnScreenshotToWaiting");
            collapse = coordinator.ActiveTransitionId;
            coordinator.CompleteTransition(collapse);
            Invoke("ShowScreenshotCompactContent");
        }
    });

    [Theory]
    [InlineData(-1, 100, false)]
    [InlineData(305, 100, false)]
    [InlineData(150, 241, false)]
    [InlineData(1, 239, false)]
    [InlineData(303, 239, false)]
    [InlineData(150, 120, true)]
    [InlineData(1, 100, true)]
    public void ScreenshotOutsideClickUsesVisibleShellWithoutHoverPadding(double x, double y, bool inside)
    {
        Assert.Equal(inside, MainWindow.ScreenshotShellContains(new Point(x, y),
            new Size(304, 240), new CornerRadius(0, 0, 20, 20)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScreenshotRestSlotMatchesMediaThumbnail(bool islandMode) => WithWindow(window =>
    {
        object Field(string name) => typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
        var settings = (NotchSettings)Field("_settings");
        settings.EnableDynamicIslandMode = islandMode;
        typeof(MainWindow).GetMethod("EnsureScreenshotSurface", PrivateInstance)!.Invoke(window, null);
        typeof(MainWindow).GetMethod("ApplyDynamicIslandContentAlignment", PrivateInstance)!.Invoke(window, [islandMode]);
        var compact = (System.Windows.Controls.Grid)Field("_screenshotCompact");
        var thumbnail = (System.Windows.Shapes.Rectangle)Field("_screenshotThumbnail");
        var target = (Rect)typeof(MainWindow).GetMethod("GetScreenshotCompactThumbnailBounds", PrivateInstance)!.Invoke(window, null)!;
        Assert.Equal(window.MusicCompactContent.Margin.Left, compact.Margin.Left);
        Assert.Equal(window.MusicCompactContent.Margin.Top, thumbnail.Margin.Top);
        Assert.Equal(compact.Margin.Left, target.X);
        Assert.Equal(thumbnail.Margin.Top, target.Y);
        Assert.Equal(VerticalAlignment.Top, thumbnail.VerticalAlignment);
    });

    [Theory]
    [InlineData(80, 40)]
    [InlineData(40, 80)]
    [InlineData(60, 60)]
    public void CompactScreenshotMatchesMovingImagePixels(int width, int height) => WithWindow(window =>
    {
        typeof(MainWindow).GetMethod("EnsureScreenshotSurface", PrivateInstance)!.Invoke(window, null);
        var thumb = (System.Windows.Shapes.Rectangle)typeof(MainWindow).GetField("_screenshotThumbnail", PrivateInstance)!.GetValue(window)!;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 3);
                pixels[i + 1] = (byte)(y * 6);
                pixels[i + 3] = 255;
            }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        thumb.Fill = new ImageBrush(source) { Stretch = Stretch.UniformToFill };
        var moving = new System.Windows.Shapes.Rectangle
        {
            Width = 22,
            Height = 22,
            RadiusX = 6,
            RadiusY = 6,
            Fill = new ImageBrush(source) { Stretch = Stretch.UniformToFill }
        };
        RenderOptions.SetBitmapScalingMode(moving, BitmapScalingMode.HighQuality);
        byte[] Render(FrameworkElement visual)
        {
            visual.Measure(new Size(22, 22));
            visual.Arrange(new Rect(0, 0, 22, 22));
            var bitmap = new RenderTargetBitmap(22, 22, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var output = new byte[22 * 22 * 4];
            bitmap.CopyPixels(output, 88, 0);
            return output;
        }
        Assert.Equal(Render(moving), Render(thumb));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstOutsideClickClosesHoveredOrQueuedScreenshot(bool queuedOpening) => WithWindow(window =>
    {
        void Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, args);
        object Field(string name) => typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
        Invoke("EnsureScreenshotSurface");
        window.NotchBorder.Width = 300;
        window.NotchBorder.Height = 40;
        var arbiter = (CompactPillArbiter)Field("_compactPillArbiter");
        typeof(MainWindow).GetField("_screenshotSlotToken", PrivateInstance)!.SetValue(window,
            arbiter.TryAcquire(CompactPillSlot.Screenshot).Token);
        var coordinator = (NotchTransitionCoordinator)Field("_transitionCoordinator");
        if (queuedOpening)
        {
            coordinator.RequestView(NotchView.Media, "TestScreenshotOpen");
            NotchView? next = null;
            coordinator.TransitionRequested += (_, request) => next = request.TargetView;
            Invoke("ReturnScreenshotToWaiting");
            Assert.Equal(NotchView.Compact, next);
            long closingTransition = coordinator.ActiveTransitionId;
            for (int click = 0; click < 20; click++)
            {
                Assert.False(coordinator.RequestView(NotchView.Media, "LateScreenshotClick"));
                Invoke("ReturnScreenshotToWaiting");
                Assert.Equal(closingTransition, coordinator.ActiveTransitionId);
            }
            Invoke("ShowScreenshotCompactContent");
            Assert.True(coordinator.RequestView(NotchView.Media, "NewScreenshotClick"));
        }
        else
        {
            typeof(MainWindow).GetField("_isCompactThumbnailHovered", PrivateInstance)!.SetValue(window, true);
            // An unshown window has no screen bounds: this is an outside click.
            Invoke("GlobalMouseHook_MouseLeftButtonDown", null, new InputMonitorService.POINT { x = -10000, y = -10000 });
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Assert.False((bool)Field("_isCompactThumbnailHovered"));
        }
        Assert.Equal(CompactPillSlot.Screenshot, arbiter.ActiveSlot);
    });

    [Theory]
    [InlineData(400, 100)]
    [InlineData(100, 400)]
    [InlineData(200, 200)]
    public void ScreenshotMorphPreservesAspectAndRestoresPreviewWhenInterrupted(int width, int height)
        => WithWindow(window =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            void Invoke(string name) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, null);
            object Field(string name) => typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
            Invoke("EnsureScreenshotSurface");
            var arbiter = (CompactPillArbiter)Field("_compactPillArbiter");
            typeof(MainWindow).GetField("_screenshotSlotToken", PrivateInstance)!.SetValue(window,
                arbiter.TryAcquire(CompactPillSlot.Screenshot).Token);
            var capture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                new byte[width * height * 4], width * 4);
            typeof(MainWindow).GetField("_activeScreenshot", PrivateInstance)!.SetValue(window, capture);
            var tray = (VNotch.Controls.ScreenshotTray)Field("_screenshotTray");
            tray.Present(capture);
            ((System.Windows.Shapes.Rectangle)Field("_screenshotThumbnail")).Fill = new ImageBrush(capture) { Stretch = Stretch.UniformToFill };
            Invoke("ShowScreenshotCompactContent");
            var host = (System.Windows.Controls.Grid)Field("_screenshotHost");
            host.Visibility = Visibility.Visible;
            host.Measure(new Size(300, 40));
            host.Arrange(new Rect(0, 0, 300, 40));
            tray.ConfigureInline();
            var target = tray.PreviewBounds;
            Assert.Equal((double)width / height, target.Width / target.Height, 5);
            Assert.InRange(target.Left, 0, 304 - target.Width);
            Invoke("BeginScreenshotThumbnailExpand");
            var overlay = (System.Windows.Shapes.Rectangle)Field("_screenshotMorph");
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Same(capture, ((ImageBrush)overlay.Fill).ImageSource);
            Assert.Equal(RenderOptions.GetBitmapScalingMode(overlay),
                RenderOptions.GetBitmapScalingMode((System.Windows.Shapes.Rectangle)Field("_screenshotThumbnail")));
            var preview = (System.Windows.Controls.Image)tray.FindName("PreviewImage");
            Assert.Equal(0, preview.Opacity);
            var liveOffset = (TranslateTransform)overlay.RenderTransform;
            var liveBounds = new Rect(liveOffset.X, liveOffset.Y, overlay.Width, overlay.Height);
            Invoke("BeginScreenshotThumbnailCollapse");
            var returningOffset = (TranslateTransform)overlay.RenderTransform;
            Assert.Equal(liveBounds.X, (double)returningOffset.GetAnimationBaseValue(TranslateTransform.XProperty));
            Assert.Equal(liveBounds.Y, (double)returningOffset.GetAnimationBaseValue(TranslateTransform.YProperty));
            Assert.Equal(1, overlay.Opacity);
            Assert.Equal(0, preview.Opacity);
            Assert.Equal(liveBounds.Width, (double)overlay.GetAnimationBaseValue(FrameworkElement.WidthProperty));
            Assert.Equal(liveBounds.Height, (double)overlay.GetAnimationBaseValue(FrameworkElement.HeightProperty));
            var returnWidth = (DoubleAnimation)Field("_cachedThumbWidthCollapse");
            Assert.Equal(TimeSpan.FromMilliseconds(650), returnWidth.Duration.TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(36), returnWidth.BeginTime);
            Assert.Equal(22, returnWidth.To);
            Invoke("ShowScreenshotCompactContent");
            Assert.True((bool)Field("_screenshotCompactHandoffPending"));
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            typeof(MainWindow).GetField("_screenshotMorphRunning", PrivateInstance)!.SetValue(window, false);
            Invoke("ShowScreenshotCompactContent");
            var compact = (System.Windows.Controls.Grid)Field("_screenshotCompact");
            Assert.Equal(1, compact.Opacity);
            Assert.Null(compact.RenderTransform);
            Invoke("EndScreenshotThumbnailMorph");
            Assert.Equal(1, preview.Opacity);
            Assert.Null(overlay.Fill);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            AnimationConfig.SetReduceMotion(true);
            Invoke("BeginScreenshotThumbnailExpand");
            Assert.Equal(1, preview.Opacity);
            Assert.Null(overlay.Fill);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void CompactHandoffKeepsAFrozenFrameAfterLiveContentChanges() => WithWindow(window =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            window.NotchContent.Background = Brushes.Red;
            window.NotchContent.Measure(new Size(300, 40));
            window.NotchContent.Arrange(new Rect(0, 0, 300, 40));
            var snapshot = Assert.IsAssignableFrom<BitmapSource>(typeof(MainWindow)
                .GetMethod("CaptureScreenshotCompactHandoff", PrivateInstance)!.Invoke(window, null));
            Assert.True(snapshot.IsFrozen);
            var before = new byte[snapshot.PixelWidth * snapshot.PixelHeight * 4];
            snapshot.CopyPixels(before, snapshot.PixelWidth * 4, 0);
            Assert.Contains((byte)255, before);
            window.NotchContent.Background = Brushes.Blue;
            window.NotchContent.Visibility = Visibility.Collapsed;
            var after = new byte[before.Length];
            snapshot.CopyPixels(after, snapshot.PixelWidth * 4, 0);
            Assert.Equal(before, after);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void CompactHandoffCanBeReplacedClearedAndSkippedForReducedMotion() => WithWindow(window =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, args);
            Invoke("EnsureScreenshotSurface");
            var first = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            var second = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
            var outgoing = (System.Windows.Controls.Image)typeof(MainWindow).GetField("_screenshotOutgoingContent", PrivateInstance)!.GetValue(window)!;
            Invoke("StartScreenshotCompactHandoff", first);
            Assert.Same(first, outgoing.Source);
            Assert.Equal(Visibility.Visible, outgoing.Visibility);
            Assert.False(outgoing.IsHitTestVisible);
            Invoke("StartScreenshotCompactHandoff", second);
            Assert.Same(second, outgoing.Source);
            Invoke("ClearScreenshotCompactHandoff");
            Assert.Null(outgoing.Source);
            Assert.Equal(Visibility.Collapsed, outgoing.Visibility);
            AnimationConfig.SetReduceMotion(true);
            Invoke("StartScreenshotCompactHandoff", first);
            Assert.Null(outgoing.Source);
            Assert.Equal(Visibility.Collapsed, outgoing.Visibility);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DismissalRestoresMediaImageAndSettlesInterruptedThumbnailAnimation(bool pendingUpdate)
        => WithWindow(window =>
    {
        void Set(string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
        void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, args);
        var oldImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        var newImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
        Set("_isMusicCompactMode", true);
        Set("_cachedThumbnailExpandTarget", (80d, 40d));
        window.ThumbnailImage.Source = window.CompactThumbnail.Source = oldImage;
        if (pendingUpdate) Set("_pendingFlipThumbnail", newImage);
        else
        {
            Set("_isThumbnailSwitchActive", true);
            window.ThumbnailImageNext.Source = window.CompactThumbnailNext.Source = newImage;
            window.CompactThumbnailNext.Visibility = Visibility.Visible;
        }
        window.CompactThumbnail.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
        window.CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, TimeSpan.Zero));
        window.CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(6, TimeSpan.Zero));
        Invoke("EnsureScreenshotSurface");
        var arbiter = (CompactPillArbiter)typeof(MainWindow).GetField("_compactPillArbiter", PrivateInstance)!.GetValue(window)!;
        Set("_screenshotSlotToken", arbiter.TryAcquire(CompactPillSlot.Screenshot).Token);
        Invoke("FinishScreenshotDismissal", false);

        Assert.Same(newImage, window.CompactThumbnail.Source);
        Assert.Equal(1, window.CompactThumbnail.Opacity);
        Assert.Equal(1, window.CompactThumbnailOutScale.ScaleX);
        Assert.Equal(0, window.CompactThumbnailOutBlur.Radius);
        Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
        Assert.Equal(Visibility.Collapsed, window.CompactThumbnailNext.Visibility);
        Assert.Null(window.CompactThumbnailNext.Source);
        Assert.Null(typeof(MainWindow).GetField("_pendingFlipThumbnail", PrivateInstance)!.GetValue(window));
        Assert.Null(typeof(MainWindow).GetField("_cachedThumbnailExpandTarget", PrivateInstance)!.GetValue(window));
        Assert.Equal(CompactPillSlot.None, arbiter.ActiveSlot);
    });

    [Fact]
    public void ScreenshotExpansionDoesNotHandoffOrMeasureTheHiddenMediaThumbnail() => WithWindow(window =>
    {
        var mediaImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
        var staleCompactImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        window.ThumbnailImage.Source = mediaImage;
        window.CompactThumbnail.Source = staleCompactImage;
        typeof(MainWindow).GetMethod("EnsureScreenshotSurface", PrivateInstance)!.Invoke(window, null);
        var arbiter = (CompactPillArbiter)typeof(MainWindow).GetField("_compactPillArbiter", PrivateInstance)!.GetValue(window)!;
        typeof(MainWindow).GetField("_screenshotSlotToken", PrivateInstance)!.SetValue(window,
            arbiter.TryAcquire(CompactPillSlot.Screenshot).Token);
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", PrivateInstance)!.GetValue(window)!;
        typeof(MainWindow).GetMethod("OnExpandCompleted", PrivateInstance)!.Invoke(window,
            [(int)coordinator.ActiveTransitionId, true, NotchView.Media]);
        Assert.Same(mediaImage, window.ThumbnailImage.Source);
        Assert.Null(typeof(MainWindow).GetField("_cachedThumbnailExpandTarget", PrivateInstance)!.GetValue(window));
    });

    private static void WithWindow(Action<MainWindow> action) => SharedStaTestRunner.Run(() =>
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            app.Resources["SFProText"] = new FontFamily("Segoe UI");
            app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        }
        string settingsPath = Path.Combine(Path.GetTempPath(), $"vnotch-screenshot-restore-{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        settingsService.Save(new NotchSettings { EnableSpotlight = false, AutoCheckUpdates = false, EnableWeather = false });
        var services = new ServiceCollection();
        var configure = typeof(App).GetMethod("ConfigureServices", PrivateInstance | BindingFlags.Static)!;
        configure.Invoke(configure.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(typeof(App)), [services]);
        services.AddSingleton<ISettingsService>(settingsService);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            action(window);
        }
        finally
        {
            window.Close();
            File.Delete(settingsPath);
        }
    });
}
