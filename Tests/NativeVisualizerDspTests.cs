using NAudio.Dsp;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class NativeVisualizerDspTests
{
    private static float[] Noise(int length, int seed)
    {
        var random = new Random(seed);
        var samples = new float[length];
        for (int i = 0; i < length; i++) samples[i] = (float)(random.NextDouble() * 2 - 1) * 0.8f;
        return samples;
    }

    // The band analysis was calibrated against NAudio's FFT, so the native transform must match it bin for bin.
    [Theory]
    [InlineData(512, 9, 1)]
    [InlineData(512, 9, 2)]
    [InlineData(256, 8, 3)]
    [InlineData(1024, 10, 4)]
    public void WindowedFftMatchesTheNAudioReference(int length, int m, int seed)
    {
        float[] samples = Noise(length, seed);

        var reference = new Complex[length];
        for (int i = 0; i < length; i++)
        {
            double window = 0.54 - 0.46 * Math.Cos((2 * Math.PI * i) / (length - 1));
            reference[i].X = (float)(samples[i] * window);
            reference[i].Y = 0;
        }
        FastFourierTransform.FFT(true, m, reference);

        var native = new SpectrumComplex[length];
        Assert.True(VisualizerDsp.WindowedFft(samples, native));

        for (int i = 0; i < length; i++)
        {
            Assert.Equal(reference[i].X, native[i].X, 6);
            Assert.Equal(reference[i].Y, native[i].Y, 6);
        }
    }

    [Fact]
    public void ToneEnergyLandsInTheExpectedBin()
    {
        const int length = 512;
        const double sampleRate = 44100;
        int bin = 20;
        double frequency = bin * sampleRate / length;
        var samples = new float[length];
        for (int i = 0; i < length; i++) samples[i] = (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);

        var fft = new SpectrumComplex[length];
        Assert.True(VisualizerDsp.WindowedFft(samples, fft));

        int peak = 1;
        double best = 0;
        for (int i = 1; i < length / 2; i++)
        {
            double magnitude = Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
            if (magnitude > best)
            {
                best = magnitude;
                peak = i;
            }
        }
        Assert.Equal(bin, peak);
    }

    [Fact]
    public void NonPowerOfTwoLengthsAreRejected() =>
        Assert.False(VisualizerDsp.WindowedFft(new float[500], new SpectrumComplex[500]));

    [Theory]
    [InlineData(false, 8, new byte[] { 64 }, -0.5f)]
    [InlineData(false, 16, new byte[] { 0x00, 0xC0 }, -0.5f)]
    [InlineData(false, 24, new byte[] { 0x00, 0x00, 0xC0 }, -0.5f)]
    [InlineData(false, 32, new byte[] { 0x00, 0x00, 0x00, 0xC0 }, -0.5f)]
    [InlineData(true, 32, new byte[] { 0x00, 0x00, 0x00, 0x3F }, 0.5f)]
    public void SamplesDecodeWithTheirPolarity(bool floating, int bits, byte[] bytes, float expected) =>
        Assert.Equal(expected, VisualizerDsp.ReadSample(bytes, 0, floating, bits), 5);

    [Fact]
    public void ReadsPastTheBufferAndUnsupportedWidthsYieldZero()
    {
        Assert.Equal(0f, VisualizerDsp.ReadSample(new byte[3], 0, false, 32));
        Assert.Equal(0f, VisualizerDsp.ReadSample(new byte[8], 0, false, 12));
        Assert.Equal(0f, VisualizerDsp.ReadSample(new byte[4], -1, true, 32));
    }
}
