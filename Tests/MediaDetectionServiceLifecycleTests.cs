using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public class MediaDetectionServiceLifecycleTests
{
    [Fact]
    public async Task ApplicationShutdownWaitsForWorkerBeyondBestEffortDisposeTimeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateTestService(async _ => { await release.Task; return null; });
        service.Start();
        var drain = service.DrainShutdownAsync();
        try
        {
            await service.DisposeAsync();
            Assert.False(drain.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DisposeAllowsAWorkerToFinishOnTheUiDispatcher() => SharedStaTestRunner.RunAsync(async () =>
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateTestService(async _ =>
        {
            await release.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => { }).Task;
            return null;
        });
        service.Start();
        var init = service.InitTask!;
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            service.Dispose();
            Assert.True(elapsed.ElapsedMilliseconds < 200, "Dispose blocked the dispatcher.");
            Assert.True(service.IsDisposed);
            var shutdown = BeginDispose(service);
            Assert.False(shutdown.IsCompleted);
            Assert.Same(shutdown, BeginDispose(service));
            release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            await init;
            Assert.Null(service.ProcessingTask);
            Assert.Null(service.HeartbeatTask);
        }
        finally
        {
            release.TrySetResult();
            await service.DisposeAsync();
        }
    });

    private static Task BeginDispose(MediaDetectionService service) => service.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutWorkerCanStillReleaseItsUpdateLock(bool stopFirst)
    {
        var service = CreateTestService(null);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var updateLock = (SemaphoreSlim)typeof(MediaDetectionService).GetField("_updateLock", flags)!.GetValue(service)!;
        await updateLock.WaitAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(async () => { await release.Task; updateLock.Release(); });
        typeof(MediaDetectionService).GetField("_processingTask", flags)!.SetValue(service, worker);
        var state = typeof(MediaDetectionService).GetField("_state", flags)!;
        state.SetValue(service, Enum.Parse(state.FieldType, "Running"));
        try
        {
            if (stopFirst) await service.StopAsync();
            await service.DisposeAsync();
            Assert.False(worker.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationDuringFallbackScanPreventsPublishingTheStoppedUpdate()
    {
        using var cts = new CancellationTokenSource();
        using var service = CreateTestService(null, new DummyWindowTitleScanner(() => cts.Cancel()));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        // An empty session list only falls back to captions after recent browser
        // media. Model that state so the cancellation occurs inside the scan.
        typeof(MediaDetectionService).GetField("_lastSource", flags)!.SetValue(service, "Browser");
        typeof(MediaDetectionService).GetField("_emptyMetadataStartTime", flags)!.SetValue(service, DateTime.UtcNow);
        bool published = false;
        service.MediaChanged += (_, _) => published = true;
        var update = typeof(MediaDetectionService).GetMethod("UpdateMediaInfoAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<bool, CancellationToken, Task>>(service);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update(true, cts.Token));
        Assert.False(published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateWaitsForContendedLockUntilCancellation(bool forceRefresh)
    {
        using var service = CreateTestService(null);
        var updateLock = (SemaphoreSlim)typeof(MediaDetectionService).GetField("_updateLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
        var update = typeof(MediaDetectionService).GetMethod("UpdateMediaInfoAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<bool, CancellationToken, Task>>(service);
        using var cts = new CancellationTokenSource();
        await updateLock.WaitAsync();
        try
        {
            var pending = update(forceRefresh, cts.Token);
            var observation = Task.Delay(650);
            Assert.Same(observation, await Task.WhenAny(pending, observation));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { updateLock.Release(); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(7200, true)]
    [InlineData(3456000, false)]
    public void LiveTimelineRecovery_DoesNotSimulateOrReuseRecoveredDuration(double durationSeconds, bool indeterminate)
    {
        using var service = CreateTestService(null);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var simulator = (MediaTimelineSimulator)typeof(MediaDetectionService).GetField("_timelineSimulator", flags)!.GetValue(service)!;
        simulator.RecoveredDuration = TimeSpan.FromMinutes(3);
        simulator.EnterThrottledMode();
        var info = new MediaInfo
        {
            CurrentTrack = "Broadcast",
            MediaSource = "YouTube",
            IsPlaying = true,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            IsIndeterminate = indeterminate,
            Position = TimeSpan.Zero
        };

        typeof(MediaDetectionService).GetMethod("ApplyVideoTimelineRecovery", flags)!
            .Invoke(service, new object?[] { info, new List<string>() });

        Assert.Equal(TimeSpan.Zero, info.Position);
        Assert.Equal(TimeSpan.FromSeconds(durationSeconds), info.Duration);
        Assert.False(info.IsThrottled);
        Assert.False(simulator.IsThrottled);
        Assert.Equal(TimeSpan.Zero, simulator.RecoveredDuration);
    }

    private static MediaDetectionService CreateTestService(
        Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager?>>? factory,
        IWindowTitleScanner? scanner = null)
    {
        return new MediaDetectionService(
            new DummyMetadataLookup(),
            new DummyArtworkService(),
            scanner ?? new DummyWindowTitleScanner(),
            factory);
    }

    [Fact]
    public async Task Start_ConcurrentCallsWhileInitPending_DeduplicatesAndInvokesFactoryOnlyOnce()
    {
        var tcs = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryInvocationCount = 0;

        using var service = CreateTestService(async ct =>
        {
            Interlocked.Increment(ref factoryInvocationCount);
            return await tcs.Task;
        });

        // Call Start() multiple times rapidly
        service.Start();
        service.Start();
        service.Start();

        // Factory should have been invoked only once before init finishes
        Assert.Equal(1, factoryInvocationCount);
        Assert.True(service.IsStarting);

        var firstInitTask = service.InitTask;
        Assert.NotNull(firstInitTask);

        // Complete the initialization
        tcs.SetResult(null);
        await firstInitTask;

        // Service should now be running
        Assert.True(service.IsRunning);
        Assert.NotNull(service.ProcessingTask);
        Assert.NotNull(service.HeartbeatTask);
    }

    [Fact]
    public async Task Dispose_WhileInitIsPending_CancelsCleanlyWithoutExceptionsOrWorkerLoops()
    {
        var tcs = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = CreateTestService(async ct =>
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task;
        });

        service.Start();
        Assert.True(service.IsStarting);

        var initTask = service.InitTask;
        Assert.NotNull(initTask);

        // Dispose while initialization is still awaiting RequestAsync
        ((IDisposable)service).Dispose();

        Assert.True(service.IsDisposed);

        // Unblock tcs if not already canceled
        tcs.TrySetResult(null);

        await initTask;

        // No background worker loops should have been created
        Assert.Null(service.ProcessingTask);
        Assert.Null(service.HeartbeatTask);
    }

    [Fact]
    public async Task StopCancelsPinReconnectDeadlineAndRestartCanArmANewOne()
    {
        await using var service = CreateTestService(_ => Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(null));
        service.Start();
        await service.InitTask!;
        var reconnect = (MediaPinReconnectWindow)typeof(MediaDetectionService)
            .GetField("_pinReconnect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        reconnect.Start();
        Assert.True(reconnect.IsWaiting);
        await service.StopAsync();
        Assert.False(reconnect.IsWaiting);
        service.Start();
        await service.InitTask;
        reconnect.Start();
        Assert.True(reconnect.IsWaiting);
        await service.DisposeAsync();
        Assert.False(reconnect.IsWaiting);
        reconnect.Start();
        Assert.False(reconnect.IsWaiting);
    }

    [Fact]
    public async Task Stop_ThenRestart_RestartsSuccessfully()
    {
        int factoryCallCount = 0;
        using var service = CreateTestService(ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(null);
        });

        // 1. First Start
        service.Start();
        Assert.NotNull(service.InitTask);
        await service.InitTask;

        Assert.True(service.IsRunning);
        Assert.Equal(1, factoryCallCount);
        Assert.NotNull(service.ProcessingTask);

        // 2. Stop
        await service.StopAsync();

        Assert.True(service.IsStopped);
        Assert.Null(service.ProcessingTask);
        Assert.Null(service.HeartbeatTask);

        // 3. Restart
        service.Start();
        Assert.NotNull(service.InitTask);
        await service.InitTask;

        Assert.True(service.IsRunning);
        Assert.Equal(2, factoryCallCount);
        Assert.NotNull(service.ProcessingTask);
        Assert.NotNull(service.HeartbeatTask);
    }

    [Fact]
    public void Dispose_MultipleTimes_IsIdempotent()
    {
        using var service = CreateTestService(null);
        service.Dispose();
        service.Dispose(); // Should not throw
        Assert.True(service.IsDisposed);
    }

    [Fact]
    public void Stop_MultipleTimes_IsIdempotent()
    {
        using var service = CreateTestService(null);
        service.Stop();
        service.Stop(); // Should not throw
        Assert.True(service.IsStopped);
    }

    [Fact]
    public async Task StartingWhileStopIsPendingDefersRestartUntilOldInitializationExits()
    {
        var firstInitialization = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;
        await using var service = CreateTestService(_ =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1) return firstInitialization.Task;
            restarted.TrySetResult();
            return Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(null);
        });
        service.Start();
        var stopping = service.StopAsync();
        Assert.False(stopping.IsCompleted);
        service.Start();
        service.Start();
        Assert.Equal(1, factoryCalls);
        firstInitialization.SetResult(null);
        await stopping;
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The factory signal can precede Start's assignment of InitTask.
        // Wait for publication, rather than racing that assignment.
        Assert.True(SpinWait.SpinUntil(() => service.InitTask != null, TimeSpan.FromSeconds(5)));
        await service.InitTask!;
        Assert.Equal(2, factoryCalls);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public void Start_AfterDispose_DoesNothing()
    {
        int factoryCallCount = 0;
        using var service = CreateTestService(ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(null);
        });

        service.Dispose();
        service.Start();

        Assert.Equal(0, factoryCallCount);
        Assert.True(service.IsDisposed);
        Assert.Null(service.InitTask);
    }

    [Fact]
    public async Task Start_WhenExceptionInFactory_ResetsToStoppedStateForRetry()
    {
        int attempts = 0;
        using var service = CreateTestService(ct =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("Simulated SMTC failure");
            }
            return Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(null);
        });

        // First start attempt fails
        service.Start();
        Assert.NotNull(service.InitTask);
        await service.InitTask;

        Assert.True(service.IsStopped);

        // Second start attempt succeeds
        service.Start();
        Assert.NotNull(service.InitTask);
        await service.InitTask;

        Assert.True(service.IsRunning);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void SuppressIntermediateYouTubeThumbnail_WhenWillFetchYouTubeThumbnail_AssignsCachedThumbnail()
    {
        using var service = CreateTestService(null);
        var cached = CreateTestBitmap(100, 100);
        service.CachedThumbnail = cached;

        var info = new MediaInfo
        {
            MediaSource = "YouTube",
            CurrentTrack = "Test Video",
            Thumbnail = null
        };

        service.SuppressIntermediateYouTubeThumbnail(info, isNewTrackForThumbnail: false);

        Assert.Same(cached, info.Thumbnail);
    }

    [Fact]
    public void SuppressIntermediateYouTubeThumbnail_NewTrackWithWideThumbnail_SuppressesThumbnail()
    {
        using var service = CreateTestService(null);
        var wideThumb = CreateTestBitmap(160, 90); // Aspect ratio ~1.77 > 1.3

        var info = new MediaInfo
        {
            MediaSource = "YouTube",
            CurrentTrack = "New Song",
            Thumbnail = wideThumb
        };

        service.SuppressIntermediateYouTubeThumbnail(info, isNewTrackForThumbnail: true);

        Assert.Null(info.Thumbnail);
    }

    [Fact]
    public void SuppressIntermediateYouTubeThumbnail_NewTrackWithSquareThumbnail_PreservesThumbnail()
    {
        using var service = CreateTestService(null);
        var squareThumb = CreateTestBitmap(100, 100); // Aspect ratio 1.0 <= 1.3

        var info = new MediaInfo
        {
            MediaSource = "YouTube",
            CurrentTrack = "New Song",
            Thumbnail = squareThumb
        };

        service.SuppressIntermediateYouTubeThumbnail(info, isNewTrackForThumbnail: true);

        Assert.Same(squareThumb, info.Thumbnail);
    }

    [Fact]
    public async Task StopAsync_WhileDownloadImageAsyncPending_CancelsThumbnailTokenImmediately()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var downloadStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var artwork = new DelegatingArtworkService(async (_, ct) =>
        {
            downloadStarted.TrySetResult(ct);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });

        await using var service = new MediaDetectionService(
            new DummyMetadataLookup(),
            artwork,
            new DummyWindowTitleScanner(),
            _ => Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(null));

        using var bgCts = new CancellationTokenSource();
        typeof(MediaDetectionService).GetField("_bgCts", flags)!.SetValue(service, bgCts);
        var stateField = typeof(MediaDetectionService).GetField("_state", flags)!;
        stateField.SetValue(service, Enum.Parse(stateField.FieldType, "Running"));
        typeof(MediaDetectionService).GetField("_processingTask", flags)!.SetValue(service, workerRelease.Task);

        var info = new MediaInfo
        {
            MediaSource = "YouTube",
            CurrentTrack = "Test Video",
            CurrentArtist = "Test Channel",
            YouTubeVideoId = "dQw4w9WgXcQ",
            SourceAppId = "Chrome",
            SessionInstanceKey = "Chrome#1",
            IsPlaying = true,
            IsAnyMediaPlaying = true
        };

        typeof(MediaDetectionService).GetMethod("CommitPublishedState", flags)!
            .Invoke(service, new object?[] { info, info.GetSignature() });
        typeof(MediaDetectionService).GetMethod("StartThumbnailFetchIfNeeded", flags)!
            .Invoke(service, new object?[] { info, false });

        var downloadToken = await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(downloadToken.IsCancellationRequested);

        var stopTask = service.StopAsync();
        try
        {
            Assert.True(downloadToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)),
                "Expected StopAsync to cancel the linked thumbnail token immediately via _bgCts.");
            Assert.True(downloadToken.IsCancellationRequested);
        }
        finally
        {
            workerRelease.TrySetResult();
        }

        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(typeof(MediaDetectionService).GetField("_thumbCts", flags)!.GetValue(service));
    }

    [Fact]
    public async Task ThumbnailFetch_WhenCompletedNormally_ClearsAndDisposesThumbCts()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDownloadComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testBitmap = CreateTestBitmap(480, 360);

        var artwork = new DelegatingArtworkService(async (_, ct) =>
        {
            downloadStarted.TrySetResult();
            await allowDownloadComplete.Task.WaitAsync(ct);
            return testBitmap;
        });

        await using var service = new MediaDetectionService(
            new DummyMetadataLookup(),
            artwork,
            new DummyWindowTitleScanner(),
            _ => Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(null));

        using var bgCts = new CancellationTokenSource();
        typeof(MediaDetectionService).GetField("_bgCts", flags)!.SetValue(service, bgCts);
        var stateField = typeof(MediaDetectionService).GetField("_state", flags)!;
        stateField.SetValue(service, Enum.Parse(stateField.FieldType, "Running"));

        var info = new MediaInfo
        {
            MediaSource = "YouTube",
            CurrentTrack = "Completed Video",
            CurrentArtist = "Test Channel",
            YouTubeVideoId = "dQw4w9WgXcQ",
            SourceAppId = "Chrome",
            SessionInstanceKey = "Chrome#1",
            IsPlaying = true,
            IsAnyMediaPlaying = true
        };

        typeof(MediaDetectionService).GetMethod("CommitPublishedState", flags)!
            .Invoke(service, new object?[] { info, info.GetSignature() });
        typeof(MediaDetectionService).GetMethod("StartThumbnailFetchIfNeeded", flags)!
            .Invoke(service, new object?[] { info, false });

        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var thumbCtsField = typeof(MediaDetectionService).GetField("_thumbCts", flags)!;
        var capturedCts = (CancellationTokenSource?)thumbCtsField.GetValue(service);
        Assert.NotNull(capturedCts);

        allowDownloadComplete.TrySetResult();

        Assert.True(SpinWait.SpinUntil(() => thumbCtsField.GetValue(service) == null, TimeSpan.FromSeconds(5)),
            "Expected _thumbCts to be cleared after thumbnail fetch completes normally.");
        Assert.Null(thumbCtsField.GetValue(service));
        Assert.Throws<ObjectDisposedException>(() => _ = capturedCts!.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveActiveSessionAsync_WhenNonCurrentActiveDisplaySessionDisappearsOrIsIgnored_UnsubscribesEventsAndDetaches(bool sessionDisappears)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fakeSession = new FakeSmtcEnv.FakeSmtcSession("Music.UI.exe", "Active Track", "Active Artist");
        var sessions = new List<GlobalSystemMediaTransportControlsSession> { fakeSession.Session };
        var manager = FakeSmtcEnv.CreateManager(
            getCurrentSession: () => null,
            getSessions: () => sessions.ToArray());

        await using var service = CreateTestService(_ => Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(manager));
        typeof(MediaDetectionService).GetField("_sessionManager", flags)!.SetValue(service, manager);

        typeof(MediaDetectionService).GetMethod("SwitchActiveDisplaySessionIfNeeded", flags)!
            .Invoke(service, new object?[] { fakeSession.Session });

        var activeDisplayField = typeof(MediaDetectionService).GetField("_activeDisplaySession", flags)!;
        Assert.NotNull(activeDisplayField.GetValue(service));
        Assert.Equal(1, fakeSession.TimelineActiveSubscriptions);
        Assert.Equal(1, fakeSession.PlaybackActiveSubscriptions);
        Assert.Equal(1, fakeSession.MediaPropertiesActiveSubscriptions);

        if (sessionDisappears)
        {
            sessions.Clear();
        }
        else
        {
            fakeSession.SourceAppId = "V-Notch.exe";
        }

        var resolveTask = (Task)typeof(MediaDetectionService).GetMethod("ResolveActiveSessionAsync", flags)!
            .Invoke(service, new object?[] { false })!;
        await resolveTask;

        Assert.Null(activeDisplayField.GetValue(service));
        Assert.Equal(0, fakeSession.TimelineActiveSubscriptions);
        Assert.Equal(0, fakeSession.PlaybackActiveSubscriptions);
        Assert.Equal(0, fakeSession.MediaPropertiesActiveSubscriptions);
        Assert.Equal(1, fakeSession.TimelineRemoveCalls);
        Assert.Equal(1, fakeSession.PlaybackRemoveCalls);
        Assert.Equal(1, fakeSession.MediaPropertiesRemoveCalls);
    }

    [Fact]
    public async Task TryGetMediaSessionInfoAsync_WhenResolveActiveSessionAsyncFetchesMediaProperties_CallsTryGetMediaPropertiesAsyncOnlyOnce()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fakeSession = new FakeSmtcEnv.FakeSmtcSession("Music.UI.exe", "Single Fetch Track", "Single Fetch Artist");
        var sessions = new List<GlobalSystemMediaTransportControlsSession> { fakeSession.Session };
        var manager = FakeSmtcEnv.CreateManager(
            getCurrentSession: () => fakeSession.Session,
            getSessions: () => sessions.ToArray());

        await using var service = CreateTestService(_ => Task.FromResult<GlobalSystemMediaTransportControlsSessionManager?>(manager));
        typeof(MediaDetectionService).GetField("_sessionManager", flags)!.SetValue(service, manager);

        var tryGetMethod = typeof(MediaDetectionService).GetMethod("TryGetMediaSessionInfoAsync", flags)!;

        // Cycle 1: Initial full session scan (forceRefresh: true)
        var firstInfo = new MediaInfo();
        await (Task<bool>)tryGetMethod.Invoke(service, new object?[] { firstInfo, true, (Func<List<string>>)(() => new List<string>()) })!;

        Assert.Equal("Single Fetch Track", firstInfo.CurrentTrack);
        Assert.Equal("Single Fetch Artist", firstInfo.CurrentArtist);
        Assert.Equal(1, fakeSession.TryGetMediaPropertiesCalls);

        // Cycle 2: Active display session fast path (forceRefresh: false)
        fakeSession.ResetTryGetMediaPropertiesCalls();
        var secondInfo = new MediaInfo();
        await (Task<bool>)tryGetMethod.Invoke(service, new object?[] { secondInfo, false, (Func<List<string>>)(() => new List<string>()) })!;

        Assert.Equal("Single Fetch Track", secondInfo.CurrentTrack);
        Assert.Equal("Single Fetch Artist", secondInfo.CurrentArtist);
        Assert.Equal(1, fakeSession.TryGetMediaPropertiesCalls);
    }

    private static System.Windows.Media.Imaging.BitmapImage CreateTestBitmap(int width, int height)
    {
        var src = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    #region Dummy Services

    private sealed class DelegatingArtworkService : IMediaArtworkService
    {
        private readonly Func<string, CancellationToken, Task<System.Windows.Media.Imaging.BitmapImage?>> _download;

        public DelegatingArtworkService(Func<string, CancellationToken, Task<System.Windows.Media.Imaging.BitmapImage?>> download)
            => _download = download;

        public Task<System.Windows.Media.Imaging.BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
            => _download(url, ct);

        public System.Windows.Media.Imaging.BitmapSource? CropToSquare(System.Windows.Media.Imaging.BitmapSource source, string mediaSource, bool forceCenterCrop = false)
            => source;

        public Task<System.Windows.Media.Imaging.BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
            => Task.FromResult<System.Windows.Media.Imaging.BitmapImage?>(null);

        public void ConfigureSmartCrop(bool enabled) { }

        public SubjectBounds? GetDominantSubjectBounds(System.Windows.Media.Imaging.BitmapSource source)
            => null;
    }

    private static class FakeSmtcEnv
    {
        private static readonly Guid IUnknownIid = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IInspectableIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
        private static readonly Guid IAgileObjectIid = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
        private static readonly Guid SessionManagerIid = new("CACE8EAC-E86E-504A-AB31-5FF8FF1BCE49");
        private static readonly Guid SessionIid = new("7148C835-9B14-5AE2-AB85-DC9B1C14E1A8");
        private static readonly Guid MediaPropsIid = new("68856CF6-ADB4-54B2-AC16-05837907ACB6");
        private static readonly Guid PlaybackInfoIid = new("94B4B6CF-E8BA-51AD-87A7-C10ADE106127");

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(IntPtr thisPtr, ref Guid iid, out IntPtr ppv);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate uint AddRefReleaseDelegate(IntPtr thisPtr);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate int GetIidsDelegate(IntPtr thisPtr, out uint iidCount, out IntPtr iids);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate int GetPtrDelegate(IntPtr thisPtr, out IntPtr value);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate int GetInt32Delegate(IntPtr thisPtr, out int value);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate int AddEventDelegate(IntPtr thisPtr, IntPtr handler, out long token);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate int RemoveEventDelegate(IntPtr thisPtr, long token);

        private static readonly object PinLock = new();
        private static readonly List<Delegate> PinnedDelegates = new();

        private static IntPtr PinDelegate(Delegate d)
        {
            lock (PinLock)
            {
                PinnedDelegates.Add(d);
            }
            return System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(d);
        }

        private static IntPtr CreateInstance(Guid interfaceIid, int slotCount, Action<IntPtr[]> configureSlots)
        {
            var slots = new IntPtr[slotCount];
            slots[0] = PinDelegate(new QueryInterfaceDelegate((IntPtr self, ref Guid iid, out IntPtr ppv) =>
            {
                if (iid == IUnknownIid || iid == IInspectableIid || iid == IAgileObjectIid || iid == interfaceIid)
                {
                    ppv = self;
                    return 0;
                }
                ppv = IntPtr.Zero;
                return unchecked((int)0x80004002);
            }));
            slots[1] = PinDelegate(new AddRefReleaseDelegate(_ => 2));
            slots[2] = PinDelegate(new AddRefReleaseDelegate(_ => 1));
            slots[3] = PinDelegate(new GetIidsDelegate((IntPtr _, out uint count, out IntPtr iids) => { count = 0; iids = IntPtr.Zero; return 0; }));
            slots[4] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr name) => { name = IntPtr.Zero; return 0; }));
            slots[5] = PinDelegate(new GetInt32Delegate((IntPtr _, out int trust) => { trust = 0; return 0; }));

            configureSlots(slots);

            IntPtr vtbl = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size * slotCount);
            for (int i = 0; i < slotCount; i++)
                System.Runtime.InteropServices.Marshal.WriteIntPtr(vtbl, i * IntPtr.Size, slots[i]);

            IntPtr instance = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size);
            System.Runtime.InteropServices.Marshal.WriteIntPtr(instance, 0, vtbl);
            return instance;
        }

        public static GlobalSystemMediaTransportControlsSessionManager CreateManager(
            Func<GlobalSystemMediaTransportControlsSession?> getCurrentSession,
            Func<IReadOnlyList<GlobalSystemMediaTransportControlsSession>> getSessions)
        {
            IntPtr ptr = CreateInstance(SessionManagerIid, 12, slots =>
            {
                slots[6] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr val) =>
                {
                    var current = getCurrentSession();
                    val = current != null ? WinRT.MarshalInspectable<GlobalSystemMediaTransportControlsSession>.FromManaged(current) : IntPtr.Zero;
                    return 0;
                }));
                slots[7] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr val) =>
                {
                    val = WinRT.MarshalInterface<IReadOnlyList<GlobalSystemMediaTransportControlsSession>>.FromManaged(getSessions());
                    return 0;
                }));
                slots[8] = PinDelegate(new AddEventDelegate((IntPtr _, IntPtr _, out long token) => { token = 1; return 0; }));
                slots[9] = PinDelegate(new RemoveEventDelegate((IntPtr _, long _) => 0));
                slots[10] = PinDelegate(new AddEventDelegate((IntPtr _, IntPtr _, out long token) => { token = 1; return 0; }));
                slots[11] = PinDelegate(new RemoveEventDelegate((IntPtr _, long _) => 0));
            });
            return GlobalSystemMediaTransportControlsSessionManager.FromAbi(ptr);
        }

        public sealed class FakeSmtcSession
        {
            public string SourceAppId { get; set; }
            public string Title { get; set; }
            public string Artist { get; set; }
            public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; set; }
            public GlobalSystemMediaTransportControlsSession Session { get; }
            public GlobalSystemMediaTransportControlsSessionMediaProperties MediaProperties { get; }
            public GlobalSystemMediaTransportControlsSessionPlaybackInfo PlaybackInfo { get; }

            public int TryGetMediaPropertiesCalls => Volatile.Read(ref _tryGetMediaPropertiesCalls);
            public int TimelineActiveSubscriptions => Volatile.Read(ref _timelineActive);
            public int PlaybackActiveSubscriptions => Volatile.Read(ref _playbackActive);
            public int MediaPropertiesActiveSubscriptions => Volatile.Read(ref _mediaPropsActive);
            public int TimelineRemoveCalls => Volatile.Read(ref _timelineRemoved);
            public int PlaybackRemoveCalls => Volatile.Read(ref _playbackRemoved);
            public int MediaPropertiesRemoveCalls => Volatile.Read(ref _mediaPropsRemoved);

            private int _tryGetMediaPropertiesCalls;
            private int _timelineActive;
            private int _playbackActive;
            private int _mediaPropsActive;
            private int _timelineRemoved;
            private int _playbackRemoved;
            private int _mediaPropsRemoved;
            private long _nextToken;

            public void ResetTryGetMediaPropertiesCalls() => Interlocked.Exchange(ref _tryGetMediaPropertiesCalls, 0);

            public FakeSmtcSession(
                string sourceAppId,
                string title = "Song Title",
                string artist = "Song Artist",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                SourceAppId = sourceAppId;
                Title = title;
                Artist = artist;
                PlaybackStatus = status;

                IntPtr propsPtr = CreateInstance(MediaPropsIid, 18, slots =>
                {
                    slots[6] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr hstr) => { hstr = WinRT.MarshalString.FromManaged(Title); return 0; }));
                    slots[7] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr hstr) => { hstr = WinRT.MarshalString.FromManaged(""); return 0; }));
                    slots[8] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr hstr) => { hstr = WinRT.MarshalString.FromManaged(""); return 0; }));
                    slots[9] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr hstr) => { hstr = WinRT.MarshalString.FromManaged(Artist); return 0; }));
                    slots[10] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr hstr) => { hstr = WinRT.MarshalString.FromManaged(""); return 0; }));
                    slots[15] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr thumb) => { thumb = IntPtr.Zero; return 0; }));
                });
                MediaProperties = GlobalSystemMediaTransportControlsSessionMediaProperties.FromAbi(propsPtr);

                IntPtr pbPtr = CreateInstance(PlaybackInfoIid, 13, slots =>
                {
                    slots[6] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr ctrl) => { ctrl = IntPtr.Zero; return 0; }));
                    slots[7] = PinDelegate(new GetInt32Delegate((IntPtr _, out int st) => { st = (int)PlaybackStatus; return 0; }));
                    slots[10] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr rate) => { rate = IntPtr.Zero; return 0; }));
                });
                PlaybackInfo = GlobalSystemMediaTransportControlsSessionPlaybackInfo.FromAbi(pbPtr);

                IntPtr sessionPtr = CreateInstance(SessionIid, 31, slots =>
                {
                    slots[6] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr hstr) => { hstr = WinRT.MarshalString.FromManaged(SourceAppId); return 0; }));
                    slots[7] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr asyncOp) =>
                    {
                        Interlocked.Increment(ref _tryGetMediaPropertiesCalls);
                        var op = Task.FromResult(MediaProperties).AsAsyncOperation();
                        asyncOp = WinRT.MarshalInterface<global::Windows.Foundation.IAsyncOperation<GlobalSystemMediaTransportControlsSessionMediaProperties>>.FromManaged(op);
                        return 0;
                    }));
                    slots[8] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr tl) => { tl = IntPtr.Zero; return 0; }));
                    slots[9] = PinDelegate(new GetPtrDelegate((IntPtr _, out IntPtr pb) =>
                    {
                        pb = WinRT.MarshalInspectable<GlobalSystemMediaTransportControlsSessionPlaybackInfo>.FromManaged(PlaybackInfo);
                        return 0;
                    }));
                    slots[25] = PinDelegate(new AddEventDelegate((IntPtr _, IntPtr _, out long token) =>
                    {
                        Interlocked.Increment(ref _timelineActive);
                        token = Interlocked.Increment(ref _nextToken);
                        return 0;
                    }));
                    slots[26] = PinDelegate(new RemoveEventDelegate((IntPtr _, long _) =>
                    {
                        Interlocked.Increment(ref _timelineRemoved);
                        Interlocked.Decrement(ref _timelineActive);
                        return 0;
                    }));
                    slots[27] = PinDelegate(new AddEventDelegate((IntPtr _, IntPtr _, out long token) =>
                    {
                        Interlocked.Increment(ref _playbackActive);
                        token = Interlocked.Increment(ref _nextToken);
                        return 0;
                    }));
                    slots[28] = PinDelegate(new RemoveEventDelegate((IntPtr _, long _) =>
                    {
                        Interlocked.Increment(ref _playbackRemoved);
                        Interlocked.Decrement(ref _playbackActive);
                        return 0;
                    }));
                    slots[29] = PinDelegate(new AddEventDelegate((IntPtr _, IntPtr _, out long token) =>
                    {
                        Interlocked.Increment(ref _mediaPropsActive);
                        token = Interlocked.Increment(ref _nextToken);
                        return 0;
                    }));
                    slots[30] = PinDelegate(new RemoveEventDelegate((IntPtr _, long _) =>
                    {
                        Interlocked.Increment(ref _mediaPropsRemoved);
                        Interlocked.Decrement(ref _mediaPropsActive);
                        return 0;
                    }));
                });
                Session = GlobalSystemMediaTransportControlsSession.FromAbi(sessionPtr);
            }
        }
    }

    private sealed class DummyMetadataLookup : IMediaMetadataLookupService
    {
        public Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default)
            => Task.FromResult<YouTubeLookupResult?>(null);

        public Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
            => Task.FromResult<YouTubeLookupResult?>(null);

        public Task<YouTubeLookupResult?> TrySearchYouTubeByTitleAsync(string title, string artist = "", CancellationToken ct = default)
            => Task.FromResult<YouTubeLookupResult?>(null);

        public Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class DummyArtworkService : IMediaArtworkService
    {
        public Task<System.Windows.Media.Imaging.BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
            => Task.FromResult<System.Windows.Media.Imaging.BitmapImage?>(null);

        public System.Windows.Media.Imaging.BitmapSource? CropToSquare(System.Windows.Media.Imaging.BitmapSource source, string mediaSource, bool forceCenterCrop = false)
            => source;

        public Task<System.Windows.Media.Imaging.BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
            => Task.FromResult<System.Windows.Media.Imaging.BitmapImage?>(null);

        public void ConfigureSmartCrop(bool enabled) { }

        public SubjectBounds? GetDominantSubjectBounds(System.Windows.Media.Imaging.BitmapSource source)
            => null;
    }

    private sealed class DummyWindowTitleScanner : IWindowTitleScanner
    {
        private readonly Action? _onScan;
        public DummyWindowTitleScanner(Action? onScan = null) => _onScan = onScan;
        public List<string> GetAllWindowTitles(bool isThrottled)
        {
            _onScan?.Invoke();
            return new();
        }
        public string? TryGetBrowserUrl() => null;
        public string? TryGetMediaUrlFromAnyBrowser() => null;
        public bool IsSpotifyWebPlayerOpen() => false;
        public bool IsPipActive(string? processName = null) => false;
        public bool TryGetPipWindow(out IntPtr pipHwnd, out string pipTitle, string? processName = null)
        {
            pipHwnd = IntPtr.Zero;
            pipTitle = "";
            return false;
        }
        public void InvalidateUrlCaches() { }
    }

    #endregion
}
