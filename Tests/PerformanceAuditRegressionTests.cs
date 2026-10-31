using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PerformanceAuditRegressionTests
{
    [Fact]
    public async Task CachedSettingsLoadDoesNotReadOrDecryptTheFileAgainAndReturnsIndependentCopies()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        await using var service = new SettingsService(path, _ => { });
        await service.SaveAsync(new NotchSettings { Width = 351 });
        var first = service.Load();
        first.Width = 999;
        first.LiquidGlass.PowerFactor = 9;
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = service.Load();
            Assert.Equal(351, second.Width);
            Assert.NotEqual(9, second.LiquidGlass.PowerFactor);
        }
        await service.SaveAsync(new NotchSettings { Width = 452 });
        Assert.Equal(452, service.Load().Width);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new NotchSettings { Width = 567 }));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(567, service.Load().Width);
    }

    [Fact]
    public async Task LoadingSettingsPrunesAnExistingOversizedBackupHistory()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion }));
        for (int i = 0; i < 24; i++) await File.WriteAllTextAsync(Path.Combine(folder.Path,
            $"settings.bak-{new DateTime(2026, 1, 1).AddMilliseconds(i):yyyyMMdd-HHmmss-fff}.json"), "{}");
        await using var service = new SettingsService(path, _ => { });
        _ = service.Load();
        Assert.InRange(Directory.GetFiles(folder.Path, "settings.bak-*.json").Length, 1, 10);
    }

    [Theory]
    [InlineData(true, false, true, 250, 100)]
    [InlineData(true, false, true, 249, 8)]
    [InlineData(true, true, true, 1000, 8)]
    [InlineData(false, false, true, 1000, 8)]
    [InlineData(true, false, false, 1000, 8)]
    public void OnlyASettledCollapsedBackdropUsesIdleCadence(bool idle, bool animating, bool hasFrame, long age, double expected) =>
        Assert.Equal(expected, LiquidGlassController.ChooseFrameIntervalMs(8, idle, animating, hasFrame, age));

    [Fact]
    public void InputWakeDoesNotInheritTheOldIdleDeadline() =>
        Assert.Equal(28, LiquidGlassController.ComputeNextFrameDeadline(100, 8, 20));

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void CroppedGlassPresentationPreservesPixelDensityAndCaptureBounds(double dpi) => SharedStaTestRunner.Run(() =>
    {
        byte[] pixels = new byte[16 * 16 * 4];
        new Random(42).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96,
            PixelFormats.Bgra32, null, pixels, 16 * 4);
        source.Freeze();
        var presentation = new GlassCaptureImage(source, 16, 16);
        foreach (var (width, height) in new[] { (7, 9), (12, 6), (16, 16) })
        {
            presentation.SetCaptureSize(width, height);
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(source, new Int32Rect(0, 0, width, height));
            Assert.Equal(Raster(crop, width, height), Raster(presentation.Image, width, height));
        }

        byte[] Raster(ImageSource image, int width, int height)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawImage(image, new Rect(0, 0, width * 96 / dpi, height * 96 / dpi));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var result = new byte[width * height * 4];
            bitmap.CopyPixels(result, width * 4, 0);
            return result;
        }
    });

    [Fact]
    public void FrameDigestDetectsEveryByteWithoutRetainingPixels()
    {
        const int w = 13, h = 9;
        byte[] bytes = new byte[w * h * 4];
        new Random(42).NextBytes(bytes);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var history = new GlassFrameHistory();
            history.Commit(pointer, w, h);
            Assert.True(history.IsUnchanged(pointer, w, h));
            for (int i = 0; i < bytes.Length; i++)
            {
                Marshal.WriteByte(pointer, i, (byte)(Marshal.ReadByte(pointer, i) ^ 1));
                Assert.False(history.IsUnchanged(pointer, w, h));
                history.CommitCheckedFrame();
            }
            Assert.False(history.IsUnchanged(pointer, h, w));
            Assert.Equal(0, history.RetainedPixelBytes);
            history.Clear();
            Assert.False(history.IsUnchanged(pointer, w, h));
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Fact]
    public async Task UpdateCacheSurvivesRestartAndUsesItsEtagAfterExpiry()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "update-cache.json");
        int requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            requests++;
            if (requests > 1)
            {
                Assert.Equal("\"release-one\"", request.Headers.IfNoneMatch.Single().Tag);
                return new(HttpStatusCode.NotModified);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><id>tag:github.com,2008:Repository/123/v1.0.0</id><updated>2026-10-04T00:00:00Z</updated><content>notes</content></entry></feed>")
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"release-one\"");
            return response;
        }));
        using (var first = new UpdateService(client, new UpdateSecurityPolicy(), cachePath: path))
            Assert.Equal("1.0.0", (await first.CheckForUpdatesAsync())?.Version);
        using (var restarted = new UpdateService(client, new UpdateSecurityPolicy(), cachePath: path))
            Assert.Equal("1.0.0", (await restarted.CheckForUpdatesAsync())?.Version);
        Assert.Equal(1, requests);
        var cache = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        cache["LastCheckUtc"] = DateTime.UtcNow.AddHours(-2);
        await File.WriteAllTextAsync(path, cache.ToJsonString());
        using var expired = new UpdateService(client, new UpdateSecurityPolicy(), cachePath: path);
        Assert.Equal("1.0.0", (await expired.CheckForUpdatesAsync())?.Version);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task RateLimitBackoffSurvivesRestartAndPrereleaseToggle()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "update-cache.json");
        int apiRequests = 0, feedRequests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") apiRequests++;
            else
            {
                Assert.Equal("/w0otdev/V-Notch/releases.atom", request.RequestUri.AbsolutePath);
                feedRequests++;
            }
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString());
            return response;
        }));
        using (var first = new UpdateService(client, new UpdateSecurityPolicy(), cachePath: path))
            Assert.Null(await first.CheckForUpdatesAsync());
        using var restarted = new UpdateService(client, new UpdateSecurityPolicy(), cachePath: path);
        Assert.Null(await restarted.CheckForUpdatesAsync());
        Assert.Null(await restarted.CheckForUpdatesAsync(includePrereleases: true));
        Assert.Equal(0, apiRequests);
        Assert.Equal(1, feedRequests);
    }

    [Fact]
    public void DeploymentFontsResolveToTheBundledFaces() => SharedStaTestRunner.Run(() =>
    {
        Assert.Equal(4, Directory.GetFiles(AppFonts.BaseUri.LocalPath, "SF-Pro-Display-*.otf").Length);
        foreach (var weight in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold })
        {
            var typeface = new Typeface(AppFonts.Get(), FontStyles.Normal, weight, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyph));
            Assert.StartsWith(AppFonts.BaseUri.AbsoluteUri, glyph.FontUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        }
    });

    [Fact]
    public void CroppedArtworkReusesSubjectDetectionsWithoutLoadingAnInferenceSession() => SharedStaTestRunner.Run(() =>
    {
        using var service = new SmartThumbnailCropService();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var original = System.Windows.Media.Imaging.BitmapSource.Create(256, 128, 96, 96,
            PixelFormats.Bgra32, null, new byte[256 * 128 * 4], 256 * 4);
        original.Freeze();
        var crop = new System.Windows.Media.Imaging.CroppedBitmap(original, new Int32Rect(64, 0, 128, 128));
        crop.Freeze();
        typeof(SmartThumbnailCropService).GetField("_modelExists", flags)!.SetValue(service, true);
        typeof(SmartThumbnailCropService).GetMethod("AddInferenceCacheEntryLocked", flags)!.Invoke(service,
            [ArtworkFingerprint.Create(original), new[] { new SmartThumbnailCropService.Detection
                { X1 = 80, X2 = 144, Y1 = 20, Y2 = 100, Confidence = .9f, ClassId = 39 } }]);
        var subject = service.GetDominantSubjectBounds(crop);
        Assert.True(subject.HasValue);
        Assert.Equal(.375f, subject.GetValueOrDefault().CenterX);
        Assert.Equal(.5f, subject.GetValueOrDefault().Width);
        Assert.Null(typeof(SmartThumbnailCropService).GetField("_cachedSession", flags)!.GetValue(service));
        Assert.Equal(subject, service.GetDominantSubjectBounds(crop));
    });

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-perf-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
