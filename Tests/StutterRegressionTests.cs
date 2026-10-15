using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class StutterRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdThumbnailTargetMatchesExpandedLayoutWithoutResizingShell(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            settings =>
            {
                settings.EnableDynamicIslandMode = island;
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
            }, services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
        var window = fixture.Window;
        window.SetDebugViewLock(true);
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>("_isStartupLayoutReady") && !IsAnimating(), "startup", ct);
        Field<IMediaDetectionService>("_mediaService").Stop();
        Call("SetupExpandedContentBeforeAnimation");
        double width = window.NotchBorder.Width;
        double height = window.NotchBorder.Height;
        var target = ((double X, double Y))Call("EnsureCachedThumbnailExpandTarget")!;
        Assert.Equal(width, window.NotchBorder.Width);
        Assert.Equal(height, window.NotchBorder.Height);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(), "media expansion", ct);
        window.UpdateLayout();
        var args = new object?[] { null };
        Assert.True((bool)typeof(MainWindow).GetMethod("TryComputeThumbnailExpandTarget", PrivateInstance)!.Invoke(window, args)!);
        var actual = ((double X, double Y))args[0]!;
        Assert.InRange(Math.Abs(actual.X - target.X), 0, 1.0);
        Assert.InRange(Math.Abs(actual.Y - target.Y), 0, 1.0);

        var artwork = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 80, 120, 180, 255 }, 4);
        artwork.Freeze();
        typeof(MainWindow).GetMethod("AnimateThumbnailSwitchOnly", PrivateInstance)!.Invoke(window, [artwork, true]);
        Assert.False(window.ThumbnailOutBlur.HasAnimatedProperties);
        Assert.False(window.ThumbnailNextBlur.HasAnimatedProperties);
        Assert.False(window.CompactThumbnailOutBlur.HasAnimatedProperties);
        Assert.False(window.CompactThumbnailNextBlur.HasAnimatedProperties);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>("_isThumbnailSwitchActive"), "artwork handoff", ct);
        Assert.Same(artwork, window.ThumbnailImage.Source);
        Assert.Same(artwork, window.CompactThumbnail.Source);
        Assert.Equal(1, window.ThumbnailImage.Opacity);
        Assert.Equal(Visibility.Collapsed, window.ThumbnailImageNext.Visibility);

        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
        object? Call(string name) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, null);
        bool IsAnimating() => (bool)typeof(MainWindow).GetProperty("_isAnimating", PrivateInstance)!.GetValue(window)!;
    });

    [Fact]
    public void MaterialAndContentShareFrozenClipUntilGeometryChanges() => SharedStaTestRunner.Run(() =>
    {
        var size = new Size(300, 100);
        var radius = new CornerRadius(20);
        var clip = GlassClipBuilder.CreateClip(size, radius, 3);
        var host = new Grid();
        host.Measure(size);
        host.Arrange(new Rect(size));
        var presenter = new GlassMaterialClipPresenter(host) { PowerFactor = 3 };
        presenter.UpdateClip(radius);
        Assert.Same(clip, host.Clip);
        Assert.True(clip!.IsFrozen);
        Assert.NotSame(clip, GlassClipBuilder.CreateClip(size, new CornerRadius(10), 3));
        Assert.NotSame(clip, GlassClipBuilder.CreateClip(size, radius, 2));
    });

    [Fact]
    public void CollapsedMaterialLayerSkipsRadiusChangesAndResynchronizes() => SharedStaTestRunner.Run(() =>
    {
        var host = new Grid();
        host.Measure(new Size(200, 60));
        host.Arrange(new Rect(0, 0, 200, 60));
        var rim = new Border { Visibility = Visibility.Collapsed };
        var presenter = new GlassMaterialClipPresenter(host, rim);
        presenter.SyncCornerRadius(new CornerRadius(20));
        Assert.Equal(new CornerRadius(), rim.CornerRadius);
        rim.Visibility = Visibility.Visible;
        presenter.SyncCornerRadius(new CornerRadius(20));
        Assert.Equal(new CornerRadius(20), rim.CornerRadius);
    });

    [Fact]
    public void PinShadowExistsOnlyWhilePinIsRevealed() => SharedStaTestRunner.Run(() =>
    {
        var badge = new MediaPinBadge();
        Assert.Null(badge.Effect);
        badge.SetPinned(true, animate: false);
        Assert.NotNull(badge.Effect);
        badge.SetPinned(false, animate: false);
        Assert.Null(badge.Effect);
        var reveal = (DependencyProperty)typeof(MediaPinBadge).GetField("RevealProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        badge.SetValue(reveal, 0.5);
        Assert.NotNull(badge.Effect);
        badge.SetValue(reveal, 0.0);
        Assert.Null(badge.Effect);
    });

    [Fact]
    public void RepeatingFresnelValuesDoesNotInvalidateBrush() => SharedStaTestRunner.Run(() =>
    {
        var stop = new GradientStop(Colors.White, 0);
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(stop);
        int changes = 0;
        brush.Changed += (_, _) => changes++;
        for (int frame = 0; frame < 550; frame++)
        {
            GlassVisualUpdates.SetColor(stop, Colors.White);
            GlassVisualUpdates.SetPoint(brush, RadialGradientBrush.CenterProperty, new Point(0.5, 0.5));
            GlassVisualUpdates.SetDouble(brush, RadialGradientBrush.RadiusXProperty, 0.500001);
        }
        Assert.Equal(0, changes);
        GlassVisualUpdates.SetColor(stop, Colors.Red);
        Assert.Equal(1, changes);
        GlassVisualUpdates.SetPoint(brush, RadialGradientBrush.CenterProperty, new Point(0.6, 0.5));
        Assert.Equal(2, changes);
    });

    [Fact]
    public void CpuMapCacheRemainsValidAfterShrinkingBuffers() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        var ensure = typeof(LiquidGlassController).GetMethod("EnsureMaps", PrivateInstance)!;
        var parameters = LiquidGlassController.GlassParams.Default;
        var large = new LiquidGlassController.MapDimensions(100, 100, 160, 160, 20, 80, 80, 10, 0, 0, 0);
        var small = new LiquidGlassController.MapDimensions(50, 40, 100, 100, 20, 40, 30, 5, 0, 0, 0);
        Assert.True((bool)ensure.Invoke(controller, [parameters, large])!);
        Assert.True((bool)ensure.Invoke(controller, [parameters, small])!);
        Assert.False((bool)ensure.Invoke(controller, [parameters, small])!);
        Assert.True((bool)ensure.Invoke(controller, [parameters, small with { CaptureShiftX = 1 }])!);
    });

    [Fact]
    public void CpuPixelBuffersGrowInChunksAndStayBounded()
    {
        int capacity = 0;
        int allocations = 0;
        for (int pixels = 10_000; pixels <= 300_000; pixels += 1_000)
        {
            int next = LiquidGlassController.GrowPixelBufferCapacity(capacity, pixels);
            if (next != capacity) allocations++;
            Assert.InRange(next, pixels, pixels + 65536);
            capacity = next;
        }
        Assert.InRange(allocations, 1, 20);
        Assert.Equal(capacity, LiquidGlassController.GrowPixelBufferCapacity(capacity, 10_000));
        Assert.Equal(1_000_000, LiquidGlassController.GrowPixelBufferCapacity(capacity, 1_000_000));
    }

    [Fact]
    public void RegionQueriesReturnWithoutWaitingForUiAndCoalesce() => SharedStaTestRunner.Run(() =>
    {
        int calls = 0;
        var expected = new LiquidGlassController.CaptureRegion(10, 20, 300, 80);
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => { calls++; return expected; });
        typeof(LiquidGlassController).GetField("_isActive", PrivateInstance)!.SetValue(controller, true);
        var getRegion = typeof(LiquidGlassController).GetMethod("GetRegionCached", PrivateInstance)!;
        var request = Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
                Assert.Null(getRegion.Invoke(controller, [false, (double)i]));
        });
        Assert.True(request.Wait(TimeSpan.FromSeconds(2)), "Capture worker must not wait for the UI dispatcher.");
        Assert.Equal(0, calls);
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert.Equal(1, calls);
        Assert.Equal(expected, (LiquidGlassController.CaptureRegion)getRegion.Invoke(controller, [false, 20.0])!);
        Assert.Equal(1, calls);
    });

    [Fact]
    public void CachedWindowOriginStillTracksAnimatedChildTransformsAndWindowMoves() => SharedStaTestRunner.Run(() =>
    {
        var translate = new TranslateTransform(12, 7);
        var child = new Border { Width = 80, Height = 30, RenderTransform = translate };
        var window = new Window { Width = 300, Height = 100, Left = 100, Top = 100, ShowInTaskbar = false, Content = child };
        try
        {
            window.Show();
            window.UpdateLayout();
            var geometry = new WindowScreenGeometry(window);
            Check();
            translate.X = 21.5;
            translate.Y = -3.25;
            Check();
            window.Left += 37;
            window.Top += 19;
            Check();
            void Check()
            {
                Assert.True(geometry.TryGetPosition(child, out var cached));
                var actual = child.PointToScreen(new Point());
                Assert.InRange(Math.Abs(actual.X - cached.X), 0, 0.01);
                Assert.InRange(Math.Abs(actual.Y - cached.Y), 0, 0.01);
            }
        }
        finally { window.Close(); }
    });
}
