using System.Runtime.InteropServices;
using VNotch.Interop;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed partial class NativeCoreAudioVolumeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_source_destroy(long handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_endpoint_open(long source, out long handle, out int hresult);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_endpoint_close(long handle);

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

    [Fact]
    public void RepeatedServiceCyclesAndInvalidHandlesDoNotLeakOrCrash()
    {
        for (int i = 0; i < 5; i++)
        {
            using var service = new VolumeService();
            _ = service.IsAvailable;
        }

        Assert.Equal((int)NativeStatus.InvalidArgument, vn_volume_source_destroy(0));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_volume_source_destroy(-1));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_volume_endpoint_open(0, out _, out _));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_volume_endpoint_close(0));
    }
}
