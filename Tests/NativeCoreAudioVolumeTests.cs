using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class NativeCoreAudioVolumeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Read-only: the machine's volume and mute state are never changed.
    [Fact]
    public void DefaultEndpointReadsWithinRangeAndSurvivesRefresh()
    {
        using var service = new VolumeService();
        output.WriteLine("available=" + service.IsAvailable);
        if (!service.IsAvailable) return; // No render endpoint (for example a headless CI agent).

        float volume = service.GetVolume();
        output.WriteLine("volume=" + volume);
        Assert.InRange(volume, 0f, 1f);
        _ = service.GetMute();

        Assert.True(service.RefreshDefaultDevice());
        Assert.InRange(service.GetVolume(), 0f, 1f);
    }

    [Fact]
    public void DisposeIsIdempotentAndStopsServingReads()
    {
        var service = new VolumeService();
        service.Dispose();
        service.Dispose();
        Assert.False(service.IsAvailable);
    }
}
