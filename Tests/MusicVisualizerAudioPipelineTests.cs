using System.Windows.Media;
using NAudio.Wave;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MusicVisualizerAudioPipelineTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    [InlineData(1000)]
    [InlineData(4000)]
    [InlineData(10000)]
    public void PcmTonesDriveFiniteSpectrumLevelsAndSilenceResetsThem(double frequency) => SharedStaTestRunner.Run(() => WithAudioState(() =>
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        byte[] pcm = Tone(frequency, .5f, 32768, 2);
        Process(pcm, pcm.Length, format);
        float[] first = MusicVisualizer.PublishedTargets.ToArray();
        Assert.Contains(first, value => value > .05f);
        Assert.All(first, value => Assert.InRange(value, 0, 1));
        Assert.True(MusicVisualizer.PublishedFrameTicks > 0);
        Assert.True(MusicVisualizer.LatestRmsNormalized > 0);
        Assert.InRange(MusicVisualizer.PublishedBeatAccent, 0, 1);
        MusicVisualizer.ResetAudioState();
        Assert.All(MusicVisualizer.PublishedTargets, value => Assert.Equal(0, value));
        Assert.Equal(0, MusicVisualizer.PublishedFrameTicks);
        Process(new byte[pcm.Length], pcm.Length, format);
        Assert.All(MusicVisualizer.PublishedTargets, value => Assert.Equal(0, value));
    }));

    [Fact]
    public void OppositeStereoChannelsCancelAndPartialFramesDoNotReadOutsideTheBuffer() => SharedStaTestRunner.Run(() => WithAudioState(() =>
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        byte[] pcm = Tone(1000, .5f, 8192, 2, invertRight: true);
        Process(pcm, pcm.Length + 100, format);
        Assert.All(MusicVisualizer.PublishedTargets, value => Assert.Equal(0, value));
        int position = MusicVisualizer.FftInputPosition;
        Process(new byte[7], 7, format);
        Assert.Equal(position, MusicVisualizer.FftInputPosition);
        Process(pcm, -10, format);
        Assert.Equal(position, MusicVisualizer.FftInputPosition);
    }));

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, true)]
    public void SampleDecodingPreservesPolarityAndHalfScaleAcrossPcmAndFloatFormats(int bits, bool floating) => SharedStaTestRunner.Run(() =>
    {
        _ = WaveFormat.CreateCustomFormat(floating ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm, 44100, 1, 44100 * bits / 8, bits / 8, bits);
        foreach (float sample in new[] { -.5f, 0f, .5f })
        {
            byte[] bytes = bits switch
            {
                8 => [(byte)(128 + sample * 128)],
                16 => BitConverter.GetBytes((short)(sample * 32768)),
                24 => BitConverter.GetBytes((int)(sample * 8388608)).Take(3).ToArray(),
                32 when floating => BitConverter.GetBytes(sample),
                32 => BitConverter.GetBytes((int)(sample * 2147483648d)),
                _ => BitConverter.GetBytes((double)sample)
            };
            Assert.Equal(sample, VNotch.Services.VisualizerDsp.ReadSample(bytes, 0, floating, bits), 5);
        }
        if (bits == 32 && floating)
        {
            _ = new WaveFormatExtensible(44100, 32, 1);
            Assert.Equal(.5f, VNotch.Services.VisualizerDsp.ReadSample(BitConverter.GetBytes(.5f), 0, true, 32), 5);
        }
    });

    [Fact]
    public void SpectrumMotionUsesFreshFramesAndUnloadedControlsDiscardCopiedFeedback() => SharedStaTestRunner.Run(() => WithAudioState(() =>
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        byte[] pcm = Tone(1000, .4f, 8192, 1);
        Process(pcm, pcm.Length, format);
        var visualizer = new MusicVisualizer { TrackId = "Fixture track" };
        float[] levels = visualizer.GetLatestDisplayLevels(out bool hasFreshAudio, out _);
        Assert.True(hasFreshAudio);
        Assert.Contains(levels, value => value > 0);
        visualizer.State = VisualizerState.Playing;
        for (int i = 0; i < 120; i++) visualizer.UpdateAnimation(.016, i * .016);
        double[] heights = visualizer.CurrentHeights.ToArray();
        Assert.All(heights, value => Assert.True(double.IsFinite(value) && value >= .08 && value <= 1));
        Assert.True(heights.Max() > .08);
        visualizer.SetCopiedFeedback(true);
        Assert.False(visualizer.IsCopiedFeedbackActive);
        visualizer.SetCopiedFeedback(false);
        Assert.False(visualizer.IsCopiedFeedbackActive);
        MusicVisualizer.PublishedFrameTicks = DateTime.UtcNow.AddSeconds(-5).Ticks;
        visualizer.GetLatestDisplayLevels(out hasFreshAudio, out _);
        Assert.False(hasFreshAudio);
    }));

    [Fact]
    public void LoadedPausedVisualizerMorphsToCopiedFeedbackAndReleasesItWhenUnloaded() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var visualizer = new MusicVisualizer { TrackId = "Fixture paused track", Width = 80, Height = 40 };
        var host = new BackgroundWindow { Width = 100, Height = 60, Content = visualizer };
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => visualizer.IsLoaded, "visualizer host loaded", ct);
            visualizer.SetCopiedFeedback(true);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(visualizer.CheckMix - 1) < 1e-6, "copied visualizer morph", ct);
            Assert.True(visualizer.IsCopiedFeedbackActive);
            await WpfFrameWaiter.UntilAsync(() => !visualizer.IsRenderingActive, "settled copied feedback releases rendering", ct);
            visualizer.SetCopiedFeedback(false);
            await WpfFrameWaiter.UntilAsync(() => visualizer.IconMix == 0, "return to paused bars", ct);
        }
        finally { host.Close(); }
        await WpfFrameWaiter.NextAsync(ct);
        Assert.False(visualizer.IsRenderingActive);
        Assert.False(visualizer.HoldsCaptureLease);
    });

    [Fact]
    public void HiddenLoadedVisualizerReleasesItsLeaseAndCannotAcquireFromPlaybackChanges() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var visualizer = new MusicVisualizer { TrackId = "Fixture paused track", Width = 80, Height = 40 };
        var host = new BackgroundWindow { Width = 100, Height = 60, Content = visualizer };
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => visualizer.IsLoaded, "visualizer host loaded", ct);
            // A lease alone does not start the driver, so this exercises the
            // lifecycle without opening an audio device in the regression test.
            visualizer.AcquireCaptureLease();
            Assert.True(visualizer.HoldsCaptureLease);
            visualizer.Visibility = System.Windows.Visibility.Collapsed;
            Assert.True(visualizer.IsLoaded);
            Assert.False(visualizer.HoldsCaptureLease);
            Assert.False(visualizer.IsRenderingActive);
            visualizer.IsPlaying = true;
            visualizer.IsBuffering = true;
            Assert.False(visualizer.HoldsCaptureLease);
            visualizer.IsBuffering = false;
            visualizer.IsPlaying = false;
        }
        finally { host.Close(); }
    });

    private static void WithAudioState(Action action)
    {
        lock (MusicVisualizer.AudioStateLock)
        {
            MusicVisualizer.ResetAudioState();
            try { action(); }
            finally { MusicVisualizer.ResetAudioState(); }
        }
    }
    private static byte[] Tone(double frequency, float amplitude, int samples, int channels, bool invertRight = false)
    {
        byte[] buffer = new byte[samples * channels * sizeof(float)];
        for (int i = 0; i < samples; i++)
        {
            float sample = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / 44100);
            for (int channel = 0; channel < channels; channel++)
                BitConverter.TryWriteBytes(buffer.AsSpan((i * channels + channel) * sizeof(float), sizeof(float)), invertRight && channel == 1 ? -sample : sample);
        }
        return buffer;
    }
    // The product code takes the decoded format fields; tests describe formats with NAudio types.
    private static void Process(byte[] buffer, int bytes, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible extensible && extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        MusicVisualizer.ProcessAudioBuffer(buffer, bytes, format.Channels, format.BitsPerSample, isFloat);
    }
}
