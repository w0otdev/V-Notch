using System.Diagnostics;
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
    [Fact]
    [Trait("Category", "Performance")]
    public void FirstSettingsOpenKeepsTheUiDispatcherResponsive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        window.SettingsPreparation.Schedule();
        await WpfFrameWaiter.UntilAsync(() =>
            window.SettingsPreparation.Prepared is SettingsWindow { IsLoaded: true, IsVisible: false },
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
                window.OpenAppSettings(false);
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
        // Compact startup prepares the notch, without constructing Settings.
        await WpfFrameWaiter.UntilAsync(() => !Animating(window) &&
            window.CoreModulesStarted,
            "notch startup and services finish", ct);
        var coordinator = window.TransitionCoordinator;
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
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => !Animating(window), "notch startup settles", ct);
        void Capture(string message)
        {
            if (pauseAfterConstruction && message.Contains("Settings snapshot", StringComparison.Ordinal))
                window.IsAnimating = true;
        }
        SettingsWindow.FirstOpenProbe += Capture;
        window.IsAnimating = !pauseAfterConstruction;
        Task? preparation = null;
        try
        {
            preparation = window.SettingsPreparation.PrepareAsync(0);
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.False(preparation.IsCompleted);
            if (pauseAfterConstruction)
                Assert.False(Assert.IsType<SettingsWindow>(window.SettingsPreparation.Prepared).IsLoaded);
            else
                Assert.Null(window.SettingsPreparation.Prepared);

            if (cancel)
                window.SettingsPreparation.Close();
            window.IsAnimating = false;
            await preparation.WaitAsync(ct);

            if (cancel)
                Assert.Null(window.SettingsPreparation.Prepared);
            else
                Assert.True(window.SettingsPreparation.Prepared is SettingsWindow { IsLoaded: true, IsVisible: false });
        }
        finally
        {
            SettingsWindow.FirstOpenProbe -= Capture;
            window.IsAnimating = false;
            if (preparation != null) await preparation.WaitAsync(ct);
        }
    });

    [Fact]
    public void SettingsPreparationWaitsForUserIntentAndTheNotchEntrance() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        bool? afterEntrance = null;
        void Capture(string message)
        {
            if (message.Contains("Settings XAML", StringComparison.Ordinal))
                afterEntrance = window.PostAppearInitializationStarted;
        }
        SettingsWindow.FirstOpenProbe += Capture;
        try
        {
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => !Animating(window) &&
                window.CoreModulesStarted,
                "compact services start", ct);
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            Assert.Null(window.SettingsPreparation.Prepared);
            Assert.Null(afterEntrance);
            window.TrayIcon.ContextMenu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.ContextMenu.OpenedEvent));
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            Assert.Null(afterEntrance);
            window.MenuSettings.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
            await WpfFrameWaiter.UntilAsync(() =>
                window.SettingsPreparation.Prepared is SettingsWindow { IsLoaded: true, IsVisible: false },
                "deferred settings preparation completes", ct);
            Assert.True(afterEntrance);
        }
        finally { SettingsWindow.FirstOpenProbe -= Capture; }
    });

    [Fact]
    public void ExpiredSettingsPreparationReleasesTheWindowAndCanBePreparedAgain() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            window.SettingsPreparation.Schedule();
            await WpfFrameWaiter.UntilAsync(() => window.SettingsPreparation.Prepared is SettingsWindow { IsLoaded: true, IsVisible: false },
                "intent prepares settings", ct);
            var standby = (SettingsWindow)window.SettingsPreparation.Prepared!;
            window.SettingsPreparation.Release();
            Assert.Null(window.SettingsPreparation.Prepared);
            Assert.False(window.SettingsPreparation.HasSnapshot);
            Assert.False(window.SettingsPreparation.HasExpiry);
            await WpfFrameWaiter.UntilAsync(() => !Application.Current.Windows.Cast<Window>().Contains(standby),
                "standby settings closes", ct);
        }
    });

    [Fact]
    public void ExpiredSettingsWindowCanBeCollected() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        window.SettingsPreparation.Schedule();
        await WpfFrameWaiter.UntilAsync(() => window.SettingsPreparation.Prepared is SettingsWindow { IsLoaded: true, IsVisible: false },
            "standby settings is ready", ct);
        var reference = ReferenceAndReleasePreparedSettings(window);
        // WPF releases composition/native-window callbacks over subsequent frames.
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        {
            await WpfFrameWaiter.NextAsync(ct);
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive, "Expired Settings must not remain rooted by its owner, timer or event subscriptions.");
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ReferenceAndReleasePreparedSettings(MainWindow window)
    {
        var reference = new WeakReference(window.SettingsPreparation.Prepared!);
        window.SettingsPreparation.Release();
        return reference;
    }

    [Fact]
    public void TakingPreparedSettingsCancelsExpiryWithoutClosingTheActiveWindow() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create();
        var window = fixture.Window;
        window.Show();
        window.SettingsPreparation.Schedule();
        await WpfFrameWaiter.UntilAsync(() => window.SettingsPreparation.Prepared is SettingsWindow { IsLoaded: true, IsVisible: false },
            "settings preparation finishes", ct);
        var standby = (SettingsWindow)window.SettingsPreparation.Prepared!;
        window.OpenAppSettings(false);
        Assert.Same(standby, window.SettingsPreparation.Active);
        Assert.False(window.SettingsPreparation.HasExpiry);
        window.SettingsPreparation.Release();
        Assert.True(standby.IsVisible);
    });

    private static bool Animating(MainWindow window) =>
        window.IsAnimating;

    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false,
        settings => { settings.EnableLocalOnlyMode = true; settings.EnableDiagnosticLogging = false; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
}
