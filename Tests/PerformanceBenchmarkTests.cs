using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class PerformanceBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32EnumProcesses", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcesses([Out] uint[] lpidProcess, uint cb, out uint lpcbNeeded);

    [Fact]
    public void Benchmark_ProcessEnumeration_OptimizedVsLegacy()
    {
        // 1. Benchmark Optimized Approach (K32EnumProcesses + PID array)
        const int iterations = 10;
        long memBeforeOpt = GC.GetTotalMemory(true);
        var swOpt = Stopwatch.StartNew();
        int totalPidsOpt = 0;
        for (int i = 0; i < iterations; i++)
        {
            uint[] pids = new uint[1024];
            if (EnumProcesses(pids, (uint)(pids.Length * sizeof(uint)), out uint bytesNeeded))
            {
                int count = (int)(bytesNeeded / sizeof(uint));
                for (int j = 0; j < count; j++)
                {
                    if (pids[j] != 0) totalPidsOpt++;
                }
            }
        }
        swOpt.Stop();
        long memAfterOpt = GC.GetTotalMemory(false);
        double avgOptMs = (double)swOpt.ElapsedMilliseconds / iterations;
        long allocOptBytes = Math.Max(0, memAfterOpt - memBeforeOpt);

        // 2. Legacy Approach simulation (Process.GetProcesses() creating Managed Process instances)
        long memBeforeLegacy = GC.GetTotalMemory(true);
        var swLegacy = Stopwatch.StartNew();
        int totalPidsLegacy = 0;
        for (int i = 0; i < iterations; i++)
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    if (proc.Id != 0) totalPidsLegacy++;
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        swLegacy.Stop();
        long memAfterLegacy = GC.GetTotalMemory(false);
        double avgLegacyMs = (double)swLegacy.ElapsedMilliseconds / iterations;
        long allocLegacyBytes = Math.Max(0, memAfterLegacy - memBeforeLegacy);

        _output.WriteLine("================================================================================");
        _output.WriteLine("BENCHMARK 1: Privacy Indicator Process Enumeration (per scan)");
        _output.WriteLine($"  - Legacy (Process.GetProcesses):  {avgLegacyMs:F2} ms/scan | Alloc: {allocLegacyBytes / 1024:N0} KB");
        _output.WriteLine($"  - Optimized (K32EnumProcesses):   {avgOptMs:F2} ms/scan | Alloc: {allocOptBytes / 1024:N0} KB");
        if (avgOptMs > 0)
        {
            _output.WriteLine($"  -> SPEEDUP: {avgLegacyMs / Math.Max(0.001, avgOptMs):F1}x faster");
        }
        _output.WriteLine("================================================================================");

        Assert.True(avgOptMs <= avgLegacyMs * 1.5, "Optimized scan should be faster or comparable to legacy scan.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BenchmarkMouseHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [Fact]
    public void Benchmark_MouseHookPointerDereferencing_UnsafeVsMarshal()
    {
        unsafe
        {
            var testStruct = new BenchmarkMouseHookStruct
            {
                X = 1920,
                Y = 1080,
                MouseData = 0,
                Flags = 0,
                Time = 123456,
                DwExtraInfo = IntPtr.Zero
            };

            IntPtr lParam = Marshal.AllocHGlobal(sizeof(BenchmarkMouseHookStruct));
            try
            {
                Marshal.StructureToPtr(testStruct, lParam, false);

                const int iterations = 1_000_000;

                // 1. Marshal.PtrToStructure (Legacy boxing)
                var swMarshal = Stopwatch.StartNew();
                int sumXMarshal = 0;
                for (int i = 0; i < iterations; i++)
                {
                    var s = Marshal.PtrToStructure<BenchmarkMouseHookStruct>(lParam);
                    sumXMarshal += s.X;
                }
                swMarshal.Stop();

                // 2. Unsafe pointer dereference (Optimized zero-alloc)
                var swUnsafe = Stopwatch.StartNew();
                int sumXUnsafe = 0;
                for (int i = 0; i < iterations; i++)
                {
                    var s = *(BenchmarkMouseHookStruct*)lParam;
                    sumXUnsafe += s.X;
                }
                swUnsafe.Stop();

                _output.WriteLine("================================================================================");
                _output.WriteLine($"BENCHMARK 2: Low-Level Mouse Hook Dispatch ({iterations:N0} events)");
                _output.WriteLine($"  - Legacy (Marshal.PtrToStructure): {swMarshal.ElapsedMilliseconds} ms ({(double)swMarshal.Elapsed.TotalNanoseconds / iterations:F1} ns/event)");
                _output.WriteLine($"  - Optimized (Unsafe pointer deref): {swUnsafe.ElapsedMilliseconds} ms ({(double)swUnsafe.Elapsed.TotalNanoseconds / iterations:F1} ns/event)");
                if (swUnsafe.ElapsedMilliseconds > 0)
                {
                    _output.WriteLine($"  -> SPEEDUP: {(double)swMarshal.ElapsedMilliseconds / swUnsafe.ElapsedMilliseconds:F1}x faster");
                }
                _output.WriteLine("================================================================================");

                Assert.Equal(sumXMarshal, sumXUnsafe);
                Assert.True(swUnsafe.ElapsedMilliseconds <= swMarshal.ElapsedMilliseconds);
            }
            finally
            {
                Marshal.FreeHGlobal(lParam);
            }
        }
    }

    [Fact]
    public void Benchmark_ArtworkCropEncoding_BmpVsPng()
    {
        SharedStaTestRunner.Run(() =>
        {
            int w = 500;
            int h = 500;
            byte[] pixelData = new byte[w * h * 4];
            Random.Shared.NextBytes(pixelData);

            var source = BitmapSource.Create(
                w, h, 96, 96,
                PixelFormats.Bgra32, null,
                pixelData, w * 4);
            source.Freeze();

            var rect = new Int32Rect(50, 50, 400, 400);

            // Warmup
            var cropped = new CroppedBitmap(source, rect);
            cropped.Freeze();

            // 1. PngBitmapEncoder Roundtrip (Legacy)
            const int iterations = 10;
            var swPng = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                using var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                encoder.Save(ms);
                ms.Position = 0;

                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = ms;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
            }
            swPng.Stop();
            double avgPngMs = (double)swPng.ElapsedMilliseconds / iterations;

            // 2. BmpBitmapEncoder Roundtrip (Optimized)
            var swBmp = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                using var ms = new MemoryStream();
                var encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                encoder.Save(ms);
                ms.Position = 0;

                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = ms;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
            }
            swBmp.Stop();
            double avgBmpMs = (double)swBmp.ElapsedMilliseconds / iterations;

            _output.WriteLine("================================================================================");
            _output.WriteLine($"BENCHMARK 3: Artwork Crop Encoding Roundtrip (500x500 image, {iterations} runs)");
            _output.WriteLine($"  - Legacy (PngBitmapEncoder roundtrip): {avgPngMs:F2} ms/crop");
            _output.WriteLine($"  - Optimized (BmpBitmapEncoder roundtrip): {avgBmpMs:F2} ms/crop");
            if (avgBmpMs > 0)
            {
                _output.WriteLine($"  -> SPEEDUP: {avgPngMs / avgBmpMs:F1}x faster");
            }
            _output.WriteLine("================================================================================");

            Assert.True(avgBmpMs < avgPngMs, "BMP encoding roundtrip should be significantly faster than PNG deflate.");
        });
    }

    [Fact]
    public void Benchmark_DynamicIslandColorExtractor_Caching()
    {
        SharedStaTestRunner.Run(() =>
        {
            int w = 250;
            int h = 250;
            byte[] pixelData = new byte[w * h * 4];
            Random.Shared.NextBytes(pixelData);

            var source = BitmapSource.Create(
                w, h, 96, 96,
                PixelFormats.Bgra32, null,
                pixelData, w * 4);
            source.Freeze();

            // 1. Cold extraction
            var swCold = Stopwatch.StartNew();
            var paletteCold = DynamicIslandColorExtractor.GetDynamicIslandPalette(source);
            swCold.Stop();
            double coldMs = swCold.Elapsed.TotalMilliseconds;

            // 2. Cached lookup (ConditionalWeakTable)
            const int cachedIterations = 10_000;
            var swCached = Stopwatch.StartNew();
            for (int i = 0; i < cachedIterations; i++)
            {
                var paletteCached = DynamicIslandColorExtractor.GetDynamicIslandPalette(source);
                _ = paletteCached.Main;
            }
            swCached.Stop();
            double avgCachedUs = (swCached.Elapsed.TotalMilliseconds * 1000.0) / cachedIterations;

            _output.WriteLine("================================================================================");
            _output.WriteLine("BENCHMARK 4: DynamicIsland Color Extraction Caching");
            _output.WriteLine($"  - Cold Extraction (Downsample + HSV buckets): {coldMs:F2} ms");
            _output.WriteLine($"  - Cached Retrieval (ConditionalWeakTable):    {avgCachedUs:F2} microseconds/call");
            _output.WriteLine($"  -> SPEEDUP: {((coldMs * 1000.0) / avgCachedUs):N0}x faster on repeated access");
            _output.WriteLine("================================================================================");

            Assert.NotEqual(default, paletteCold.Main);
        });
    }
}
