using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class GreetingAcceptanceTests
{
    public static IEnumerable<object?[]> LanguageCases =>
        new string?[] { "vi", "VI", " vi ", "en", "ja", "zh", "pt", "ru", "ar", "ko", "es", "fr", "de", "hi", "it", "tr", "pl", "nl", "id", "unknown", null, "" }
            .Select(code => new object?[] { code, code?.Trim().Equals("vi", StringComparison.OrdinalIgnoreCase) == true });

    [Fact]
    public void SpotlightHandoffRetiresHoverScaleAndBlocksNewHoverClocks() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new MainWindowFixture("en", greeting: false);
        var window = fixture.Window;
        window.NotchScale.ScaleX = 1.08;
        window.NotchShadowScale.ScaleX = 1.08;
        window.SetSpotlightMorphSessionActive(true);
        Assert.Equal(1, window.NotchScale.ScaleX);
        Assert.Equal(1, window.NotchShadowScale.ScaleX);
        Invoke(window, "AnimateNotchHover", true);
        Assert.False(window.NotchScale.HasAnimatedProperties);
        Assert.Equal(0, window.HoverGlow.Opacity);
        window.SetSpotlightMorphSessionActive(false);
    });

    [Fact]
    public void GreetingBlocksKeyboardInputUntilDismissed() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new MainWindowFixture("en");
        await fixture.ShowGreetingAsync(ct);
        var input = System.Windows.Input.InputManager.Current;
        int delivered = 0;
        System.Windows.Input.ProcessInputEventHandler observe = (_, _) => delivered++;
        input.PostProcessInput += observe;
        try
        {
            input.ProcessInput(new System.Windows.Input.TextCompositionEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                new System.Windows.Input.TextComposition(input, fixture.Window, "test"))
            { RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent });
            Assert.Equal(0, delivered);
            Assert.True(fixture.Window.IsGreetingInteractionBlocked);
            Invoke(fixture.Window, "DismissGreeting");
            await WpfFrameWaiter.UntilAsync(() => !fixture.Window.IsGreetingInteractionBlocked, "greeting keyboard block cleared", ct);
        }
        finally { input.PostProcessInput -= observe; }
    });

    [Theory]
    [MemberData(nameof(LanguageCases))]
    public void OnlyVietnameseUsesXinChaoAndAllOtherLanguagesUseHello(string? language, bool vietnamese)
    {
        Assert.Equal(vietnamese, StartupGreeting.UsesVietnamese(language));
        Assert.Equal(!vietnamese, StartupGreeting.UsesEnglishHandwriting(language));
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ar")]
    [InlineData("de")]
    public void NewWidgetsAreHiddenAsAGroupAndTheirStateSurvivesGreeting(string language) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new MainWindowFixture(language);
        var window = fixture.Window;
        var newWidget = new Border { Opacity = 0.65, Visibility = Visibility.Visible };
        var disabledWidget = new Border { Visibility = Visibility.Collapsed };
        window.NotchLiveContent.Children.Add(newWidget);
        window.NotchLiveContent.Children.Add(disabledWidget);
        await fixture.ShowGreetingAsync(ct);
        Assert.Equal(Visibility.Hidden, window.NotchLiveContent.Visibility);
        Assert.Equal(Visibility.Visible, newWidget.Visibility);
        Assert.Equal(0.65, newWidget.Opacity);
        Assert.Equal(Visibility.Collapsed, disabledWidget.Visibility);
        Assert.Equal(language == "vi" ? Visibility.Visible : Visibility.Collapsed, window.XinChaoPathContainer.Visibility);
        Assert.Equal(language != "vi" ? Visibility.Visible : Visibility.Collapsed, window.HelloPathContainer.Visibility);
        foreach (var widget in new FrameworkElement[] { window.VolumeIndicatorContainer, window.CollapsedContent,
            window.NavIconsPanel, window.MusicCompactContent, window.AudioContent, window.PrivacyIndicatorPanel })
            Assert.True(IsDescendantOf(widget, window.NotchLiveContent));
        Assert.False(IsDescendantOf(window.GreetingOverlay, window.NotchLiveContent));

        // Dismiss through the production fade + collapse callbacks.
        Invoke(window, "DismissGreeting");
        await WpfFrameWaiter.UntilAsync(() => !GetField<bool>(window, "_isGreetingActive"), "greeting dismissed", ct);
        Assert.Equal(Visibility.Visible, window.NotchLiveContent.Visibility);
        Assert.Equal(Visibility.Visible, newWidget.Visibility);
        Assert.Equal(0.65, newWidget.Opacity);
        Assert.Equal(Visibility.Collapsed, disabledWidget.Visibility);
        Assert.Equal(Visibility.Collapsed, window.GreetingOverlay.Visibility);
    });

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void GreetingAutomaticallyDismissesAndRestoresTheCompactView(string language) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new MainWindowFixture(language);
        await fixture.ShowGreetingAsync(ct);
        Assert.True(GetField<bool>(fixture.Window, "_isGreetingActive"));
        await WpfFrameWaiter.UntilAsync(() => language == "vi" ? fixture.Window.ViPath1.HasAnimatedProperties : fixture.Window.HelloPath1.HasAnimatedProperties, "greeting strokes started", ct);
        Invoke(fixture.Window, "ScheduleGreetingDismiss", TimeSpan.FromMilliseconds(1));
        await WpfFrameWaiter.UntilAsync(() => !GetField<bool>(fixture.Window, "_isGreetingActive"), "greeting auto-dismissed", ct);
        Assert.Null(GetField<UiScheduledTimer?>(fixture.Window, "_greetingDismissTimer"));
        Assert.Equal(Visibility.Visible, fixture.Window.NotchLiveContent.Visibility);
        Assert.Equal(Visibility.Visible, fixture.Window.CollapsedContent.Visibility);
        Assert.Equal(Visibility.Collapsed, fixture.Window.GreetingOverlay.Visibility);
        Assert.False((bool)typeof(MainWindow).GetProperty("_isAnimating", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Window)!);
    });

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void CloseDuringStrokesStopsDismissTimerAndAnimationCallbacks(string language) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new MainWindowFixture(language);
        var window = fixture.Window;
        await fixture.ShowGreetingAsync(ct);
        await WpfFrameWaiter.UntilAsync(() => language == "vi" ? window.ViPath10.HasAnimatedProperties : window.HelloPath2.HasAnimatedProperties,
            "greeting strokes animating", ct);
        Invoke(window, "ScheduleGreetingDismiss", TimeSpan.FromMinutes(1));
        var timer = GetField<UiScheduledTimer>(window, "_greetingDismissTimer");
        Assert.True(timer.IsEnabled);
        window.Close();
        Assert.False(timer.IsEnabled);
        Assert.Null(GetField<UiScheduledTimer?>(window, "_greetingDismissTimer"));
        Assert.False(GetField<bool>(window, "_isGreetingActive"));
        foreach (var path in Paths(window)) Assert.False(path.HasAnimatedProperties);
        await Task.Delay(50, ct);
        Assert.Null(GetField<UiScheduledTimer?>(window, "_greetingDismissTimer"));
    });

    [Fact]
    public void CloseDuringInitialAppearCannotStartGreetingLater() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new MainWindowFixture("vi");
        fixture.Window.ShowActivated = false;
        fixture.Window.Show();
        await WpfFrameWaiter.UntilAsync(() => GetField<bool>(fixture.Window, "_isGreetingActive"), "greeting active on initial appear", ct);
        fixture.Window.Close();
        await Task.Delay(50, ct);
        Assert.False(GetField<bool>(fixture.Window, "_isGreetingActive"));
        Assert.Null(GetField<UiScheduledTimer?>(fixture.Window, "_greetingDismissTimer"));
    });

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    [InlineData("ar")]
    [InlineData("de")]
    [InlineData("ru")]
    public void GreetingGeometryRendersInsideViewportAt100150And200Percent(string language) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new MainWindowFixture(language);
        var window = fixture.Window;
        Invoke(window, "PlayGreetingAnimation");
        Assert.Equal(language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, window.FlowDirection);
        Assert.Equal(language != "vi" ? Visibility.Visible : Visibility.Collapsed, window.HelloPathContainer.Visibility);
        foreach (var path in Paths(window))
        {
            path.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            path.StrokeDashOffset = 0;
            path.Opacity = 1;
        }
        window.ViDotI.Visibility = Visibility.Visible;
        window.ViDotI.Opacity = 1;
        const int width = 320, height = 147;
        window.GreetingOverlay.Measure(new Size(width, height));
        window.GreetingOverlay.Arrange(new Rect(0, 0, width, height));
        window.GreetingOverlay.UpdateLayout();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(window.GreetingOverlay)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
                Stretch = Stretch.None
                // The visual inherits RTL; the DrawingVisual destination is LTR.
                ,
                RelativeTransform = window.GreetingOverlay.FlowDirection == FlowDirection.RightToLeft
                    ? new ScaleTransform(-1, 1, .5, .5) : Transform.Identity
            }, null, new Rect(0, 0, width, height));
        }
        string? artifacts = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            double scale = dpi / 96d;
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            int ink = 0, minX = bitmap.PixelWidth, minY = bitmap.PixelHeight, maxX = 0, maxY = 0;
            for (int y = 0; y < bitmap.PixelHeight; y++)
                for (int x = 0; x < bitmap.PixelWidth; x++)
                    if (pixels[y * stride + x * 4] > 32)
                    { ink++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            Assert.True(ink > 100, "The greeting must render visible strokes.");
            Assert.True(minX / scale >= 8 && maxX / scale <= width - 8, $"Horizontal ink bounds: {minX}..{maxX} at {dpi} DPI.");
            Assert.True(minY / scale >= 8 && maxY / scale <= height - 8, $"Vertical ink bounds: {minY}..{maxY} at {dpi} DPI.");
            if (!string.IsNullOrEmpty(artifacts))
            {
                Directory.CreateDirectory(artifacts);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(System.IO.Path.Combine(artifacts, $"greeting-{language}-{(int)(scale * 100)}.png"));
                encoder.Save(stream);
            }
        }
    });

    [Theory]
    [InlineData("de", "Datenschutzeinstellungen und Batteriekapazität")]
    [InlineData("ru", "Настройки конфиденциальности и ёмкость аккумулятора")]
    public void CompactLabelsAreBoundedAndTheirTooltipRetainsTheFullTranslation(string language, string text) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new MainWindowFixture(language);
        var label = fixture.Window.BluetoothDeviceName;
        label.Text = text;
        label.Measure(new Size(90, 40));
        label.Arrange(new Rect(0, 0, 90, 40));
        label.UpdateLayout();
        Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        Assert.True(label.DesiredSize.Width <= 90);
        Assert.Equal(text, label.ToolTip);
    });

    private static IEnumerable<System.Windows.Shapes.Path> Paths(MainWindow window) =>
        new[] { window.HelloPath1, window.HelloPath2, window.ViPath1, window.ViPath2, window.ViPath3, window.ViPath4,
            window.ViPath5, window.ViPath6, window.ViPath7, window.ViPath8, window.ViPath9, window.ViPath10 };

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
    private static void Invoke(MainWindow window, string name, params object[] args)
        => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static T GetField<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    internal sealed class MainWindowFixture : IDisposable, IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-greeting-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        private Task? _disposeTask;
        internal MainWindow Window { get; }
        internal MainWindowFixture(string language, bool greeting = true, Action<NotchSettings>? configureSettings = null,
            Action<IServiceCollection>? configureServices = null)
        {
            Loc.SetLanguage(language);
            BackgroundTestWindows.EnsureApplicationResources();
            var settings = new SettingsService(System.IO.Path.Combine(_directory, "settings.json"), _ => { });
            var options = new NotchSettings
            {
                Language = language,
                Width = 300,
                Height = 40,
                CornerRadius = 20,
                EnableHelloGreeting = greeting,
                EnableSpotlight = false,
                EnableWeather = false,
                AutoCheckUpdates = false,
                HideOnExclusiveFullscreen = false,
                HideOnWindowedFullscreen = false
            };
            configureSettings?.Invoke(options);
            settings.Save(options);
            var services = new ServiceCollection();
            ServiceConfigurator.ConfigureServices(services);
            // The fixture's provider owns the worker and drains queued saves before deleting its directory.
            services.AddSingleton<ISettingsService>(_ => settings);
            services.AddSingleton(_ => new VNotch.Services.Clipboard.ClipboardHistoryStore(System.IO.Path.Combine(_directory, "clipboard")));
            configureServices?.Invoke(services);
            _provider = services.BuildServiceProvider();
            Window = _provider.GetRequiredService<MainWindow>();
        }
        internal Task ShowGreetingAsync(CancellationToken ct = default)
        {
            Window.ShowActivated = false;
            Window.Show();
            return WpfFrameWaiter.UntilAsync(() => Window.GreetingOverlay.Visibility == Visibility.Visible, "greeting overlay visible", ct);
        }
        public void Dispose()
        {
            DisposeAsync().AsTask().SafeFireAndForget("TEST-FIXTURE-DISPOSE");
        }

        public ValueTask DisposeAsync()
        {
            if (_disposeTask == null)
            {
                Window.Close();
                Loc.SetLanguage("en");
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }

        private async Task DisposeCoreAsync()
        {
            await Window.CleanupAsync();
            await _provider.DisposeAsync();
            string expectedPrefix = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-greeting-");
            Assert.StartsWith(expectedPrefix, System.IO.Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
