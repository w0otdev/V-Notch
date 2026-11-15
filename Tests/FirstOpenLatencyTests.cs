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
    [Trait("Category", "Performance")]
    public void FirstSettingsOpenKeepsTheUiDispatcherResponsive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() =>
            typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window) is SettingsWindow { IsLoaded: true, IsVisible: false },
            "post-entrance settings preparation finishes", ct);

        var pauses = new List<double>();
        void Capture(string message) => output.WriteLine(message);
        SettingsWindow.FirstOpenProbe += Capture;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var watch = Stopwatch.StartNew();
            double lastTick = 0, longestPause = 0;
            bool closed = false;
            bool measuring = false;
            var timerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                if (!measuring) { timerReady.TrySetResult(); return; }
                double now = watch.Elapsed.TotalMilliseconds;
                // Only time beyond the timer's requested interval is dispatcher delay.
                longestPause = Math.Max(longestPause, now - lastTick - timer.Interval.TotalMilliseconds);
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
                await timerReady.Task.WaitAsync(ct);
                watch.Restart();
                measuring = true;
                typeof(MainWindow).GetMethod("OpenAppSettings", Private)!.Invoke(window, [false]);
                await WpfFrameWaiter.UntilAsync(() => closed, "settings close completes", ct);
            }
            finally { timer.Stop(); }
            pauses.Add(longestPause);
            output.WriteLine($"Settings open {attempt + 1}: longest input timer delay {longestPause:F1} ms");
            await WpfFrameWaiter.UntilAsync(() => !Animating(window), "settings return settles", ct);
        }
        SettingsWindow.FirstOpenProbe -= Capture;
        Assert.True(pauses[0] < 100, $"First Settings click delayed the input timer by {pauses[0]:F1} ms (second: {pauses[1]:F1} ms).");
    });

    [Fact]
    [Trait("Category", "Performance")]
    public void FirstNotchExpansionKeepsTheUiDispatcherResponsive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        // Measure the first expansion after startup preparation, as the Settings
        // probe does. Entrance completion alone leaves service/template work queued.
        await WpfFrameWaiter.UntilAsync(() => !Animating(window) &&
            typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window) is SettingsWindow { IsLoaded: true, IsVisible: false },
            "notch startup and deferred UI preparation finish", ct);
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
        var pauses = new List<double>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var watch = Stopwatch.StartNew();
            double lastTick = 0, longestPause = 0;
            bool measuring = false;
            var timerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                if (!measuring) { timerReady.TrySetResult(); return; }
                double now = watch.Elapsed.TotalMilliseconds;
                longestPause = Math.Max(longestPause, now - lastTick - timer.Interval.TotalMilliseconds);
                lastTick = now;
            };
            timer.Start();
            try
            {
                await timerReady.Task.WaitAsync(ct);
                watch.Restart();
                measuring = true;
                coordinator.RequestView(NotchView.Media, "first-open-latency");
                await WpfFrameWaiter.UntilAsync(() => !Animating(window) && coordinator.CurrentView == NotchView.Media, "notch expansion completes", ct);
            }
            finally { timer.Stop(); }
            pauses.Add(longestPause);
            output.WriteLine($"Notch open {attempt + 1}: longest input timer delay {longestPause:F1} ms");
            coordinator.RequestView(NotchView.Compact, "first-open-latency");
            await WpfFrameWaiter.UntilAsync(() => !Animating(window) && coordinator.CurrentView == NotchView.Compact, "notch collapse completes", ct);
        }
        Assert.True(pauses[0] < 100, $"First notch click delayed the input timer by {pauses[0]:F1} ms (second: {pauses[1]:F1} ms).");
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SettingsPreparationYieldsToAnimationAndHonorsCancellation(bool pauseAfterConstruction, bool cancel) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        var animating = typeof(MainWindow).GetProperty("_isAnimating", Private)!;
        var preparedField = typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!;
        // Keep the production startup scheduler from launching a second preparation.
        typeof(MainWindow).GetField("_settingsPreparationScheduled", Private)!.SetValue(window, true);
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => !Animating(window), "notch startup settles", ct);
        void Capture(string message)
        {
            if (pauseAfterConstruction && message.Contains("Settings snapshot", StringComparison.Ordinal))
                animating.SetValue(window, true);
        }
        SettingsWindow.FirstOpenProbe += Capture;
        animating.SetValue(window, !pauseAfterConstruction);
        Task? preparation = null;
        try
        {
            preparation = (Task)typeof(MainWindow).GetMethod("PrepareSettingsFirstOpenAsync", Private)!.Invoke(window, [0])!;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.False(preparation.IsCompleted);
            if (pauseAfterConstruction)
                Assert.False(Assert.IsType<SettingsWindow>(preparedField.GetValue(window)).IsLoaded);
            else
                Assert.Null(preparedField.GetValue(window));

            if (cancel)
                typeof(MainWindow).GetMethod("DiscardPreparedSettingsWindow", Private)!.Invoke(window, null);
            animating.SetValue(window, false);
            await preparation.WaitAsync(ct);

            if (cancel)
                Assert.Null(preparedField.GetValue(window));
            else
                Assert.True(preparedField.GetValue(window) is SettingsWindow { IsLoaded: true, IsVisible: false });
        }
        finally
        {
            SettingsWindow.FirstOpenProbe -= Capture;
            animating.SetValue(window, false);
            if (preparation != null) await preparation.WaitAsync(ct);
        }
    });

    [Fact]
    public void SettingsPreparationWaitsUntilTheNotchStartupEntranceHasFinished() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        bool? afterEntrance = null;
        void Capture(string message)
        {
            if (message.Contains("Settings XAML", StringComparison.Ordinal))
                afterEntrance = (bool)typeof(MainWindow).GetField("_postAppearInitializationStarted", Private)!.GetValue(window)!;
        }
        SettingsWindow.FirstOpenProbe += Capture;
        try
        {
            window.Show();
            Assert.Null(typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window));
            await WpfFrameWaiter.UntilAsync(() =>
                typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window) is SettingsWindow { IsLoaded: true, IsVisible: false },
                "deferred settings preparation completes", ct);
            Assert.True(afterEntrance);
        }
        finally { SettingsWindow.FirstOpenProbe -= Capture; }
    });

    private static bool Animating(MainWindow window) =>
        (bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!;

    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.EnableDiagnosticLogging = false; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
}
