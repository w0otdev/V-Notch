using System.IO;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed partial class ServiceOptimizationTests
{
    [Fact]
    public void ReciprocalMatchesDivisionForEverySupportedChannelSum()
    {
        for (int radius = 1; radius <= 32; radius++)
        {
            int window = radius * 2 + 1;
            uint reciprocal = GetWindowReciprocal(window);
            for (int sum = 0; sum <= 255 * window; sum++)
                Assert.Equal(sum / window, (int)(((uint)sum * reciprocal) >> 24));
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.78f)]
    [InlineData(0.96f)]
    [InlineData(1f)]
    public void DarkeningPreservesEveryByteValueAndAlpha(float factor)
    {
        byte[] pixels = Enumerable.Range(0, 1024).Select(i => (byte)(i / 4)).ToArray();
        byte[] expected = (byte[])pixels.Clone();
        BaselineFastBlurService.DarkenPixels(expected, expected.Length, factor);
        DarkenPixels(pixels, pixels.Length, factor);
        Assert.Equal(expected, pixels);
    }

    [Theory]
    [InlineData(1, 0.96f)]
    [InlineData(3, 0.96f)]
    [InlineData(4, 0.96f)]
    [InlineData(5, 0.96f)]
    [InlineData(17, 0.96f)]
    [InlineData(1, 0.78f)]
    [InlineData(3, 0.78f)]
    [InlineData(4, 0.78f)]
    [InlineData(5, 0.78f)]
    [InlineData(17, 0.78f)]
    public void DarkeningHandlesVectorTailsWithoutTouchingBufferRemainder(int pixelCount, float factor)
    {
        byte[] pixels = new byte[(pixelCount + 4) * 4];
        new Random(42).NextBytes(pixels);
        byte[] expected = (byte[])pixels.Clone();
        BaselineFastBlurService.DarkenPixels(expected, pixelCount * 4, factor);
        DarkenPixels(pixels, pixelCount * 4, factor);
        Assert.Equal(expected, pixels);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(257)]
    public void BlurKernelsAllocateNothingAfterPoolWarmup(int width)
    {
        byte[] input = new byte[width * 31 * 4], scratch = new byte[input.Length];
        Array.Fill(input, (byte)255);
        for (int i = 0; i < 10; i++) { BoxBlurHorizontal(input, scratch, width, 31, 20); BoxBlurVertical(scratch, input, width, 31, 20); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            BoxBlurHorizontal(input, scratch, width, 31, 20);
            BoxBlurVertical(scratch, input, width, 31, 20);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.All(input, value => Assert.Equal(255, value));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(3, 5)]
    [InlineData(63, 65)]
    [InlineData(128, 128)]
    [InlineData(257, 31)]
    public void BothBlurPassesMatchReferenceIncludingEdges(int width, int height)
    {
        byte[] source = new byte[width * height * 4];
        new Random(17).NextBytes(source);
        for (int radius = 1; radius <= 32; radius++)
        {
            byte[] expected = (byte[])source.Clone(), actual = (byte[])source.Clone();
            byte[] expectedTemp = new byte[source.Length], actualTemp = new byte[source.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                BaselineFastBlurService.BoxBlurHorizontal(expected, expectedTemp, width, height, radius);
                BoxBlurHorizontal(actual, actualTemp, width, height, radius);
                Assert.Equal(expectedTemp, actualTemp);
                BaselineFastBlurService.BoxBlurVertical(expectedTemp, expected, width, height, radius);
                BoxBlurVertical(actualTemp, actual, width, height, radius);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("  Hà\t Nội\r\n  ")]
    [InlineData("\0\u0001\u0085")]
    [InlineData("\u2003Đà Nẵng\u2003")]
    public void CityCleaningMatchesOriginal(string? city)
    {
        string? expected = string.IsNullOrWhiteSpace(city) ? null : new string(city.Where(c => !char.IsControl(c)).ToArray()).Trim();
        Assert.Equal(expected, WeatherService.CleanCity(city));
    }

    [Fact]
    public void CityCleaningDoesNotSilentlyTruncateLongNames()
    {
        string city = "  " + string.Concat(Enumerable.Repeat("a\t", 150));
        Assert.Equal(new string('a', 150), WeatherService.CleanCity(city));
    }

    [Theory]
    [InlineData("https://example.com/path", true)]
    [InlineData("HTTP://example.com", true)]
    [InlineData("mailto:test@example.com", true)]
    [InlineData("ms-settings:batterysaver", true)]
    [InlineData("MS-SETTINGS:BATTERYSAVER", true)]
    [InlineData("ms-settings:batterysaver?x=1", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData(" ms-settings:batterysaver", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("relative/path", false)]
    public void BothUrlInputsEnforceSameAllowlist(string value, bool allowed)
    {
        Assert.Equal(allowed, SafeLauncher.IsSafeUrl(value, out _));
        Assert.Equal(allowed, SafeLauncher.IsSafeUri(new Uri(value, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void NullAndRelativeUrisAreRejectedWithoutLaunching()
    {
        Assert.False(SafeLauncher.IsSafeUri(null));
        Assert.False(SafeLauncher.TryOpenUrl(new Uri("relative", UriKind.Relative)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JsonReaderHonorsExactByteLimitAndOwnsDocumentStorage(bool knownLength)
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"city\":\"Hà Nội\"}");
        using var response = Response(payload, knownLength);
        using var doc = await WeatherService.ReadLimitedJsonAsync(response, payload.Length, default);
        Assert.Equal("Hà Nội", doc!.RootElement.GetProperty("city").GetString());
        using var oversized = Response(payload, knownLength);
        Assert.Null(await WeatherService.ReadLimitedJsonAsync(oversized, payload.Length - 1, default));
        // Exercise another read before revisiting doc to detect returned pooled storage.
        using var otherResponse = Response(Encoding.UTF8.GetBytes("{\"city\":\"London\"}"), knownLength);
        using var other = await WeatherService.ReadLimitedJsonAsync(otherResponse, 100, default);
        Assert.Equal("Hà Nội", doc.RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public async Task JsonReaderHandlesEmptyMalformedAndCancelledBodies()
    {
        using var empty = Response([], false);
        Assert.Null(await WeatherService.ReadLimitedJsonAsync(empty, 100, default));
        using var malformed = Response(Encoding.UTF8.GetBytes("{invalid"), false);
        await Assert.ThrowsAnyAsync<JsonException>(() => WeatherService.ReadLimitedJsonAsync(malformed, 100, default));
        using var cancelled = Response(Encoding.UTF8.GetBytes("{}"), false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WeatherService.ReadLimitedJsonAsync(cancelled, 100, new CancellationToken(true)));
    }

    [Fact]
    public async Task StreamingResponseBodyStillHasRequestTimeout()
    {
        using var http = new HttpClient(new SlowHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        var service = new WeatherService(http);
        Assert.Null(await service.GetCurrentWeatherAsync("Hanoi").WaitAsync(TimeSpan.FromSeconds(60)));
    }

    private static HttpResponseMessage Response(byte[] bytes, bool knownLength) => new()
    {
        Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new ChunkedStream(bytes))
    };

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }

    private sealed class SlowStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage { Content = new StreamContent(new SlowStream()) });
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_fast_blur_get_window_reciprocal(int window, out uint outReciprocal);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_fast_blur_darken_pixels(byte* pixels, int length, float factor);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_fast_blur_box_horizontal(byte* source, byte* target, int w, int h, int radius);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_fast_blur_box_vertical(byte* source, byte* target, int w, int h, int radius);

    private static unsafe void DarkenPixels(byte[] pixels, int length, float factor = 0.96f)
    {
        if (pixels == null || length <= 0) return;
        fixed (byte* p = pixels)
        {
            _ = vn_fast_blur_darken_pixels(p, Math.Min(pixels.Length, length), factor);
        }
    }

    private static unsafe void BoxBlurHorizontal(byte[] source, byte[] target, int w, int h, int radius)
    {
        fixed (byte* pSrc = source)
        fixed (byte* pDst = target)
        {
            _ = vn_fast_blur_box_horizontal(pSrc, pDst, w, h, radius);
        }
    }

    private static uint GetWindowReciprocal(int window)
    {
        _ = vn_fast_blur_get_window_reciprocal(window, out uint rec);
        return rec;
    }

    private static unsafe void BoxBlurVertical(byte[] source, byte[] target, int w, int h, int radius)
    {
        fixed (byte* pSrc = source)
        fixed (byte* pDst = target)
        {
            _ = vn_fast_blur_box_vertical(pSrc, pDst, w, h, radius);
        }
    }
}
