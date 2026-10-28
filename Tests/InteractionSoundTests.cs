using System.IO;
using System.Text;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class InteractionSoundTests
{
    [Fact]
    public void DisabledSoundsAreSilentAndRapidEventsDoNotStack()
    {
        long now = 100;
        var played = new List<InteractionCue>();
        using var player = new InteractionSoundPlayer((cue, _) => played.Add(cue), () => now);
        player.Play(InteractionCue.Click);
        Assert.Empty(played);
        player.Enabled = true;
        player.Play(InteractionCue.ToggleOn);
        player.Play(InteractionCue.Click);
        now += 65;
        player.Play(InteractionCue.ToggleOff);
        player.Enabled = false;
        now += 100;
        player.Play(InteractionCue.Click);
        Assert.Equal(new[] { InteractionCue.ToggleOn, InteractionCue.ToggleOff }, played);
    }

    [Fact]
    public async Task SoundPreferenceSurvivesSaveReloadAndClone()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vnotch-sound-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new NotchSettings();
            Assert.True(settings.EnableInteractionSounds);
            var muted = settings.Clone();
            muted.EnableInteractionSounds = false;
            muted.InteractionSoundVolume = .25;
            Assert.False(settings.ValueEquals(muted));
            await using (var service = new SettingsService(Path.Combine(folder, "settings.json"), _ => { }))
                await service.SaveAsync(muted);
            await using var reload = new SettingsService(Path.Combine(folder, "settings.json"), _ => { });
            Assert.False(reload.Load().EnableInteractionSounds);
            Assert.Equal(.25, reload.Load().InteractionSoundVolume);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void CuesAreShortQuietPcmWithSilentEdges(int cue)
    {
        for (int variant = 0; variant < InteractionSoundPlayer.VariantCount; variant++)
            CheckWave(InteractionSoundPlayer.CreateWave((InteractionCue)cue, variant));
    }

    private static void CheckWave(byte[] wave)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(44100, BitConverter.ToInt32(wave, 24));
        Assert.Equal(wave.Length - 44, BitConverter.ToInt32(wave, 40));
        Assert.InRange((wave.Length - 44) / 88200.0, .015, .105);
        Assert.Equal(0, BitConverter.ToInt16(wave, 44));
        Assert.Equal(0, BitConverter.ToInt16(wave, wave.Length - 2));
        Assert.Contains(wave.Skip(44), value => value != 0);
        for (int offset = 44; offset < wave.Length; offset += 2)
            Assert.InRange((int)BitConverter.ToInt16(wave, offset), -30000, 30000);
    }

    [Fact]
    public void ShortClicksHaveConsistentLevelAndLimitedPeaks()
    {
        foreach (var cue in Enum.GetValues<InteractionCue>())
            for (int variant = 0; variant < InteractionSoundPlayer.VariantCount; variant++)
            {
                byte[] wave = InteractionSoundPlayer.CreateWave(cue, variant);
                double energy = 0;
                int peak = 0, count = (wave.Length - 44) / 2;
                for (int offset = 44; offset < wave.Length; offset += 2)
                {
                    int sample = BitConverter.ToInt16(wave, offset);
                    peak = Math.Max(peak, Math.Abs(sample));
                    energy += (double)sample * sample;
                }
                double rmsDb = 20 * Math.Log10(Math.Sqrt(energy / count) / 32768);
                Assert.True(rmsDb >= -21 && rmsDb <= -13, $"{cue}/{variant}: RMS {rmsDb:F1} dBFS");
                Assert.InRange(peak, 1, 27867);
            }
    }

    [Fact]
    public void RepeatedGesturesNeverReuseTheirPreviousVariant()
    {
        long now = 0;
        var played = new List<(InteractionCue Cue, int Variant)>();
        using var player = new InteractionSoundPlayer((cue, variant) => played.Add((cue, variant)), () => now) { Enabled = true };
        for (int i = 0; i < 30; i++)
            foreach (var cue in Enum.GetValues<InteractionCue>()) { now += 100; player.Play(cue); }
        foreach (var group in played.GroupBy(item => item.Cue))
        {
            var variants = group.Select(item => item.Variant).ToArray();
            for (int i = 1; i < variants.Length; i++) Assert.NotEqual(variants[i - 1], variants[i]);
        }
        var waves = Enum.GetValues<InteractionCue>().SelectMany(cue => Enumerable.Range(0, InteractionSoundPlayer.VariantCount)
            .Select(variant => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(InteractionSoundPlayer.CreateWave(cue, variant)))));
        Assert.Equal(Enum.GetValues<InteractionCue>().Length * InteractionSoundPlayer.VariantCount, waves.Distinct().Count());
    }

    [Fact]
    public void VolumeZeroMutesAndValuesStayWithinTheAllowedRange()
    {
        var played = new List<float>();
        InteractionSoundPlayer? player = null;
        using (player = new InteractionSoundPlayer((_, _) => played.Add(player!.Volume)))
        {
            player.Enabled = true;
            player.Volume = 0;
            player.Play(InteractionCue.Click);
            Assert.Empty(played);
            player.Volume = .25f;
            player.Play(InteractionCue.Click);
            Assert.Equal(new[] { .25f }, played);
            player.Volume = 3;
            Assert.Equal(1, player.Volume);
            player.Volume = -1;
            Assert.Equal(0, player.Volume);
            player.Volume = float.NaN;
            Assert.Equal(0, player.Volume);
        }
        var settings = new NotchSettings { InteractionSoundVolume = double.NaN };
        Assert.Equal(.55, settings.InteractionSoundVolume);
    }

    [Fact]
    public void ConfirmationHasAPairedTapAndButtonsUseTheirSemanticCue()
    {
        static double LateEnergy(byte[] wave)
        {
            double energy = 0;
            for (int offset = 44 + (int)(44100 * .014) * 2; offset < wave.Length; offset += 2)
            {
                double sample = BitConverter.ToInt16(wave, offset);
                energy += sample * sample;
            }
            return energy;
        }
        Assert.True(LateEnergy(InteractionSoundPlayer.CreateWave(InteractionCue.Confirm)) > 10000);
        Assert.Equal(0, LateEnergy(InteractionSoundPlayer.CreateWave(InteractionCue.Click)));
        SharedStaTestRunner.Run(() =>
        {
            foreach (var (name, expected) in new[]
            {
                ("SaveButton", InteractionCue.Confirm), ("CopyDetailButton", InteractionCue.Confirm),
                ("ConfirmDeleteButton", InteractionCue.Delete), ("CancelDeleteButton", InteractionCue.Click),
                ("ClearSearchButton", InteractionCue.Select), ("NavMedia", InteractionCue.Navigate),
                ("InlinePrevButton", InteractionCue.Transport), ("PlayPauseButton", InteractionCue.Transport)
            })
                Assert.Equal(expected, InteractionSounds.ClassifyAction(new System.Windows.Controls.Button { Name = name }));
        });
    }
}
