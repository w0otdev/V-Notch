using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class FirstOpenLatencyTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void FirstSettingsOpenKeepsTheUiDispatcherResponsive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => !Animating(window), "notch startup settles", ct);

        var pauses = new List<double>();
        void Capture(string message) => output.WriteLine(message);
        SettingsWindow.FirstOpenProbe += Capture;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var watch = Stopwatch.StartNew();
            double lastTick = 0, longestPause = 0;
            bool closed = false;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                double now = watch.Elapsed.TotalMilliseconds;
                longestPause = Math.Max(longestPause, now - lastTick);
                lastTick = now;
                if (now >= 500 && Application.Current.Windows.OfType<SettingsWindow>().LastOrDefault() is { } settings)
                {
                    settings.Closed += (_, _) => closed = true;
                    settings.Close();
                }
            };
            timer.Start();
            try
            {
                typeof(MainWindow).GetMethod("OpenAppSettings", Private)!.Invoke(window, [false]);
                await WpfFrameWaiter.UntilAsync(() => closed, "settings close completes", ct);
            }
            finally { timer.Stop(); }
            pauses.Add(longestPause);
            output.WriteLine($"Settings open {attempt + 1}: longest UI pause {longestPause:F1} ms");
            await WpfFrameWaiter.UntilAsync(() => !Animating(window), "settings return settles", ct);
        }
        SettingsWindow.FirstOpenProbe -= Capture;
        Assert.True(pauses[0] < 100, $"First Settings click stalled the UI for {pauses[0]:F1} ms (second: {pauses[1]:F1} ms).");
    });

    [Fact]
    public void FirstNotchExpansionKeepsTheUiDispatcherResponsive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => !Animating(window), "notch startup settles", ct);
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
        var pauses = new List<double>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var watch = Stopwatch.StartNew();
            double lastTick = 0, longestPause = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                double now = watch.Elapsed.TotalMilliseconds;
                longestPause = Math.Max(longestPause, now - lastTick);
                lastTick = now;
            };
            timer.Start();
            try
            {
                coordinator.RequestView(NotchView.Media, "first-open-latency");
                await WpfFrameWaiter.UntilAsync(() => !Animating(window) && coordinator.CurrentView == NotchView.Media, "notch expansion completes", ct);
            }
            finally { timer.Stop(); }
            pauses.Add(longestPause);
            output.WriteLine($"Notch open {attempt + 1}: longest UI pause {longestPause:F1} ms");
            coordinator.RequestView(NotchView.Compact, "first-open-latency");
            await WpfFrameWaiter.UntilAsync(() => !Animating(window) && coordinator.CurrentView == NotchView.Compact, "notch collapse completes", ct);
        }
        Assert.True(pauses[0] < 100, $"First notch click stalled the UI for {pauses[0]:F1} ms (second: {pauses[1]:F1} ms).");
    });

    private static bool Animating(MainWindow window) =>
        (bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!;

    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.EnableDiagnosticLogging = false; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
}
