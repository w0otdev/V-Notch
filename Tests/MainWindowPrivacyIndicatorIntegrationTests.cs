using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowPrivacyIndicatorIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CameraStopClearsTheAnimatedDotWhileMicWorkerIsBlockedAndStopNotificationIsMissed()
        => SharedStaTestRunner.RunAsync(async ct =>
    {
        int cameraActive = 1;
        int registryChanged = 0;
        string consumer = Environment.ProcessPath!.Replace('\\', '#');
        int uiThread = Environment.CurrentManagedThreadId;
        var scanThreads = new System.Collections.Concurrent.ConcurrentBag<int>();
        var service = new PrivacyIndicatorService(TimeSpan.FromMilliseconds(50), capability =>
        {
            scanThreads.Add(Environment.CurrentManagedThreadId);
            return capability == "microphone" ||
                (capability == "webcam" && Volatile.Read(ref cameraActive) != 0)
                ? new[] { new CapabilityUsage(consumer, "Recorder", DateTime.UtcNow.ToFileTimeUtc()) }
                : Array.Empty<CapabilityUsage>();
        }, () => Interlocked.Exchange(ref registryChanged, 0) != 0, new ScreenRecordingProbe(() => false));

        var micGate = Field<SemaphoreSlim>(service, "_micFlowGate");
        await micGate.WaitAsync(ct);
        GreetingAcceptanceTests.MainWindowFixture? fixture = null;
        try
        {
            fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
                configureSettings: settings =>
                {
                    settings.EnableLocalOnlyMode = true;
                    settings.EnablePrivacyIndicators = true;
                },
                configureServices: services =>
                {
                    services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService());
                    services.AddSingleton(service);
                });
            var window = fixture.Window;
            window.NotchBorder.Width = 300;
            window.NotchBorder.Height = 40;
            var module = Field<PrivacyIndicatorModule>(window, "_privacyModule");
            var publishThreads = new System.Collections.Concurrent.ConcurrentBag<int>();
            service.StateChanged += (_, _) => publishThreads.Add(Environment.CurrentManagedThreadId);
            module.Start();
            int generation = Field<int>(service, "_currentGeneration");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            for (int cycle = 0; cycle < 2; cycle++)
            {
                if (cycle > 0)
                {
                    Volatile.Write(ref cameraActive, 1);
                    // Starts notify normally; neither stop sends a notification.
                    Interlocked.Exchange(ref registryChanged, 1);
                }
                await WpfFrameWaiter.UntilAsync(() =>
                    window.PrivacyIndicatorPanel.Visibility == Visibility.Visible &&
                    DependencyPropertyHelper.GetValueSource(window.PrivacyDot, UIElement.OpacityProperty).IsAnimated,
                    "camera privacy dot is visible and breathing", deadline.Token);
                Assert.True(service.CurrentState.CameraInUse);
                Assert.False(service.CurrentState.MicrophoneInUse);
                var micWorker = Field<Task>(service, "_micFlowTask");
                Assert.False(micWorker.IsCompleted);
                Assert.Equal(0, micGate.CurrentCount);

                Volatile.Write(ref cameraActive, 0);
                await WpfFrameWaiter.UntilAsync(() =>
                    window.PrivacyIndicatorPanel.Visibility == Visibility.Collapsed &&
                    window.PrivacyDot.Visibility == Visibility.Collapsed,
                    "camera stop hides the privacy dot without releasing the microphone worker", deadline.Token);

                Assert.Equal(PrivacyIndicatorState.Empty, service.CurrentState);
                Assert.False(DependencyPropertyHelper.GetValueSource(window.PrivacyDot, UIElement.OpacityProperty).IsAnimated);
                Assert.Equal(1d, window.PrivacyDot.Opacity);
                Assert.Same(micWorker, Field<Task>(service, "_micFlowTask"));
                Assert.False(micWorker.IsCompleted);
                Assert.True(module.IsRunning);
                Assert.Equal(generation, Field<int>(service, "_currentGeneration"));
                using var running = new PrivacyIndicatorService.ConsumerProcessProbe();
                Assert.True(running.IsRunning(consumer));
            }

            Assert.NotEmpty(scanThreads);
            Assert.All(scanThreads, thread => Assert.NotEqual(uiThread, thread));
            Assert.NotEmpty(publishThreads);
            Assert.All(publishThreads, thread => Assert.Equal(uiThread, thread));
        }
        finally
        {
            // Stop before releasing the gate so no hardware probe escapes the fixture.
            service.Stop();
            micGate.Release();
            service.Dispose();
            try { await service.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { fixture?.Dispose(); }
        }
    });

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
}
