using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed partial class NativePipelineOptimizationTests(ITestOutputHelper output)
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_preprocess_bgra32(ReadOnlySpan<byte> pixels, int length,
        int width, int height, int stride, Span<float> tensor, int capacity, out float scale);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_smartcrop_session_create(string model, int modelLength, string directory,
        int directoryLength, byte preferDml, out long handle, out byte dml);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_session_destroy(long handle);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_session_infer(long handle, ReadOnlySpan<byte> pixels, int length,
        int width, int height, int stride, Span<byte> detections, int capacity, out int count, out byte dml);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_convert_oklab(ReadOnlySpan<byte> pixels, int length, Span<float> lab, int capacity, byte avx);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_extract_advanced_palette(ReadOnlySpan<byte> pixels, int width, int height, int stride, Span<byte> result);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_visualizer_stream_create(int sampleRate, out long handle);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_visualizer_stream_push(long handle, ReadOnlySpan<byte> data, int length,
        int channels, int bits, byte isFloat, Span<float> levels, out float beat, out float rms);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_visualizer_stream_close(long handle);

    [Theory]
    [InlineData(13, 416)] // AVX bulk and a five-pixel scalar tail.
    [InlineData(607, 313)] // Non-integral downscale and bilinear sampling.
    [InlineData(17, 31)] // Upscale.
    public void NativeResizeMatchesBilinearBgrReferenceIncludingPadding(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        new Random(17).NextBytes(pixels);
        float[] tensor = new float[3 * 416 * 416];
        Assert.Equal(0, vn_smartcrop_preprocess_bgra32(pixels, pixels.Length, width, height, width * 4,
            tensor, tensor.Length, out float scale));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        for (int y = 0; y < 416; ++y)
            for (int x = 0; x < 416; ++x)
                for (int c = 0; c < 3; ++c)
                {
                    float expected = 114;
                    if (x < w && y < h)
                    {
                        double sx = Math.Clamp((x + .5) * width / w - .5, 0, width - 1);
                        double sy = Math.Clamp((y + .5) * height / h - .5, 0, height - 1);
                        int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
                        double wx = sx - x0, wy = sy - y0;
                        double a = pixels[(y0 * width + x0) * 4 + c] * (1 - wx) + pixels[(y0 * width + x1) * 4 + c] * wx;
                        double b = pixels[(y1 * width + x0) * 4 + c] * (1 - wx) + pixels[(y1 * width + x1) * 4 + c] * wx;
                        expected = (float)(a * (1 - wy) + b * wy);
                    }
                    Assert.InRange(Math.Abs(tensor[c * 416 * 416 + y * 416 + x] - expected), 0, .015f);
                }
    }

    [Fact]
    public void TruncatedPixelBufferIsRejectedBeforeWritingTheTensor()
    {
        float[] tensor = Enumerable.Repeat(-11f, 3 * 416 * 416).ToArray();
        Assert.Equal(-1, vn_smartcrop_preprocess_bgra32(new byte[7], 7, 2, 1, 8, tensor, tensor.Length, out _));
        Assert.All(tensor, value => Assert.Equal(-11, value));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void NativeYoloxRunsWithCpuOrPreferredDirectMlWithoutManagedTensorAllocations(byte preferDml)
    {
        string directory = AppContext.BaseDirectory, model = Path.Combine(directory, "Models", "yolox_nano.onnx");
        Assert.Equal(0, vn_smartcrop_session_create(model, model.Length, directory, directory.Length, preferDml, out long handle, out byte backend));
        try
        {
            if (preferDml == 0) Assert.Equal(0, backend);
            byte[] pixels = new byte[64 * 64 * 4];
            byte[] detections = new byte[3549 * 24];
            Assert.Equal(0, vn_smartcrop_session_infer(handle, pixels, pixels.Length, 64, 64, 256,
                detections, 3549, out _, out _));
            var stopwatch = Stopwatch.StartNew();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int status = 0;
            for (int i = 0; i < 5; ++i)
                status |= vn_smartcrop_session_infer(handle, pixels, pixels.Length, 64, 64, 256,
                    detections, 3549, out _, out backend);
            long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
            stopwatch.Stop();
            Assert.Equal(0, status);
            Assert.Equal(0, delta);
            output.WriteLine($"YOLOX backend={(backend != 0 ? "DirectML" : "CPU")}, mean={stopwatch.Elapsed.TotalMilliseconds / 5:F2} ms, managed tensor allocations={delta} bytes");
        }
        finally { Assert.Equal(0, vn_smartcrop_session_destroy(handle)); }
    }

    [Fact]
    public void VectorOklabMatchesScalarReferenceForBlackWhitePrimariesAndRandomPixels()
    {
        byte[] pixels = new byte[1003 * 4];
        new Random(71).NextBytes(pixels);
        new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255 }.CopyTo(pixels, 0);
        float[] scalar = new float[1003 * 3], vector = new float[1003 * 3];
        Assert.Equal(0, vn_dicolor_convert_oklab(pixels, pixels.Length, scalar, scalar.Length, 0));
        Assert.Equal(0, vn_dicolor_convert_oklab(pixels, pixels.Length, vector, vector.Length, 1));
        Assert.InRange(vector[3], .9999f, 1.0001f);
        Assert.InRange(Math.Abs(vector[4]), 0, .00001f);
        Assert.InRange(Math.Abs(vector[5]), 0, .00001f);
        for (int i = 0; i < vector.Length; ++i) Assert.InRange(Math.Abs(vector[i] - scalar[i]), 0, .00001f);
    }

    [Theory]
    [InlineData(140, 140, 20)]
    [InlineData(220, 30, 80)]
    [InlineData(20, 20, 250)]
    [InlineData(20, 200, 80)]
    public void PaletteSelectsReadableTextOnTheChosenBackground(byte red, byte green, byte blue)
    {
        byte[] pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255; }
        Span<byte> palette = stackalloc byte[18];
        Assert.Equal(0, vn_dicolor_extract_advanced_palette(pixels, 16, 16, 64, palette));
        static double Linear(byte channel)
        { double v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        double background = .2126 * Linear(palette[0]) + .7152 * Linear(palette[1]) + .0722 * Linear(palette[2]);
        double text = .2126 * Linear(palette[14]) + .7152 * Linear(palette[15]) + .0722 * Linear(palette[16]);
        Assert.True((Math.Max(text, background) + .05) / (Math.Min(text, background) + .05) >= 4.5);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(36)]
    public void PaletteTailsIgnoreRowPaddingAndLowAlphaColors(int width)
    {
        const int height = 11;
        int stride = width * 4 + 12;
        byte[] pixels = new byte[(height - 1) * stride + width * 4];
        new Random(42).NextBytes(pixels);
        byte[] referencePixels = new byte[8 * 8 * 4];
        for (int i = 0; i < referencePixels.Length; i += 4)
        { referencePixels[i] = 20; referencePixels[i + 1] = 80; referencePixels[i + 2] = 220; referencePixels[i + 3] = 255; }
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                int i = y * stride + x * 4;
                if (x % 3 == 0) { pixels[i] = 255; pixels[i + 1] = 0; pixels[i + 2] = 0; pixels[i + 3] = 79; }
                else { pixels[i] = 20; pixels[i + 1] = 80; pixels[i + 2] = 220; pixels[i + 3] = 255; }
            }
        byte[] expected = new byte[18], actual = new byte[18];
        Assert.Equal(0, vn_dicolor_extract_advanced_palette(referencePixels, 8, 8, 32, expected));
        Assert.Equal(0, vn_dicolor_extract_advanced_palette(pixels, width, height, stride, actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RegistryWriteWakesTheWorkerAndTheNotificationRearms()
    {
        string path = @"Software\VNotch.Tests\NativeWatch-" + Guid.NewGuid().ToString("N");
        using var key = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            using var signal = new SemaphoreSlim(0);
            using var monitor = new RegistryChangeMonitor(Registry.CurrentUser, path, onChanged: () => signal.Release());
            Assert.True(monitor.ConsumeChange());
            for (int i = 1; i <= 2; ++i)
            {
                using var child = key.CreateSubKey("microphone");
                child.SetValue("LastUsedTimeStop", i);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // Kernel and managed waits may coalesce or deliver an already-consumed signal.
                do { await signal.WaitAsync(deadline.Token); } while (!monitor.ConsumeChange());
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void NativeAudioAnalysisIsIndependentOfPacketBoundaries()
    {
        var samples = new float[16384];
        for (int i = 0; i < samples.Length; ++i) samples[i] = .4f * MathF.Sin(2 * MathF.PI * 1000 * i / 44100);
        byte[] pcm = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        Assert.Equal(0, vn_visualizer_stream_create(44100, out long whole));
        Assert.Equal(0, vn_visualizer_stream_create(44100, out long chunked));
        try
        {
            float[] a = new float[5], b = new float[5];
            Assert.Equal(0, vn_visualizer_stream_push(whole, pcm, pcm.Length, 1, 32, 1, a, out float beatA, out float rmsA));
            float beatB = 0, rmsB = 0;
            for (int offset = 0; offset < pcm.Length; offset += 788)
            {
                var chunk = pcm.AsSpan(offset, Math.Min(788, pcm.Length - offset));
                Assert.Equal(0, vn_visualizer_stream_push(chunked, chunk, chunk.Length, 1, 32, 1, b, out beatB, out rmsB));
            }
            Assert.Equal(a, b); Assert.Equal(beatA, beatB); Assert.Equal(rmsA, rmsB);
            Assert.Contains(a, value => value > .05f);
            Assert.All(a, value => Assert.InRange(value, 0, 1));
            Assert.Equal(0, vn_visualizer_stream_push(whole, new byte[pcm.Length * 4], pcm.Length * 4, 1, 32, 1, a, out _, out float silentRms));
            Assert.Equal(0, silentRms);
            Assert.All(a, value => Assert.InRange(value, 0, .005f)); // Beat envelopes release gradually.
        }
        finally { vn_visualizer_stream_close(whole); vn_visualizer_stream_close(chunked); }
    }
}
