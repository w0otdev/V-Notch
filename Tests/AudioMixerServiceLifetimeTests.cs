using System;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AudioMixerServiceLifetimeTests
{
    [Fact]
    public void AudioMixerService_CanBeCreatedAndDisposedSafely()
    {
        using var mixer = new AudioMixerService();
        Assert.NotNull(mixer);
        mixer.ReleaseSessionCache();
    }

    [Fact]
    public void AudioMixerService_NonExistentProcessVolumeReturnsFalseGracefully()
    {
        using var mixer = new AudioMixerService();
        // Process ID 99999999 should not exist or fail gracefully without throwing COM/AccessViolation exceptions
        bool result = mixer.SetSessionVolume(99999999, 0.5f);
        Assert.False(result);

        bool muteResult = mixer.ToggleSessionMute(99999999);
        Assert.False(muteResult);
    }
}
