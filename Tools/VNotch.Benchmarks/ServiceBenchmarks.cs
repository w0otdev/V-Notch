using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BenchmarkDotNet.Attributes;
using VNotch.Services;

namespace VNotch.Benchmarks;

[MemoryDiagnoser, DisassemblyDiagnoser(maxDepth: 3)]
public class BlurBenchmarks
{
    [Params(128, 256)] public int Size { get; set; }
    [Params(8, 20)] public int Radius { get; set; }
    private byte[] source = null!, pixels = null!, target = null!;

    [GlobalSetup]
    public void Setup()
    {
        source = new byte[Size * Size * 4];
        new Random(42).NextBytes(source);
        pixels = new byte[source.Length];
        target = new byte[source.Length];
    }

    [Benchmark(Baseline = true)]
    public byte Baseline()
    {
        source.CopyTo(pixels, 0);
        for (int i = 0; i < 2; i++)
        {
            BaselineFastBlurService.BoxBlurHorizontal(pixels, target, Size, Size, Radius);
            BaselineFastBlurService.BoxBlurVertical(target, pixels, Size, Size, Radius);
        }
        BaselineFastBlurService.DarkenPixels(pixels, pixels.Length, 0.96f);
        return pixels[0];
    }

    [Benchmark]
    public byte Optimized()
    {
        source.CopyTo(pixels, 0);
        for (int i = 0; i < 2; i++)
        {
            FastBlurService.BoxBlurHorizontal(pixels, target, Size, Size, Radius);
            FastBlurService.BoxBlurVertical(target, pixels, Size, Size, Radius);
        }
        FastBlurService.DarkenPixels(pixels, pixels.Length);
        return pixels[0];
    }
}

[MemoryDiagnoser]
public class WeatherJsonBenchmarks
{
    [Params(1024, 65536)] public int PayloadBytes { get; set; }
    private byte[] payload = null!;
    [GlobalSetup] public void Setup() => payload = Encoding.UTF8.GetBytes("{\"city\":\"Hà Nội\",\"padding\":\"" + new string('x', PayloadBytes) + "\"}");

    [Benchmark(Baseline = true)]
    public async Task<int> Baseline()
    {
        using var response = new HttpResponseMessage { Content = new ByteArrayContent(payload) };
        string? json = await BaselineWeatherService.ReadLimitedStringAsync(response, 512 * 1024, default);
        using var document = JsonDocument.Parse(json!);
        return (int)document.RootElement.GetProperty("padding").ValueKind;
    }

    [Benchmark]
    public async Task<int> Optimized()
    {
        using var response = new HttpResponseMessage { Content = new ByteArrayContent(payload) };
        using var document = await WeatherService.ReadLimitedJsonAsync(response, 512 * 1024, default);
        return (int)document!.RootElement.GetProperty("padding").ValueKind;
    }
}

[MemoryDiagnoser, DisassemblyDiagnoser(maxDepth: 2)]
public class CityBenchmarks
{
    private const string City = "  Hà\t Nội\r\n  ";
    [Benchmark(Baseline = true)] public string Baseline() => new string(City.Where(c => !char.IsControl(c)).Take(100).ToArray()).Trim();
    [Benchmark] public string? Optimized() => WeatherService.CleanCity(City);
}

[MemoryDiagnoser]
public class UrlBenchmarks
{
    private const string Url = "https://example.com/weather?q=Hanoi";
    private readonly Uri uri = new(Url);
    // Measure validation only: never launch a browser during benchmarking.
    [Benchmark(Baseline = true)] public bool StringBaseline() => BaselineSafeLauncher.IsSafeUrl(Url, out var parsed) && BaselineSafeLauncher.IsSafeUrl(parsed!.OriginalString, out _);
    [Benchmark] public bool StringOptimized() => SafeLauncher.IsSafeUrl(Url, out _);
    [Benchmark] public bool UriBaseline() => BaselineSafeLauncher.IsSafeUrl(uri.OriginalString, out _);
    [Benchmark] public bool UriOptimized() => SafeLauncher.IsSafeUri(uri);
}

[MemoryDiagnoser]
public class ColorBenchmarks
{
    [Params(64, 256)] public int Size { get; set; }
    private BitmapImage image = null!;
    private readonly BaselineColorExtractionService baseline = new();
    private readonly ColorExtractionService optimized = new();

    [GlobalSetup]
    public void Setup()
    {
        byte[] pixels = new byte[Size * Size * 4];
        new Random(42).NextBytes(pixels);
        var source = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        if (baseline.ExtractDominantColor(image) != optimized.ExtractDominantColor(image))
            throw new InvalidOperationException("Color output changed.");
    }

    [Benchmark(Baseline = true)] public Color Baseline() => baseline.ExtractDominantColor(image);
    [Benchmark] public Color Optimized() => optimized.ExtractDominantColor(image);
}

[MemoryDiagnoser, DisassemblyDiagnoser(maxDepth: 3)]
public class ColorKernelBenchmarks
{
    private readonly byte[] pixels = new byte[64 * 64 * 4];
    [GlobalSetup] public void Setup() => new Random(42).NextBytes(pixels);
    [Benchmark(Baseline = true)] public Color Baseline() => BaselineColorExtractionService.ExtractSampledColor(pixels, 64, 64);
    [Benchmark] public Color Optimized() => ColorExtractionService.ExtractSampledColor(pixels, 64, 64);
}
