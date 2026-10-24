using System.Runtime.InteropServices;
using Xunit;

namespace VNotch.Tests;

public sealed partial class NativeInteractionPlaybackTests
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_interaction_play(int cue, int variant, float volume, int burstCount);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_interaction_stop();

    // Volume 0 scales every sample to silence, so the test exercises synthesis, gain and PlaySound without sound.
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(9)]
    public void SilentPlaybackSucceedsOrReportsNoDeviceWithoutCrashing(int cue)
    {
        int status = vn_interaction_play(cue, 0, 0f, 2);
        Assert.True(status == 0 || status == -3, $"unexpected status {status}");
        Assert.Equal(0, vn_interaction_stop());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 6)]
    public void OutOfRangeCuesAndVariantsAreRejected(int cue, int variant) =>
        Assert.NotEqual(0, vn_interaction_play(cue, variant, 0f, 0));
}
