using NAudio.Dsp;
using VNotch.Benchmarks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class VisualizerSpectrumBandsTests
{
    [Fact]
    public void CachedBandsMatchOriginalBitsAcrossFramesAndDeviceSampleRateChanges()
    {
        var original = new BaselineSpectrumBands();
        var optimized = new VisualizerSpectrumBands(512);
        var fft = new Complex[512];
        var random = new Random(42);
        foreach (int sampleRate in new[] { 44100, 48000, 96000, 8000, 192000, 22050, 0, -1, 44100 })
        for (int frame = 0; frame < 40; frame++)
        {
            for (int i = 0; i < fft.Length; i++)
            {
                fft[i].X = frame == 0 ? 0 : (float)(random.NextDouble() * 2 - 1);
                fft[i].Y = frame == 0 ? 0 : (float)(random.NextDouble() * 2 - 1);
            }
            var expected = original.Compute(fft, sampleRate);
            var actual = optimized.Compute(fft, sampleRate);
            for (int band = 0; band < expected.Length; band++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[band]), BitConverter.DoubleToInt64Bits(actual[band]));
        }
    }

    [Fact]
    public void SteadyStateDoesNotAllocate()
    {
        var bands = new VisualizerSpectrumBands(512);
        var fft = new Complex[512];
        for (int frame = 0; frame < 100; frame++) bands.Compute(fft, 48000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 100; frame++) bands.Compute(fft, 48000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
