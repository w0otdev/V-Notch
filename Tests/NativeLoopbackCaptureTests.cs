using System.Runtime.InteropServices;
using VNotch.Interop;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed partial class NativeLoopbackCaptureTests(ITestOutputHelper output)
{
    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_loopback_stop(long handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static unsafe partial int vn_loopback_get_device(long handle, char* buffer, int capacity, out int length, out byte active);

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

    [Fact]
    public void RepeatedCreateAndDisposeCyclesReleaseCleanly()
    {
        for (int i = 0; i < 10; i++)
        {
            var capture = NativeLoopbackCapture.StartAnalyzed(string.Empty, (_, _) => { }, () => { }, out int hresult);
            if (capture == null)
            {
                output.WriteLine($"no endpoint on cycle {i} (0x{hresult:X8})");
                return;
            }

            capture.Dispose();
            capture.Dispose(); // Idempotent double-dispose.
        }
    }

    [Fact]
    public async Task DisposingFromCallbackDoesNotCrashOrContinueDelivering()
    {
        NativeLoopbackCapture? capture = null;
        int callbackCount = 0;
        var firstCallback = new ManualResetEventSlim(false);
        capture = NativeLoopbackCapture.Start(string.Empty, (_, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            firstCallback.Set();
            capture?.Dispose();
        }, () => { }, out int hresult);

        if (capture == null)
        {
            output.WriteLine($"no endpoint (0x{hresult:X8})");
            return;
        }

        try
        {
            firstCallback.Wait(TimeSpan.FromMilliseconds(500));
            capture.Dispose();
            int afterDispose = Volatile.Read(ref callbackCount);
            await Task.Delay(80);
            Assert.Equal(afterDispose, Volatile.Read(ref callbackCount));
        }
        finally
        {
            capture.Dispose();
        }
    }

    [Fact]
    public unsafe void BoundaryRejectsInvalidHandlesAndOversizedDeviceIds()
    {
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_loopback_stop(0));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_loopback_stop(-1));

        char* buf = stackalloc char[16];
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_loopback_get_device(0, buf, 16, out _, out _));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_loopback_get_device(999999, buf, 16, out _, out _));

        string oversized = new('a', 1025);
        Assert.Null(NativeLoopbackCapture.ResolveDeviceId(oversized));
        Assert.Null(NativeLoopbackCapture.Start(oversized, (_, _) => { }, () => { }, out _));
    }
}
