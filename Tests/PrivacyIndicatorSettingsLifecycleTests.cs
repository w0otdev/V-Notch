using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class PrivacyIndicatorSettingsLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void DisabledIndicatorsStopPollingAndReenableUsesFreshActivity() => SharedStaTestRunner.RunAsync(async ct =>
    {
        int scans = 0;
        bool recording = true;
        using var privacy = new PrivacyIndicatorService(TimeSpan.FromMilliseconds(20),
            _ => Array.Empty<CapabilityUsage>(), () => false,
            new ScreenRecordingProbe(() => { Interlocked.Increment(ref scans); return Volatile.Read(ref recording); }));
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => { settings.EnablePrivacyIndicators = false; settings.EnableLocalOnlyMode = true; },
            configureServices: services =>
            {
                services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService());
                services.AddSingleton(_ => privacy);
            });
        var window = fixture.Window;
        var module = Field<PrivacyIndicatorModule>(window, "_privacyModule");
        var settings = Field<NotchSettings>(window, "_settings");
        Assert.False(module.IsRunning);
        Assert.Equal(0, Volatile.Read(ref scans));
        typeof(MainWindow).GetField("_coreModulesStarted", Private)!.SetValue(window, true);
        Apply(window, settings, false);
        Assert.False(module.IsRunning);

        Apply(window, settings, true);
        Assert.True(module.IsRunning);
        await WpfFrameWaiter.UntilAsync(() => module.CurrentState.ScreenRecordingActive &&
            window.PrivacyIndicatorPanel.Visibility == Visibility.Visible, "first privacy evidence", ct);
        var worker = Field<Task>(privacy, "_workerTask");
        Apply(window, settings, false);
        Assert.False(module.IsRunning);
        await worker.WaitAsync(ct);
        await WpfFrameWaiter.UntilAsync(() => window.PrivacyIndicatorPanel.Visibility == Visibility.Collapsed, "disabled privacy dot", ct);
        int stoppedScans = Volatile.Read(ref scans);
        await Task.Delay(80, ct);
        Assert.Equal(stoppedScans, Volatile.Read(ref scans));

        Apply(window, settings, true);
        Assert.False(module.CurrentState.ScreenRecordingActive);
        await WpfFrameWaiter.UntilAsync(() => Volatile.Read(ref scans) > stoppedScans &&
            module.CurrentState.ScreenRecordingActive && window.PrivacyIndicatorPanel.Visibility == Visibility.Visible,
            "same active evidence restored after fresh scan", ct);

        worker = Field<Task>(privacy, "_workerTask");
        Apply(window, settings, false);
        await worker.WaitAsync(ct);
        await WpfFrameWaiter.UntilAsync(() => window.PrivacyIndicatorPanel.Visibility == Visibility.Collapsed, "second disabled privacy dot", ct);
        Volatile.Write(ref recording, false);
        stoppedScans = Volatile.Read(ref scans);
        Apply(window, settings, true);
        Assert.False(module.CurrentState.ScreenRecordingActive);
        await WpfFrameWaiter.UntilAsync(() => Volatile.Read(ref scans) > stoppedScans, "inactive fresh privacy scan", ct);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.False(module.CurrentState.AnyInUse);
        Assert.Equal(Visibility.Collapsed, window.PrivacyIndicatorPanel.Visibility);
    });

    [Fact]
    public void QueuedPrivacyEvidenceIsRejectedAcrossToggleButPreservedWhenSettingsAreReapplied() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var scanRelease = new ManualResetEventSlim(false);
        using var scanEntered = new ManualResetEventSlim(false);
        using var privacy = new PrivacyIndicatorService(TimeSpan.FromMilliseconds(20),
            _ => Array.Empty<CapabilityUsage>(), () => false,
            new ScreenRecordingProbe(() => { scanEntered.Set(); scanRelease.Wait(ct); return false; }));
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureSettings: settings => { settings.EnablePrivacyIndicators = false; settings.EnableLocalOnlyMode = true; },
            configureServices: services =>
            {
                services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService());
                services.AddSingleton(_ => privacy);
            });
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        typeof(MainWindow).GetField("_coreModulesStarted", Private)!.SetValue(window, true);
        try
        {
            Apply(window, settings, true);
            Assert.True(scanEntered.Wait(TimeSpan.FromSeconds(10), ct));
            QueueActiveEvidence(window);
            Apply(window, settings, false);
            Apply(window, settings, true);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            Assert.Equal(Visibility.Collapsed, window.PrivacyIndicatorPanel.Visibility);
            Assert.False(Field<PrivacyIndicatorState>(window, "_lastPrivacyState").AnyInUse);

            QueueActiveEvidence(window);
            typeof(MainWindow).GetMethod("ApplySettings", Private, null, new[] { typeof(NotchSettings), typeof(bool) }, null)!
                .Invoke(window, new object?[] { null, false });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            Assert.Equal(Visibility.Visible, window.PrivacyIndicatorPanel.Visibility);
        }
        finally
        {
            Apply(window, settings, false);
            scanRelease.Set();
        }
    });

    private static void QueueActiveEvidence(MainWindow window)
        => typeof(MainWindow).GetMethod("PrivacyModule_StateChanged", Private)!
            .Invoke(window, new object?[] { null, PrivacyIndicatorState.Empty with { ScreenRecordingActive = true } });

    private static void Apply(MainWindow window, NotchSettings settings, bool enabled)
    {
        var previous = settings.Clone();
        settings.EnablePrivacyIndicators = enabled;
        typeof(MainWindow).GetMethod("ApplySettings", Private, null, new[] { typeof(NotchSettings), typeof(bool) }, null)!
            .Invoke(window, new object?[] { previous, false });
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
}
