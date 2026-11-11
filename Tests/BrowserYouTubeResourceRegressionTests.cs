using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Vortice.Direct3D9;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class BrowserYouTubeResourceRegressionTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SmartCropYouTubeThumbnailRunsOnCpuWithoutLoadingDirectMl() => SharedStaTestRunner.Run(() =>
    {
        bool directMlPreloaded = GetModuleHandleW("DirectML.dll") != IntPtr.Zero;
        using var artwork = new MediaArtworkService { EnableSmartCrop = true };
        var source = CreateDetailedWidescreenBitmap(320, 180);

        var cropped = artwork.CropToSquare(source, "YouTube");

        Assert.NotNull(cropped);
        Assert.Equal(cropped!.PixelWidth, cropped.PixelHeight);
        Assert.InRange(cropped.PixelWidth, 168, 180);
        if (!directMlPreloaded)
        {
            Assert.Equal(IntPtr.Zero, GetModuleHandleW("DirectML.dll"));
        }
    });

    [Fact]
    public void SteadyStateBrowserPlaybackSkipsUrlScanAndSmtcReopenAndDeduplicatesRetries() => SharedStaTestRunner.RunAsync(async () =>
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vnotch-yt-reg-" + Guid.NewGuid().ToString("N"));
        var metadata = new NoopMetadataLookup();
        var artwork = new PassThroughArtwork();
        var scanner = new CountingScanner { AnyUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" };
        var cache = new MediaSourceCache(Path.Combine(tempDir, "sources.json"));
        using var service = new MediaDetectionService(metadata, artwork, scanner, null, cache);
        try
        {
            var stateField = typeof(MediaDetectionService).GetField("_state", Private)!;
            stateField.SetValue(service, Enum.Parse(stateField.FieldType, "Running"));

            var thumb = CreateDetailedWidescreenBitmap(320, 180);
            var info = new MediaInfo
            {
                CurrentTrack = "Sample Video",
                CurrentArtist = "Sample Channel",
                MediaSource = "Browser",
                SourceAppId = "chrome.exe",
                SessionInstanceKey = "chrome-session-1",
                Thumbnail = thumb
            };

            Invoke(service, "StartThumbnailFetchIfNeeded", info, false);
            Assert.Equal(0, scanner.AnyUrlCalls);

            Invoke(service, "StartThumbnailFetchIfNeeded", info, true);
            Assert.Equal(1, scanner.AnyUrlCalls);

            // Seed steady-state verified session artwork (>6s after track metadata settled).
            service.CachedThumbnail = thumb;
            Set(service, "_thumbnailFetchGeneration", 5);
            Set(service, "_sessionArtworkGeneration", 5);
            Set(service, "_lastSessionArtworkFingerprint", ArtworkFingerprint.Create(thumb));
            Set(service, "_lastMetadataChangeTime", DateTime.UtcNow - TimeSpan.FromSeconds(10));
            Set(service, "_lastPublishedSessionInstanceKey", "chrome-session-1");

            var steadyInfo = info.Clone();
            steadyInfo.Thumbnail = null;
            await (Task)Invoke(service, "ApplySessionThumbnailWithRefreshAsync", steadyInfo, null, null, false, false)!;
            Assert.Same(thumb, steadyInfo.Thumbnail);

            // Deduplicate deferred 250ms/750ms heartbeat tasks per thumbnail fetch generation.
            using var cts = new CancellationTokenSource();
            Set(service, "_bgCts", cts);
            service.CachedThumbnail = null;
            Set(service, "_thumbnailFetchGeneration", 9);

            Assert.True((bool)Invoke(service, "ScheduleDeferredSessionThumbnailRefreshIfNeeded")!);
            Assert.False((bool)Invoke(service, "ScheduleDeferredSessionThumbnailRefreshIfNeeded")!);
            Assert.False((bool)Invoke(service, "ScheduleDeferredSessionThumbnailRefreshIfNeeded")!);
            Assert.Equal(9, Field<int>(service, "_deferredThumbRefreshGeneration"));
            cts.Cancel();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    });

    [Fact]
    public void EmptyYouTubeVideoIdOnRepeatedMediaUpdatesDoesNotRefetchMissingSubtitles() => SharedStaTestRunner.RunAsync(async ct =>
    {
        int lookupCalls = 0;
        int subtitleHttpCalls = 0;
        var fakeMedia = new FakeMediaDetectionService
        {
            LookupYouTubeVideoId = (_, _, _) =>
            {
                Interlocked.Increment(ref lookupCalls);
                return Task.FromResult<YouTubeLookupResult?>(new YouTubeLookupResult { Id = "dQw4w9WgXcQ" });
            }
        };

        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture(
            "en",
            greeting: false,
            settings =>
            {
                settings.EnableLocalOnlyMode = false;
                settings.EnableYouTubeSubtitles = true;
                settings.DisableMouseLeaveAutoClose = true;
            },
            services => services.AddSingleton<IMediaDetectionService>(fakeMedia));

        var window = fixture.Window;
        using var httpClient = new HttpClient(new CountingEmptyResponseHandler(() => Interlocked.Increment(ref subtitleHttpCalls)));
        Field<YouTubeSubtitleService>(window, "_youtubeSubtitleService").Dispose();
        Set(window, "_youtubeSubtitleService", new YouTubeSubtitleService(httpClient));
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        window.SetDebugViewLock(true);
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !coordinator.IsTransitionActive, "startup", ct);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "expand", ct);

        MediaInfo BuildTick() => new()
        {
            CurrentTrack = "Uncaptioned Video",
            CurrentArtist = "Creator",
            MediaSource = "YouTube",
            SourceAppId = "chrome.exe",
            SessionInstanceKey = "chrome-1",
            IsAnyMediaPlaying = true,
            IsPlaying = true,
            YouTubeVideoId = "",
            Duration = TimeSpan.FromMinutes(5),
            Position = TimeSpan.FromSeconds(12)
        };

        InvokeWindow(window, "ApplyMediaUpdate", BuildTick());
        await WpfFrameWaiter.UntilAsync(
            () => Field<int>(window, "_subtitleFetchFailureCount") >= 1,
            "initial subtitle lookup finished",
            ct);

        int initialHttpCalls = Volatile.Read(ref subtitleHttpCalls);
        Assert.Equal(1, Volatile.Read(ref lookupCalls));
        Assert.Equal("dQw4w9WgXcQ", Field<MediaInfo>(window, "_currentMediaInfo").YouTubeVideoId);

        for (int i = 0; i < 5; i++)
        {
            var nextTick = BuildTick();
            nextTick.Position = TimeSpan.FromSeconds(13 + i);
            InvokeWindow(window, "ApplyMediaUpdate", nextTick);
        }

        await Task.Delay(80, ct);
        Assert.Equal(1, Volatile.Read(ref lookupCalls));
        Assert.Equal(initialHttpCalls, Volatile.Read(ref subtitleHttpCalls));
        Assert.Equal(1, Field<int>(window, "_subtitleFetchFailureCount"));
        Assert.Equal("dQw4w9WgXcQ", Field<MediaInfo>(window, "_currentMediaInfo").YouTubeVideoId);
    });

    [Fact]
    public unsafe void GpuGlassRenderPixelsSkipsRedundantStaticUploadsAndRerendersOnChange() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { Width = 64, Height = 64, ShowInTaskbar = false, ShowActivated = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        IDirect3D9Ex? d3d9 = null;
        IDirect3DDevice9Ex? device9 = null;
        long gpuHandle = 0;
        try
        {
            try
            {
                d3d9 = D3D9.Direct3DCreate9Ex();
                var presentParams = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    BackBufferFormat = Format.Unknown,
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                    BackBufferCount = 1,
                    DeviceWindowHandle = hwnd,
                    PresentationInterval = PresentInterval.Immediate
                };
                device9 = d3d9.CreateDeviceEx(
                    0,
                    DeviceType.Hardware,
                    hwnd,
                    CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                    presentParams);
            }
            catch
            {
                return;
            }

            if (vn_glass_gpu_create(device9.NativePointer, 256, 128, out gpuHandle) != 0 || gpuHandle == 0)
                return;

            using var destSurface = device9.CreateRenderTarget(256, 128, Format.A8R8G8B8, MultisampleType.None, 0, lockable: false);

            const int w = 64, h = 32, stride = w * 4;
            byte[] pixels = new byte[stride * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)((i * 31 + 17) & 0xFF);

            var frame = new D3DImageFramePresenter.GpuFrame
            {
                X = 0,
                Y = 0,
                Width = w,
                Height = h,
                Blur = 12f,
                Refract = 0
            };

            fixed (byte* ptr = pixels)
            {
                int hrFirst = vn_glass_gpu_render_pixels(gpuHandle, in frame, (IntPtr)ptr, pixels.Length, stride);
                Assert.Equal(0, hrFirst);
                Assert.Equal(1UL, vn_glass_gpu_take_capture_count(gpuHandle));

                _ = vn_glass_gpu_wait_ready(gpuHandle, 8);
                System.Threading.SpinWait.SpinUntil(() => vn_glass_gpu_present_ready(gpuHandle, destSurface.NativePointer) == 0, 50);

                // Identical pixels + identical frame parameters must skip GPU upload & render (S_FALSE == 1).
                int hrDuplicate = vn_glass_gpu_render_pixels(gpuHandle, in frame, (IntPtr)ptr, pixels.Length, stride);
                Assert.Equal(1, hrDuplicate);
                Assert.Equal(0UL, vn_glass_gpu_take_capture_count(gpuHandle));

                // Mutating a pixel must immediately re-upload and render (S_OK == 0).
                ptr[0] ^= 0xFF;
                int hrChanged = vn_glass_gpu_render_pixels(gpuHandle, in frame, (IntPtr)ptr, pixels.Length, stride);
                Assert.Equal(0, hrChanged);
                Assert.Equal(1UL, vn_glass_gpu_take_capture_count(gpuHandle));
            }
        }
        finally
        {
            if (gpuHandle != 0) vn_glass_gpu_destroy(gpuHandle);
            device9?.Dispose();
            d3d9?.Dispose();
            window.Close();
        }
    });

    [Fact]
    public void Benchmark_BrowserYouTubePlayback_HotPaths() => SharedStaTestRunner.RunAsync(async () =>
    {
        // 1. YOLOX 16:9 YouTube SmartCrop CPU Session + Inference + Working Set Delta
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long wsBeforeCrop = proc.WorkingSet64;
        long privBeforeCrop = proc.PrivateMemorySize64;

        using (var artworkSvc = new MediaArtworkService { EnableSmartCrop = true })
        {
            var coldBmp = CreateDetailedWidescreenBitmap(320, 180);
            var swCold = Stopwatch.StartNew();
            var coldCrop = artworkSvc.CropToSquare(coldBmp, "YouTube");
            swCold.Stop();
            Assert.NotNull(coldCrop);

            // Warm uncached inference (distinct pixel pattern)
            byte[] warmPixels = new byte[320 * 180 * 4];
            for (int i = 0; i < warmPixels.Length; i++) warmPixels[i] = (byte)((i * 19 + 7) & 0xFF);
            var warmBmp = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, warmPixels, 320 * 4);
            warmBmp.Freeze();

            var swWarm = Stopwatch.StartNew();
            _ = artworkSvc.CropToSquare(warmBmp, "YouTube");
            swWarm.Stop();

            proc.Refresh();
            double wsDeltaMb = (proc.WorkingSet64 - wsBeforeCrop) / (1024.0 * 1024.0);
            double privDeltaMb = (proc.PrivateMemorySize64 - privBeforeCrop) / (1024.0 * 1024.0);
            bool directMlLoaded = GetModuleHandleW("DirectML.dll") != IntPtr.Zero;
            output.WriteLine($"[BENCH] SmartCrop CPU (320x180 YouTube): cold={swCold.Elapsed.TotalMilliseconds:F2} ms, warm={swWarm.Elapsed.TotalMilliseconds:F2} ms, wsDelta={wsDeltaMb:+0.0;-0.0;0.0} MB, privDelta={privDeltaMb:+0.0;-0.0;0.0} MB, DirectML={directMlLoaded}");
        }

        // 2. MediaDetectionService 600 Steady-State Browser YouTube Ticks (10 min @ 1 Hz or 10 s @ 60 Hz)
        string tempDir = Path.Combine(Path.GetTempPath(), "vnotch-yt-bench-" + Guid.NewGuid().ToString("N"));
        var scanner = new CountingScanner { AnyUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" };
        using var mediaSvc = new MediaDetectionService(new NoopMetadataLookup(), new PassThroughArtwork(), scanner, null, new MediaSourceCache(Path.Combine(tempDir, "sources.json")));
        try
        {
            var stateField = typeof(MediaDetectionService).GetField("_state", Private)!;
            stateField.SetValue(mediaSvc, Enum.Parse(stateField.FieldType, "Running"));
            var thumb = CreateDetailedWidescreenBitmap(320, 180);
            mediaSvc.CachedThumbnail = thumb;
            Set(mediaSvc, "_thumbnailFetchGeneration", 3);
            Set(mediaSvc, "_sessionArtworkGeneration", 3);
            Set(mediaSvc, "_lastSessionArtworkFingerprint", ArtworkFingerprint.Create(thumb));
            Set(mediaSvc, "_lastMetadataChangeTime", DateTime.UtcNow - TimeSpan.FromSeconds(10));
            Set(mediaSvc, "_lastPublishedSessionInstanceKey", "chrome-bench");

            var tickInfo = new MediaInfo
            {
                CurrentTrack = "Benchmark Video",
                CurrentArtist = "Benchmark Channel",
                MediaSource = "Browser",
                SourceAppId = "chrome.exe",
                SessionInstanceKey = "chrome-bench",
                Thumbnail = thumb
            };

            // Warmup
            for (int i = 0; i < 10; i++)
            {
                await (Task)Invoke(mediaSvc, "ApplySessionThumbnailWithRefreshAsync", tickInfo, null, null, false, false)!;
                Invoke(mediaSvc, "StartThumbnailFetchIfNeeded", tickInfo, false);
            }

            const int ticks = 600;
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var swTicks = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++)
            {
                tickInfo.Thumbnail = null;
                await (Task)Invoke(mediaSvc, "ApplySessionThumbnailWithRefreshAsync", tickInfo, null, null, false, false)!;
                Invoke(mediaSvc, "StartThumbnailFetchIfNeeded", tickInfo, false);
            }
            swTicks.Stop();
            long allocPerTick = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / ticks;
            double usPerTick = swTicks.Elapsed.TotalMilliseconds * 1000.0 / ticks;
            output.WriteLine($"[BENCH] MediaDetection 600 steady-state browser ticks: total={swTicks.Elapsed.TotalMilliseconds:F2} ms ({usPerTick:F2} us/tick), alloc={allocPerTick} B/tick, browserUrlScans={scanner.AnyUrlCalls}");
            Assert.Equal(0, scanner.AnyUrlCalls);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }

        // 3. GpuGlass::RenderPixels Active GPU Render vs Static Backdrop Skip (600 frames @ 220x38 compact notch)
        var window = new Window { Width = 64, Height = 64, ShowInTaskbar = false, ShowActivated = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        IDirect3D9Ex? d3d9 = null;
        IDirect3DDevice9Ex? device9 = null;
        long gpuHandle = 0;
        try
        {
            d3d9 = D3D9.Direct3DCreate9Ex();
            var pp = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                BackBufferFormat = Format.Unknown,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferCount = 1,
                DeviceWindowHandle = hwnd,
                PresentationInterval = PresentInterval.Immediate
            };
            device9 = d3d9.CreateDeviceEx(0, DeviceType.Hardware, hwnd,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, pp);
            if (vn_glass_gpu_create(device9.NativePointer, 512, 128, out gpuHandle) == 0 && gpuHandle != 0)
            {
                using var destSurface = device9.CreateRenderTarget(512, 128, Format.A8R8G8B8, MultisampleType.None, 0, lockable: false);
                const int w = 220, h = 38, stride = w * 4, frames = 600;
                byte[] pixels = new byte[stride * h];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i * 29 + 11) & 0xFF);
                var frame = new D3DImageFramePresenter.GpuFrame { X = 0, Y = 0, Width = w, Height = h, Blur = 18f, Refract = 0 };

                unsafe
                {
                    fixed (byte* ptr = pixels)
                    {
                        // Measure 100 forced GPU uploads + blur pyramid passes (mutating 1 byte each frame)
                        var swActive = Stopwatch.StartNew();
                        for (int i = 0; i < 100; i++)
                        {
                            ptr[0] = (byte)i;
                            Assert.Equal(0, vn_glass_gpu_render_pixels(gpuHandle, in frame, (IntPtr)ptr, pixels.Length, stride));
                            _ = vn_glass_gpu_wait_ready(gpuHandle, 8);
                            System.Threading.SpinWait.SpinUntil(() => vn_glass_gpu_present_ready(gpuHandle, destSurface.NativePointer) == 0, 50);
                        }
                        swActive.Stop();
                        _ = vn_glass_gpu_take_capture_count(gpuHandle);

                        // Measure 600 static-backdrop frames (unchanged pixels behind notch while video plays below)
                        var swSkipped = Stopwatch.StartNew();
                        int skippedCount = 0;
                        for (int i = 0; i < frames; i++)
                        {
                            if (vn_glass_gpu_render_pixels(gpuHandle, in frame, (IntPtr)ptr, pixels.Length, stride) == 1)
                                skippedCount++;
                        }
                        swSkipped.Stop();
                        ulong gpuCaptures = vn_glass_gpu_take_capture_count(gpuHandle);

                        double activeUs = swActive.Elapsed.TotalMilliseconds * 1000.0 / 100.0;
                        double skippedUs = swSkipped.Elapsed.TotalMilliseconds * 1000.0 / frames;
                        output.WriteLine($"[BENCH] GpuGlass RenderPixels (220x38, blur=18): activeRender={activeUs:F1} us/frame, staticSkip={skippedUs:F2} us/frame ({activeUs / Math.Max(0.01, skippedUs):F1}x faster), skipped={skippedCount}/{frames}, gpuUploads={gpuCaptures}");
                        Assert.Equal(frames, skippedCount);
                        Assert.Equal(0UL, gpuCaptures);
                    }
                }
            }
        }
        finally
        {
            if (gpuHandle != 0) vn_glass_gpu_destroy(gpuHandle);
            device9?.Dispose();
            d3d9?.Dispose();
            window.Close();
        }
    });

    private static BitmapSource CreateDetailedWidescreenBitmap(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 4;
                pixels[idx] = (byte)((x * 5 + y * 3) & 0xFF);
                pixels[idx + 1] = (byte)((x * 7 + y * 11) & 0xFF);
                pixels[idx + 2] = (byte)((x * 13 + y * 17) & 0xFF);
                pixels[idx + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static object? Invoke(MediaDetectionService service, string method, params object?[] args) =>
        typeof(MediaDetectionService).GetMethod(method, Private)!.Invoke(service, args);

    private static object? InvokeWindow(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static void Set(object target, string name, object? value) =>
        target.GetType().GetField(name, Private)!.SetValue(target, value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("VNotch.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int vn_glass_gpu_create(IntPtr device, int width, int height, out long handle);

    [DllImport("VNotch.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int vn_glass_gpu_render_pixels(long handle, in D3DImageFramePresenter.GpuFrame frame, IntPtr pixels, int length, int stride);

    [DllImport("VNotch.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int vn_glass_gpu_wait_ready(long handle, uint timeoutMs);

    [DllImport("VNotch.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int vn_glass_gpu_present_ready(long handle, IntPtr destination);

    [DllImport("VNotch.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern ulong vn_glass_gpu_take_capture_count(long handle);

    [DllImport("VNotch.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void vn_glass_gpu_destroy(long handle);

    private sealed class CountingEmptyResponseHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>no captions</body></html>")
            });
        }
    }

    private sealed class CountingScanner : IWindowTitleScanner
    {
        public string? AnyUrl { get; set; }
        public int AnyUrlCalls { get; private set; }
        public List<string> GetAllWindowTitles(bool isThrottled) => new();
        public string? TryGetBrowserUrl() => AnyUrl;
        public string? TryGetMediaUrlFromAnyBrowser()
        {
            AnyUrlCalls++;
            return AnyUrl;
        }
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

    private sealed class PassThroughArtwork : IMediaArtworkService
    {
        public Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default) => Task.FromResult<BitmapImage?>(null);
        public BitmapSource? CropToSquare(BitmapSource source, string mediaSource, bool forceCenterCrop = false) => source;
        public Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default) => Task.FromResult<BitmapImage?>(null);
        public void ConfigureSmartCrop(bool enabled) { }
        public SubjectBounds? GetDominantSubjectBounds(BitmapSource source) => null;
    }

    private sealed class NoopMetadataLookup : IMediaMetadataLookupService
    {
        public Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default) => Task.FromResult<YouTubeLookupResult?>(null);
        public Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default) => Task.FromResult<YouTubeLookupResult?>(null);
        public Task<YouTubeLookupResult?> TrySearchYouTubeByTitleAsync(string title, string artist = "", CancellationToken ct = default) => Task.FromResult<YouTubeLookupResult?>(null);
        public Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> TryGetSpotifyArtworkUrlAsync(string title, string primaryArtist, TimeSpan duration = default, string? albumTitle = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
