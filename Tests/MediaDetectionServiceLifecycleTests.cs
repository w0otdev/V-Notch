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
            var shutdown = service.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.Same(shutdown, service.DisposeAsync().AsTask());
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
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { updateLock.Release(); }
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
        service.Dispose();

        Assert.True(service.IsDisposed);

        // Unblock tcs if not already canceled
        tcs.TrySetResult(null);

        await initTask;

        // No background worker loops should have been created
        Assert.Null(service.ProcessingTask);
        Assert.Null(service.HeartbeatTask);
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
        service.Stop();

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
