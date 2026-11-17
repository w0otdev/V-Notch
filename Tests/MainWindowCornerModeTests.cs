using System.Globalization;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowCornerModeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double Size = NotchSettings.CornerCollapsedSize;

    [Fact]
    public void CentreIsTheDefaultPosition()
    {
        var settings = new NotchSettings();
        Assert.False(settings.EnableTopLeftCornerMode);
        Assert.False(settings.TopLeftCornerFloatingCircle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CornerModeTucksTheCollapsedNotchIntoTheTopLeft(bool floating) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(s => { s.EnableTopLeftCornerMode = true; s.TopLeftCornerFloatingCircle = floating; });
        var window = fixture.Window;
        await ShowAsync(window, ct);
        double gap = floating ? 8 : 0;

        Assert.Equal(HorizontalAlignment.Left, window.NotchWrapper.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Left, window.NotchShadowWrapper.HorizontalAlignment);
        Assert.Equal(Size, window.NotchBorder.Width);
        Assert.Equal(Size, window.NotchBorder.Height);
        Assert.Equal(floating ? new CornerRadius(Size / 2) : new CornerRadius(0, 0, Size, 0), window.NotchBorder.CornerRadius);
        Assert.Equal(gap, window.NotchContainer.Margin.Left);
        Assert.Equal(gap, window.NotchContainer.Margin.Top);
        Assert.Equal(Visibility.Collapsed, window.LeftEar.Visibility);
        Assert.Equal(Visibility.Collapsed, window.RightEar.Visibility);

        // Satellite windows (Settings, Spotlight morph) line up with this rectangle.
        var screen = MonitorSelection.Resolve(Field<NotchSettings>(window, "_settings"));
        var rect = window.GetNotchScreenRect();
        Assert.Equal(screen.Bounds.Left / MonitorSelection.GetScale(screen) + gap, rect.Left, 3);
        Assert.Equal(Size, rect.Width);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CornerModeShowsTheTimeWhenIdleAndOnlyTheAlbumArtWithMedia(bool floating) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(s => { s.EnableTopLeftCornerMode = true; s.TopLeftCornerFloatingCircle = floating; });
        var window = fixture.Window;
        await ShowAsync(window, ct);
        string pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Replace("tt", "").Trim();

        window.CollapsedContent.Visibility = Visibility.Visible;
        var before = DateTime.Now;
        Invoke(window, "RefreshCollapsedClock");
        Assert.True(window.CollapsedClockText.IsVisible);
        Assert.Contains(window.CollapsedClockText.Text, new[] { before.ToString(pattern, CultureInfo.CurrentCulture), DateTime.Now.ToString(pattern, CultureInfo.CurrentCulture) });

        window.CollapsedContent.Visibility = Visibility.Collapsed;
        window.MusicCompactContent.Visibility = Visibility.Visible;
        window.MusicViz.Visibility = Visibility.Visible;
        Assert.True(window.CompactThumbnailBorder.IsVisible);
        Assert.False(window.CompactClockText.IsVisible);
        Assert.Equal(Visibility.Collapsed, window.MusicViz.Visibility);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CornerModeExpandsFromTheCornerAndCollapsesBackIntoIt(bool floating) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            await using var fixture = CreateFixture(s =>
            {
                s.EnableTopLeftCornerMode = true;
                s.TopLeftCornerFloatingCircle = floating;
                s.DisableMouseLeaveAutoClose = true;
            });
            var window = fixture.Window;
            await ShowAsync(window, ct);
            var coordinator = Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator");
            double gap = floating ? 8 : 0;

            Assert.True(coordinator.RequestView(NotchView.Media, "ExpandFromCorner"));
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "expanded from the corner", ct);
            window.UpdateLayout();
            Assert.True(window.NotchBorder.ActualWidth > 300);
            Assert.True(window.NotchBorder.ActualHeight > Size);
            var origin = window.NotchBorder.TransformToAncestor(window).Transform(new Point(0, 0));
            Assert.Equal(gap, origin.X, 1);
            Assert.Equal(gap, origin.Y, 1);
            Assert.Equal(floating ? window.NotchBorder.CornerRadius.BottomRight : 0, window.NotchBorder.CornerRadius.TopLeft);
            Assert.Equal(floating ? window.NotchBorder.CornerRadius.BottomRight : 0, window.NotchBorder.CornerRadius.BottomLeft);
            Assert.True(window.NotchBorder.CornerRadius.BottomRight > 0);

            Assert.True(coordinator.RequestView(NotchView.Compact, "CollapseIntoCorner"));
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "collapsed into the corner", ct);
            window.UpdateLayout();
            Assert.Equal(Size, window.NotchBorder.ActualWidth, 1);
            Assert.Equal(Size, window.NotchBorder.ActualHeight, 1);
            Assert.Equal(floating ? new CornerRadius(Size / 2) : new CornerRadius(0, 0, Size, 0), window.NotchBorder.CornerRadius);
        }
        finally { AnimationConfig.SetReduceMotion(previousReducedMotion); }
    });

    [Fact]
    public void SwitchingBackToCentreRestoresTheCentredNotch() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(s => s.EnableTopLeftCornerMode = true);
        var window = fixture.Window;
        await ShowAsync(window, ct);
        var field = typeof(MainWindow).GetField("_settings", Private)!;
        var corner = (NotchSettings)field.GetValue(window)!;
        var centred = corner.Clone();
        centred.EnableTopLeftCornerMode = false;

        field.SetValue(window, centred);
        typeof(MainWindow).GetMethod("ApplySettings", Private, [typeof(NotchSettings), typeof(bool)])!.Invoke(window, [corner, false]);
        Invoke(window, "PositionAtTop");

        Assert.Equal(HorizontalAlignment.Center, window.NotchWrapper.HorizontalAlignment);
        Assert.Equal(centred.Width, window.NotchBorder.Width);
        Assert.Equal(centred.Height, window.NotchBorder.Height);
        Assert.Equal(new CornerRadius(0, 0, centred.CornerRadius, centred.CornerRadius), window.NotchBorder.CornerRadius);
        Assert.Equal(0, window.NotchContainer.Margin.Left);
        Assert.Equal(Visibility.Visible, window.LeftEar.Visibility);
        Assert.NotEqual(Visibility.Collapsed, window.CompactClockText.Visibility);
        var rect = window.GetNotchScreenRect();
        var screen = MonitorSelection.Resolve(centred);
        double scale = MonitorSelection.GetScale(screen);
        Assert.Equal(screen.Bounds.Left / scale + (screen.Bounds.Width / scale - centred.Width) / 2.0, rect.Left, 0);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(Action<NotchSettings> configure) =>
        new("en", greeting: false, configure,
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));

    private static async Task ShowAsync(MainWindow window, CancellationToken ct)
    {
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_coreModulesStarted") && Field<bool>(window, "_isStartupLayoutReady") &&
            !(bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!, "startup", ct);
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
