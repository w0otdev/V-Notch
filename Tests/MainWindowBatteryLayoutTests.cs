using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowBatteryLayoutTests
{
    [Fact]
    public void BothFillsFollowIconSizeAndBorderChanges() => SharedStaTestRunner.RunAsync(async () =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true,
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        AssertFillTracksLayout(window.BatteryIcon, window.BatteryFill);
        AssertFillTracksLayout(Assert.IsType<Border>(window.ChargingBatteryFill.Parent), window.ChargingBatteryFill);
    });

    private static void AssertFillTracksLayout(Border icon, Border fill)
    {
        Arrange(icon);
        double before = fill.ActualWidth;
        Assert.True(before > 0);

        icon.Width += 12;
        var border = icon.BorderThickness;
        icon.BorderThickness = new Thickness(border.Left + 1, border.Top + 1, border.Right + 1, border.Bottom + 1);
        Arrange(icon);

        // WPF layout rounding can shift either edge by a device pixel.
        double tolerance = 2 / VisualTreeHelper.GetDpi(icon).DpiScaleX;
        Assert.InRange(fill.ActualWidth - before, 10 - tolerance, 10 + tolerance);
    }

    private static void Arrange(Border icon)
    {
        icon.Measure(new Size(100, 100));
        icon.Arrange(new Rect(0, 0, icon.Width, icon.Height));
    }
}
