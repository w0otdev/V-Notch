using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed partial class NativeMicrophoneProbeTests
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_create")]
    private static unsafe partial int Create(delegate* unmanaged[Stdcall]<long, void> callback, long context, out long handle, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_destroy")]
    private static partial int Destroy(long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_rebuild")]
    private static partial int Rebuild(long handle, out int count, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_keep")]
    private static unsafe partial int Keep(long handle, uint* processIds, int count, out int remaining);

    [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_sample")]
    private static partial int Sample(long handle, out byte hasActive, out float peak, out byte anyInvalid);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void Ignore(long context) { /* no-op callback */ }

    // Read-only: capture endpoints and sessions are listed and metered, never modified.
    [Fact]
    public unsafe void ProbeRebuildsKeepsNothingAndSamplesWithoutSessions()
    {
        if (Create(&Ignore, 0, out long handle, out _) != 0) return; // No audio subsystem on this machine.
        try
        {
            Assert.Equal(0, Rebuild(handle, out int count, out _));
            Assert.InRange(count, 0, 4096);

            // Dropping every session leaves an empty, valid probe.
            Assert.Equal(0, Keep(handle, null, 0, out int remaining));
            Assert.Equal(0, remaining);
            Assert.Equal(0, Sample(handle, out byte active, out float peak, out byte invalid));
            Assert.Equal(0, active);
            Assert.Equal(0f, peak);
            Assert.Equal(0, invalid);
        }
        finally { Assert.Equal(0, Destroy(handle)); }
    }

    [Fact]
    public unsafe void InvalidHandlesAreRejectedWithoutCrashing()
    {
        Assert.NotEqual(0, Rebuild(0, out _, out _));
        Assert.NotEqual(0, Sample(12345, out _, out _, out _));
        Assert.NotEqual(0, Destroy(0));
    }
}
