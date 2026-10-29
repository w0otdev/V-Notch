using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class NativeLoopbackCaptureTests(ITestOutputHelper output)
{
    // Read-only: the default render endpoint is tapped in loopback mode and released again.
    [Fact]
    public async Task DefaultEndpointStartsReportsItsFormatAndStopsCleanly()
    {
        int packets = 0;
        var capture = NativeLoopbackCapture.Start(string.Empty, (_, bytes) => Interlocked.Add(ref packets, bytes > 0 ? 1 : 0), () => { }, out int hresult);
        if (capture == null)
        {
            output.WriteLine($"no endpoint (0x{hresult:X8})");
            return; // No render endpoint, for example a headless CI agent.
        }

        using (capture)
        {
            output.WriteLine($"{capture.SampleRate} Hz, {capture.Channels} ch, {capture.BitsPerSample} bit, float={capture.IsFloat}, id={capture.DeviceId}");
            Assert.InRange(capture.SampleRate, 8000, 384000);
            Assert.InRange(capture.Channels, 1, 16);
            Assert.True(capture.BitsPerSample is 8 or 16 or 24 or 32 or 64);
            Assert.False(string.IsNullOrWhiteSpace(capture.DeviceId));
            Assert.True(capture.IsDeviceActive());
            Assert.Equal(capture.DeviceId, NativeLoopbackCapture.ResolveDeviceId(string.Empty));
            await Task.Delay(150);
        }

        int seen = Volatile.Read(ref packets);
        await Task.Delay(100);
        Assert.Equal(seen, Volatile.Read(ref packets)); // No callbacks after dispose.
    }

    [Fact]
    public void UnknownEndpointFallsBackToTheDefault()
    {
        string? resolved = NativeLoopbackCapture.ResolveDeviceId("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}");
        string? defaultId = NativeLoopbackCapture.ResolveDeviceId(string.Empty);
        Assert.Equal(defaultId, resolved);
    }
}
