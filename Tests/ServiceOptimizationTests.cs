using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ServiceOptimizationTests
{
    [Fact]
    public void ReciprocalMatchesDivisionForEverySupportedChannelSum()
    {
        for (int radius = 1; radius <= 32; radius++)
        {
            int window = radius * 2 + 1;
            uint reciprocal = FastBlurService.GetWindowReciprocal(window);
            for (int sum = 0; sum <= 255 * window; sum++)
                Assert.Equal(sum / window, (int)(((uint)sum * reciprocal) >> 24));
        }
    }

    [Fact]
    public void DarkeningPreservesEveryByteValueAndAlpha()
    {
        byte[] pixels = Enumerable.Range(0, 1024).Select(i => (byte)(i / 4)).ToArray();
        byte[] expected = (byte[])pixels.Clone();
        BaselineFastBlurService.DarkenPixels(expected, expected.Length, 0.96f);
        FastBlurService.DarkenPixels(pixels, pixels.Length);
        Assert.Equal(expected, pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    public void DarkeningHandlesVectorTailsWithoutTouchingBufferRemainder(int pixelCount)
    {
        byte[] pixels = new byte[(pixelCount + 4) * 4];
        new Random(42).NextBytes(pixels);
        byte[] expected = (byte[])pixels.Clone();
        BaselineFastBlurService.DarkenPixels(expected, pixelCount * 4, 0.96f);
        FastBlurService.DarkenPixels(pixels, pixelCount * 4);
        Assert.Equal(expected, pixels);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(257)]
    public void BlurKernelsAllocateNothingAfterPoolWarmup(int width)
    {
        byte[] source = new byte[width * 31 * 4], target = new byte[source.Length];
        Array.Fill(source, (byte)255);
        for (int i = 0; i < 10; i++) FastBlurService.BoxBlurVertical(source, target, width, 31, 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            FastBlurService.BoxBlurHorizontal(source, target, width, 31, 20);
            FastBlurService.BoxBlurVertical(target, source, width, 31, 20);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.All(source, value => Assert.Equal(255, value));
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
                FastBlurService.BoxBlurHorizontal(actual, actualTemp, width, height, radius);
                Assert.Equal(expectedTemp, actualTemp);
                BaselineFastBlurService.BoxBlurVertical(expectedTemp, expected, width, height, radius);
                FastBlurService.BoxBlurVertical(actualTemp, actual, width, height, radius);
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
        string? expected = string.IsNullOrWhiteSpace(city) ? null : new string(city.Where(c => !char.IsControl(c)).Take(100).ToArray()).Trim();
        Assert.Equal(expected, WeatherService.CleanCity(city));
    }

    [Fact]
    public void CityLimitCountsSurvivingCharactersBeforeTrimming()
    {
        string city = "  " + string.Concat(Enumerable.Repeat("a\t", 150));
        Assert.Equal(new string('a', 98), WeatherService.CleanCity(city));
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
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(256, 128)]
    [InlineData(37, 83)]
    public void ColorExtractionMatchesOriginalForFrozenArtwork(int width, int height)
    {
        var baseline = new BaselineColorExtractionService();
        var optimized = new ColorExtractionService();
        for (int seed = 0; seed < 10; seed++)
        {
            byte[] pixels = new byte[width * height * 4];
            new Random(seed).NextBytes(pixels);
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Color expected = baseline.ExtractDominantColor(image);
            Assert.Equal(expected, optimized.ExtractDominantColor(image));
            Parallel.For(0, 4, _ => Assert.Equal(expected, optimized.ExtractDominantColor(image)));
        }
    }

    [Fact]
    public void ColorKernelHasNoSteadyStateManagedAllocationAndKeepsFallback()
    {
        byte[] pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < 100; i++) ColorExtractionService.ExtractSampledColor(pixels, 64, 64);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Color result = default;
        for (int i = 0; i < 100; i++) result = ColorExtractionService.ExtractSampledColor(pixels, 64, 64);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Colors.White, result);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ColorBucketsPreserveSampleOrderAndFiltering()
    {
        // Two bins with varying populations exercise ties, first-seen order,
        // alpha rejection, and the inclusive brightness thresholds.
        for (int seed = 0; seed < 50; seed++)
        {
            var random = new Random(seed);
            byte[] pixels = new byte[64 * 64 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                bool first = random.Next(2) == 0;
                pixels[i] = first ? (byte)150 : (byte)60;
                pixels[i + 1] = 60;
                pixels[i + 2] = first ? (byte)60 : (byte)150;
                pixels[i + 3] = random.Next(3) == 0 ? (byte)99 : (byte)100;
            }
            Assert.Equal(BaselineColorExtractionService.ExtractSampledColor(pixels, 64, 64),
                ColorExtractionService.ExtractSampledColor(pixels, 64, 64));
        }
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
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], token);
    }

    private sealed class SlowStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = token.Register(() => tcs.TrySetCanceled(token));
            return await tcs.Task;
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage { Content = new StreamContent(new SlowStream()) });
    }
}
