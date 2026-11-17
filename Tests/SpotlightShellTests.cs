using System;
using System.Windows;
using System.Windows.Media;
using VNotch;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class SpotlightShellTests
{
    [Fact]
    public void EntranceMetricsMatchAutoLayout()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings());
            var window = fixture.Window;
            window.Shell.BorderThickness = new Thickness(1);
            double width = SpotlightLayoutMetrics.ResolveMeasureWidth(window.ActualWidth, window.Width);
            window.Shell.Measure(new Size(width, double.PositiveInfinity));
            var target = SpotlightLayoutMetrics.CalculateEntranceSize(
                width, window.Shell.DesiredSize.Height, window.Shell.ActualHeight, window.Shell.Margin);
            window.Shell.Measure(new Size(window.Width, double.PositiveInfinity));
            double restingHeight = window.Shell.DesiredSize.Height - window.Shell.Margin.Top - window.Shell.Margin.Bottom;
            Assert.Equal(target.Height, restingHeight, precision: 3);
        });
    }

    [Fact]
    public void DefaultSettings_KeepOpaqueShellSkin()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings());
            var window = fixture.Window;

            Assert.NotEqual(Brushes.Transparent, window.Shell.Background);
        });
    }

    [Fact]
    public void ControllerCreatesAFreshSpotlightWindowAfterItsHostCloses()
    {
        RunSta(() =>
        {
            var settings = new NotchSettings { EnableSpotlight = true };
            using var first = new SpotlightWindowFixture(settings);
            using var second = new SpotlightWindowFixture(settings);
            int creations = 0;
            using var controller = new SpotlightController(() => ++creations == 1 ? first.Window : second.Window);
            var firstHost = new BackgroundWindow { Width = 100, Height = 100 };
            var secondHost = new BackgroundWindow { Width = 100, Height = 100 };
            try
            {
                controller.Initialize(firstHost, settings);
                controller.ToggleSpotlight();
                Assert.Equal(1, creations);
                firstHost.Close();
                controller.Initialize(secondHost, settings);
                controller.ToggleSpotlight();
                Assert.Equal(2, creations);
                Assert.Same(secondHost, second.Window.Owner);
            }
            finally
            {
                firstHost.Close();
                secondHost.Close();
            }
        });
    }

    internal static Application CreateApplicationResources()
    {
        BackgroundTestWindows.EnsureApplicationResources();
        return Application.Current!;
    }

    private static void RunSta(Action action) => SharedStaTestRunner.Run(action, 45);
}
