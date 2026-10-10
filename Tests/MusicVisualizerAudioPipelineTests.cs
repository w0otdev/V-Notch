using System.Reflection;
using System.Windows.Media;
using NAudio.Wave;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class MusicVisualizerAudioPipelineTests
{
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;

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
        MusicVisualizer.ProcessAudioBuffer(pcm, pcm.Length, format);
        float[] first = Get<float[]>("_publishedTargets").ToArray();
        Assert.Contains(first, value => value > .05f);
        Assert.All(first, value => Assert.InRange(value, 0, 1));
        Assert.True(Get<long>("_publishedFrameTicks") > 0);
        Assert.True(Get<float>("_latestRmsNormalized") > 0);
        Assert.InRange(Get<float>("_publishedBeatAccent"), 0, 1);
        Invoke("ResetAudioState");
        Assert.All(Get<float[]>("_publishedTargets"), value => Assert.Equal(0, value));
        Assert.Equal(0, Get<long>("_publishedFrameTicks"));
        MusicVisualizer.ProcessAudioBuffer(new byte[pcm.Length], pcm.Length, format);
        Assert.All(Get<float[]>("_publishedTargets"), value => Assert.Equal(0, value));
    }));

    [Fact]
    public void OppositeStereoChannelsCancelAndPartialFramesDoNotReadOutsideTheBuffer() => SharedStaTestRunner.Run(() => WithAudioState(() =>
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        byte[] pcm = Tone(1000, .5f, 8192, 2, invertRight: true);
        MusicVisualizer.ProcessAudioBuffer(pcm, pcm.Length + 100, format);
        Assert.All(Get<float[]>("_publishedTargets"), value => Assert.Equal(0, value));
        int position = Get<int>("_fftInputPos");
        MusicVisualizer.ProcessAudioBuffer(new byte[7], 7, format);
        Assert.Equal(position, Get<int>("_fftInputPos"));
        MusicVisualizer.ProcessAudioBuffer(pcm, -10, format);
        Assert.Equal(position, Get<int>("_fftInputPos"));
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
        WaveFormat format = WaveFormat.CreateCustomFormat(floating ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm, 44100, 1, 44100 * bits / 8, bits / 8, bits);
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
            Assert.Equal(sample, (float)Invoke("ReadSampleAsFloat", bytes, 0, format)!, 5);
        }
        if (bits == 32 && floating)
        {
            var extensible = new WaveFormatExtensible(44100, 32, 1);
            Assert.Equal(.5f, (float)Invoke("ReadSampleAsFloat", BitConverter.GetBytes(.5f), 0, extensible)!, 5);
        }
    });

    [Fact]
    public void SpectrumMotionUsesFreshFramesAndUnloadedControlsDiscardCopiedFeedback() => SharedStaTestRunner.Run(() => WithAudioState(() =>
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        byte[] pcm = Tone(1000, .4f, 8192, 1);
        MusicVisualizer.ProcessAudioBuffer(pcm, pcm.Length, format);
        var visualizer = new MusicVisualizer { TrackId = "Fixture track" };
        object?[] freshness = [false, 0d];
        float[] levels = (float[])typeof(MusicVisualizer).GetMethod("GetLatestDisplayLevels", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(visualizer, freshness)!;
        Assert.True((bool)freshness[0]!);
        Assert.Contains(levels, value => value > 0);
        typeof(MusicVisualizer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(visualizer, VisualizerState.Playing);
        var update = typeof(MusicVisualizer).GetMethod("UpdateAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 120; i++) update.Invoke(visualizer, [.016, i * .016]);
        double[] heights = (double[])typeof(MusicVisualizer).GetField("_currentHeights", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(visualizer)!;
        Assert.All(heights, value => Assert.True(double.IsFinite(value) && value >= .08 && value <= 1));
        Assert.True(heights.Max() > .08);
        visualizer.SetCopiedFeedback(true);
        Assert.False((bool)typeof(MusicVisualizer).GetField("_copiedFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(visualizer)!);
        visualizer.SetCopiedFeedback(false);
        Assert.False((bool)typeof(MusicVisualizer).GetField("_copiedFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(visualizer)!);
        typeof(MusicVisualizer).GetField("_publishedFrameTicks", Private)!.SetValue(null, DateTime.UtcNow.AddSeconds(-5).Ticks);
        freshness = [false, 0d];
        typeof(MusicVisualizer).GetMethod("GetLatestDisplayLevels", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(visualizer, freshness);
        Assert.False((bool)freshness[0]!);
    }));

    [Fact]
    public void LoadedPausedVisualizerMorphsToCopiedFeedbackAndReleasesItWhenUnloaded() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var visualizer = new MusicVisualizer { TrackId = "Fixture paused track", Width = 80, Height = 40 };
        var host = new BackgroundWindow { Width = 100, Height = 60, Content = visualizer };
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => visualizer.IsLoaded, "visualizer host loaded", ct);
            visualizer.SetCopiedFeedback(true);
            await WpfFrameWaiter.UntilAsync(() => (double)typeof(MusicVisualizer).GetField("_checkMix", instance)!.GetValue(visualizer)! == 1, "copied visualizer morph", ct);
            Assert.True((bool)typeof(MusicVisualizer).GetField("_copiedFeedback", instance)!.GetValue(visualizer)!);
            await WpfFrameWaiter.UntilAsync(() => !(bool)typeof(MusicVisualizer).GetField("_isRenderingActive", instance)!.GetValue(visualizer)!, "settled copied feedback releases rendering", ct);
            visualizer.SetCopiedFeedback(false);
            await WpfFrameWaiter.UntilAsync(() => (double)typeof(MusicVisualizer).GetField("_iconMix", instance)!.GetValue(visualizer)! == 0, "return to paused bars", ct);
        }
        finally { host.Close(); }
        await WpfFrameWaiter.NextAsync(ct);
        Assert.False((bool)typeof(MusicVisualizer).GetField("_isRenderingActive", instance)!.GetValue(visualizer)!);
        Assert.False((bool)typeof(MusicVisualizer).GetField("_holdsCaptureLease", instance)!.GetValue(visualizer)!);
    });

    [Fact]
    public void HiddenLoadedVisualizerReleasesItsLeaseAndCannotAcquireFromPlaybackChanges() => SharedStaTestRunner.RunAsync(async ct =>
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var visualizer = new MusicVisualizer { TrackId = "Fixture paused track", Width = 80, Height = 40 };
        var host = new BackgroundWindow { Width = 100, Height = 60, Content = visualizer };
        var holdsLease = typeof(MusicVisualizer).GetField("_holdsCaptureLease", instance)!;
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => visualizer.IsLoaded, "visualizer host loaded", ct);
            // A lease alone does not start the driver, so this exercises the
            // lifecycle without opening an audio device in the regression test.
            typeof(MusicVisualizer).GetMethod("AcquireCaptureLease", instance)!.Invoke(visualizer, null);
            Assert.True((bool)holdsLease.GetValue(visualizer)!);
            visualizer.Visibility = System.Windows.Visibility.Collapsed;
            Assert.True(visualizer.IsLoaded);
            Assert.False((bool)holdsLease.GetValue(visualizer)!);
            Assert.False((bool)typeof(MusicVisualizer).GetField("_isRenderingActive", instance)!.GetValue(visualizer)!);
            visualizer.IsPlaying = true;
            visualizer.IsBuffering = true;
            Assert.False((bool)holdsLease.GetValue(visualizer)!);
            visualizer.IsBuffering = false;
            visualizer.IsPlaying = false;
        }
        finally { host.Close(); }
    });

    private static void WithAudioState(Action action)
    {
        lock (Get<object>("_lockObj"))
        {
            Invoke("ResetAudioState");
            try { action(); }
            finally { Invoke("ResetAudioState"); }
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
    private static T Get<T>(string name) => (T)typeof(MusicVisualizer).GetField(name, Private)!.GetValue(null)!;
    private static object? Invoke(string name, params object?[] arguments) => typeof(MusicVisualizer).GetMethod(name, Private)!.Invoke(null, arguments);
}
