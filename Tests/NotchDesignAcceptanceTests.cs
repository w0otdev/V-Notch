using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotchDesignAcceptanceTests
{
    [Theory]
    [InlineData("en", 96)]
    [InlineData("en", 144)]
    [InlineData("en", 192)]
    [InlineData("vi", 144)]
    [InlineData("ar", 144)]
    public void MediaTextUsesArtworkColorsAndReturnsToNeutralWithoutArtwork(string language, int dpi) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture(language, greeting: false, options =>
        {
            options.EnableLocalOnlyMode = true;
            options.EnableSubjectBlur = false;
        });
        var window = fixture.Window;
        // Keep physical desktop clicks/deactivation from changing the view under a render assertion.
        window.SetDebugViewLock(true);
        window.ShowActivated = false;
        window.Show();
        PumpUntil(() => Field<bool>(window, "_isStartupLayoutReady"));
        Field<MediaDetectionService>(window, "_mediaService").Stop();
        Field<DispatcherTimer>(window, "_updateTimer").Stop();
        PumpUntil(() => !IsAnimating(window));
        PumpFor(TimeSpan.FromMilliseconds(150)); // Drain the service's final empty snapshot.
        var artwork = new BitmapImage();
        artwork.BeginInit();
        artwork.CacheOption = BitmapCacheOption.OnLoad;
        artwork.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dark-textured-artwork.png"));
        artwork.EndInit();
        artwork.Freeze();
        var info = new MediaInfo
        {
            CurrentTrack = "Midnight, softly",
            CurrentArtist = "V-Notch · Local session",
            MediaSource = "Local",
            IsAnyMediaPlaying = true,
            IsPlaying = true,
            Thumbnail = artwork,
            Duration = TimeSpan.FromMinutes(4),
            Position = TimeSpan.FromSeconds(72)
        };
        typeof(ShellViewModel).GetMethod("ApplyMediaUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window.DataContext, new object?[] { info });
        PumpFor(TimeSpan.FromMilliseconds(800));
        Assert.Equal(info.CurrentTrack, ((ShellViewModel)window.DataContext).Media.Title);
        Assert.Equal(info.CurrentTrack, Field<MediaInfo>(window, "_currentMediaInfo").CurrentTrack);
        Invoke(window, "ExpandNotch", null, NotchView.Media);
        CaptureTransitionIfRequested(window, language, dpi);
        PumpUntil(() => !IsAnimating(window));
        PumpFor(TimeSpan.FromMilliseconds(650));
        Assert.Equal(Visibility.Visible, window.ExpandedContent.Visibility);
        var title = window.TrackTitleLayer.Opacity > window.TrackTitleNextLayer.Opacity ? window.TrackTitle : window.TrackTitleNext;
        Assert.Equal(info.CurrentTrack, title.Text);
        Assert.True(Math.Max(window.TrackTitleLayer.Opacity, window.TrackTitleNextLayer.Opacity) > .99);
        AssertMediaInk(window, hasArtwork: true);
        Assert.Equal(.45, window.FileShelfIconButton.Opacity, 3);
        Assert.Equal(1, window.HomeIconButton.Opacity, 3);
        var navInk = Assert.IsType<SolidColorBrush>(((System.Windows.Shapes.Path)((System.Windows.Controls.Viewbox)window.FileShelfIconButton.Child).Child).Fill);
        Assert.Equal(Colors.White, navInk.Color);
        SaveFrame(window, language, dpi, "artwork");
        Invoke(window, "HideMediaBackground");
        PumpFor(TimeSpan.FromMilliseconds(550));
        AssertMediaInk(window, hasArtwork: false);
        SaveFrame(window, language, dpi, "black");
        window.SetDebugViewLock(false);
        Invoke(window, "CollapseNotch", new object?[] { null });
        PumpUntil(() => !IsAnimating(window) && ((ShellViewModel)window.DataContext).CurrentView == NotchView.Compact);
        Invoke(window, "AnimateNotchHover", true);
        PumpFor(TimeSpan.FromMilliseconds(550));
        SaveFrame(window, language, dpi, "hover");
    });

    [Fact]
    public void ReducedMotionHoverKeepsTheNotchStillAndShowsLightFeedback() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false);
        try
        {
            fixture.Window.ShowActivated = false;
            fixture.Window.Show();
            PumpUntil(() => Field<bool>(fixture.Window, "_isStartupLayoutReady"));
            Field<MediaDetectionService>(fixture.Window, "_mediaService").Stop();
            PumpUntil(() => !IsAnimating(fixture.Window));
            AnimationConfig.SetReduceMotion(true);
            Invoke(fixture.Window, "AnimateNotchHover", true);
            PumpFor(TimeSpan.FromMilliseconds(250));
            Assert.Equal(1, fixture.Window.NotchScale.ScaleX);
            Assert.False(fixture.Window.NotchScale.HasAnimatedProperties);
            Assert.Equal(.35, fixture.Window.HoverGlow.Opacity, 3);
            Assert.IsType<LinearGradientBrush>(fixture.Window.HoverGlow.BorderBrush);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static void AssertMediaInk(MainWindow window, bool hasArtwork)
    {
        var title = ((LinearGradientBrush)window.Resources["TrackTitleGradient"]).GradientStops[0].Color;
        var artist = Assert.IsType<SolidColorBrush>(window.TrackArtist.Foreground).Color;
        var currentTime = Assert.IsType<SolidColorBrush>(window.CurrentTimeText.Foreground).Color;
        var remainingTime = Assert.IsType<SolidColorBrush>(window.RemainingTimeText.Foreground).Color;
        Assert.Equal(currentTime, remainingTime);
        if (hasArtwork)
        {
            Assert.NotEqual(Colors.White, title);
            Assert.True(title.R >= 216 && title.G >= 216 && title.B >= 216);
            Assert.Equal(255, title.A);
            Assert.Equal(Color.FromArgb(191, title.R, title.G, title.B), artist);
            Assert.Equal(artist, Assert.IsType<SolidColorBrush>(window.LyricTextA.Foreground).Color);
            Assert.Equal(artist, Assert.IsType<SolidColorBrush>(window.LyricTextB.Foreground).Color);
            Assert.Equal(255, currentTime.A);
            Assert.True(currentTime.R != currentTime.G || currentTime.G != currentTime.B);
            Assert.Equal(currentTime, Assert.IsType<SolidColorBrush>(window.CompactTitleMarquee.Foreground).Color);
        }
        else
        {
            Assert.Equal(UiPalette.PrimaryColor, title);
            Assert.Equal(UiPalette.SecondaryColor, artist);
            Assert.Equal(UiPalette.SecondaryColor, currentTime);
        }
        Assert.Equal(title, Assert.IsType<SolidColorBrush>(window.PlayIconPath.Fill).Color);
        Assert.Equal(title, Assert.IsType<SolidColorBrush>(window.PauseIconPath.Fill).Color);
        Assert.Equal(title, Assert.IsType<SolidColorBrush>(window.PrevArrow2.Fill).Color);
        Assert.Equal(title, Assert.IsType<SolidColorBrush>(window.NextArrow1.Fill).Color);
        Assert.Equal(UiPalette.PrimaryColor, Assert.IsType<SolidColorBrush>(window.VolumeIcon.Foreground).Color);
        Assert.True(UiPalette.PrimaryBrush.IsFrozen && UiPalette.SecondaryBrush.IsFrozen && UiPalette.IconBrush.IsFrozen);
    }

    private static void CaptureTransitionIfRequested(MainWindow window, string language, int dpi)
    {
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (string.IsNullOrEmpty(directory) || language != "en" || dpi != 144) return;
        var elapsed = Stopwatch.StartNew();
        var timestamps = new List<string> { "frame,milliseconds,width,height" };
        for (int i = 0; i < 35; i++)
        {
            SaveFrame(window, language, dpi, $"frame-{i:D2}");
            timestamps.Add(string.Create(CultureInfo.InvariantCulture,
                $"{i},{elapsed.Elapsed.TotalMilliseconds},{window.NotchBorder.ActualWidth},{window.NotchBorder.ActualHeight}"));
            PumpFor(TimeSpan.FromMilliseconds(20));
        }
        File.WriteAllLines(Path.Combine(directory, "frames.csv"), timestamps);
    }

    private static void SaveFrame(MainWindow window, string language, int dpi, string state)
    {
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        const int width = 700, height = 240;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 33, 44)), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(window.NotchContainer)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect((window.NotchContainer.ActualWidth - width) / 2, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 32, width, height),
                Stretch = Stretch.None,
                RelativeTransform = window.FlowDirection == FlowDirection.RightToLeft ? new ScaleTransform(-1, 1, .5, .5) : Transform.Identity
            }, null, new Rect(0, 32, width, height));
        }
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"notch-{language}-{(int)(scale * 100)}-{state}.png"));
        encoder.Save(stream);
    }

    private static bool IsAnimating(MainWindow window) =>
        (bool)typeof(MainWindow).GetProperty("_isAnimating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static void PumpUntil(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) PumpFor(TimeSpan.FromMilliseconds(20));
        Assert.True(ready(), "The production transition did not complete.");
    }
    private static void PumpFor(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
