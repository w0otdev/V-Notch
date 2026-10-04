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
    public void NewWidgetsAreHiddenAsAGroupAndTheirStateSurvivesGreeting(string language) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new MainWindowFixture(language);
        var window = fixture.Window;
        var newWidget = new Border { Opacity = 0.65, Visibility = Visibility.Visible };
        var disabledWidget = new Border { Visibility = Visibility.Collapsed };
        window.NotchLiveContent.Children.Add(newWidget);
        window.NotchLiveContent.Children.Add(disabledWidget);
        fixture.ShowGreeting();
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
        PumpUntil(() => !GetField<bool>(window, "_isGreetingActive"), TimeSpan.FromSeconds(4));
        Assert.Equal(Visibility.Visible, window.NotchLiveContent.Visibility);
        Assert.Equal(Visibility.Visible, newWidget.Visibility);
        Assert.Equal(0.65, newWidget.Opacity);
        Assert.Equal(Visibility.Collapsed, disabledWidget.Visibility);
        Assert.Equal(Visibility.Collapsed, window.GreetingOverlay.Visibility);
    });

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void GreetingAutomaticallyDismissesAndRestoresTheCompactView(string language) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new MainWindowFixture(language);
        fixture.ShowGreeting();
        Assert.True(GetField<bool>(fixture.Window, "_isGreetingActive"));
        PumpUntil(() => !GetField<bool>(fixture.Window, "_isGreetingActive"), TimeSpan.FromSeconds(10));
        Assert.Null(GetField<DispatcherTimer?>(fixture.Window, "_greetingDismissTimer"));
        Assert.Equal(Visibility.Visible, fixture.Window.NotchLiveContent.Visibility);
        Assert.Equal(Visibility.Visible, fixture.Window.CollapsedContent.Visibility);
        Assert.Equal(Visibility.Collapsed, fixture.Window.GreetingOverlay.Visibility);
        Assert.False((bool)typeof(MainWindow).GetProperty("_isAnimating", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Window)!);
    });

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void CloseDuringStrokesStopsDismissTimerAndAnimationCallbacks(string language) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new MainWindowFixture(language);
        var window = fixture.Window;
        fixture.ShowGreeting();
        PumpUntil(() => language == "vi" ? window.ViPath10.HasAnimatedProperties : window.HelloPath2.HasAnimatedProperties,
            TimeSpan.FromSeconds(3));
        Invoke(window, "ScheduleGreetingDismiss", TimeSpan.FromMinutes(1));
        var timer = GetField<DispatcherTimer>(window, "_greetingDismissTimer");
        Assert.True(timer.IsEnabled);
        window.Close();
        Assert.False(timer.IsEnabled);
        Assert.Null(GetField<DispatcherTimer?>(window, "_greetingDismissTimer"));
        Assert.False(GetField<bool>(window, "_isGreetingActive"));
        foreach (var path in Paths(window)) Assert.False(path.HasAnimatedProperties);
        PumpFor(TimeSpan.FromMilliseconds(750));
        Assert.Null(GetField<DispatcherTimer?>(window, "_greetingDismissTimer"));
    });

    [Fact]
    public void CloseDuringInitialAppearCannotStartGreetingLater() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new MainWindowFixture("vi");
        fixture.Window.ShowActivated = false;
        fixture.Window.Show();
        PumpUntil(() => GetField<bool>(fixture.Window, "_isGreetingActive"), TimeSpan.FromSeconds(3));
        fixture.Window.Close();
        PumpFor(TimeSpan.FromMilliseconds(750));
        Assert.False(GetField<bool>(fixture.Window, "_isGreetingActive"));
        Assert.Null(GetField<DispatcherTimer?>(fixture.Window, "_greetingDismissTimer"));
    });

    [Theory]
    [InlineData("en", 96)]
    [InlineData("en", 144)]
    [InlineData("en", 192)]
    [InlineData("vi", 96)]
    [InlineData("vi", 144)]
    [InlineData("vi", 192)]
    [InlineData("ar", 96)]
    [InlineData("ar", 144)]
    [InlineData("ar", 192)]
    [InlineData("de", 96)]
    [InlineData("de", 144)]
    [InlineData("de", 192)]
    [InlineData("ru", 96)]
    [InlineData("ru", 144)]
    [InlineData("ru", 192)]
    public void GreetingGeometryRendersInsideViewportAt100150And200Percent(string language, int dpi) => SharedStaTestRunner.Run(() =>
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
        string? artifacts = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (!string.IsNullOrEmpty(artifacts))
        {
            Directory.CreateDirectory(artifacts);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(System.IO.Path.Combine(artifacts, $"greeting-{language}-{(int)(scale * 100)}.png"));
            encoder.Save(stream);
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

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) PumpFor(TimeSpan.FromMilliseconds(20));
        Assert.True(condition(), "The production animation callback did not complete within the timeout.");
    }
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    internal sealed class MainWindowFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-greeting-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        internal MainWindow Window { get; }
        internal MainWindowFixture(string language, bool greeting = true, Action<NotchSettings>? configureSettings = null,
            Action<IServiceCollection>? configureServices = null)
        {
            Loc.SetLanguage(language);
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            app.Resources["SFProText"] = new FontFamily("Segoe UI");
            app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
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
                AutoCheckUpdates = false
            };
            configureSettings?.Invoke(options);
            settings.Save(options);
            var services = new ServiceCollection();
            ServiceConfigurator.ConfigureServices(services);
            services.AddSingleton<ISettingsService>(settings);
            configureServices?.Invoke(services);
            _provider = services.BuildServiceProvider();
            Window = _provider.GetRequiredService<MainWindow>();
        }
        internal void ShowGreeting()
        {
            Window.ShowActivated = false;
            Window.Show();
            PumpUntil(() => Window.GreetingOverlay.Visibility == Visibility.Visible, TimeSpan.FromSeconds(4));
        }
        public void Dispose()
        {
            Window.Close();
            _provider.Dispose();
            string expectedPrefix = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-greeting-");
            Assert.StartsWith(expectedPrefix, System.IO.Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            Loc.SetLanguage("en");
        }
    }
}
