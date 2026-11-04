using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

public sealed partial class PrivacyIndicatorService
{
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consumers_create(ReadOnlySpan<Win32Interop.VnProcessEntry> entries, int count, out long handle);
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consumers_close(long handle);
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consumers_match(NativeConsumersHandle handle, ReadOnlySpan<char> raw, int length, uint pid, out byte match);
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consent_cache_create(out long handle);
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consent_cache_close(long handle);
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consent_cache_refresh(NativeConsentCache handle, byte changed);
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consumers_cache_package(uint pid, ReadOnlySpan<char> family, int length, long expires, long now);
    [LibraryImport(NativeDll)]
    private static partial int vn_privacy_consumers_query_package_cache(uint pid, out int count, out byte contains);
    [LibraryImport(NativeDll)]
    private static partial int vn_privacy_consumers_clear_package_cache();
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_consumers_scan(NativeConsumersHandle handle, NativeConsentCache cache, ReadOnlySpan<char> capability, int length,
        Span<VnConsentStoreEntry> output, int capacity, out int count);

    private sealed class NativeConsumersHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeConsumersHandle(ReadOnlySpan<Win32Interop.VnProcessEntry> entries) : base(true)
        {
            if (vn_privacy_consumers_create(entries, entries.Length, out long value) != 0) throw new InvalidOperationException("Cannot capture privacy consumers.");
            SetHandle((IntPtr)value);
        }
        protected override bool ReleaseHandle() => vn_privacy_consumers_close(handle.ToInt64()) == 0;
    }

    internal sealed class NativeConsentCache : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeConsentCache(long value) : base(true) => SetHandle((IntPtr)value);
        internal static NativeConsentCache Create()
        {
            if (vn_privacy_consent_cache_create(out long value) != 0) throw new InvalidOperationException("Cannot initialize privacy scan.");
            return new(value);
        }
        protected override bool ReleaseHandle() => vn_privacy_consent_cache_close(handle.ToInt64()) == 0;
    }

    internal sealed class ConsumerProcessProbe : IDisposable
    {
        private PrivacyProcessSnapshot? _processSnapshot;
        private NativeConsumersHandle? _native;
        internal PrivacyProcessSnapshot? CapturedProcessSnapshot => _processSnapshot;
        internal PrivacyProcessSnapshot GetProcessSnapshot() => _processSnapshot ??= PrivacyProcessSnapshot.Capture();
        private NativeConsumersHandle Native => _native ??= new(GetProcessSnapshot().Entries);
        public bool IsRunning(string rawConsumer) => vn_privacy_consumers_match(Native, rawConsumer, rawConsumer.Length, 0, out byte matched) == 0 && matched != 0;
        public bool MatchesProcess(string rawConsumer, uint processId) => processId != 0 &&
            vn_privacy_consumers_match(Native, rawConsumer, rawConsumer.Length, processId, out byte matched) == 0 && matched != 0;
        internal static bool IsDesktopExecutableProcess(string executablePath, uint processId) => Win32Interop.ProcessImagePathEquals(processId, executablePath);
        internal static void CachePackageFamilyForPid(uint processId, string family, long expireTicks, long now) =>
            vn_privacy_consumers_cache_package(processId, family, family.Length, expireTicks, now);
        internal static int CachedPackageFamilyCount => vn_privacy_consumers_query_package_cache(0, out int count, out _) == 0 ? count : 0;
        internal static bool ContainsCachedPackageFamily(uint processId) =>
            vn_privacy_consumers_query_package_cache(processId, out _, out byte contains) == 0 && contains != 0;
        internal static void ClearPackageFamilyCache() => vn_privacy_consumers_clear_package_cache();
        public void Dispose() { _native?.Dispose(); _processSnapshot?.Dispose(); }

        internal IReadOnlyList<CapabilityUsage> Scan(NativeConsentCache cache, string capability)
        {
            Span<VnConsentStoreEntry> output = stackalloc VnConsentStoreEntry[32];
            int status = vn_privacy_consumers_scan(Native, cache, capability, capability.Length, output, output.Length, out int count);
            if (status == -2 && count > output.Length && count <= 65536)
            {
                var larger = new VnConsentStoreEntry[count + 32];
                status = vn_privacy_consumers_scan(Native, cache, capability, capability.Length, larger, larger.Length, out count);
                if (status == 0) return BuildConsentUsages(larger.AsSpan(0, count));
            }
            if (status != 0) throw new InvalidOperationException("Privacy consumer scan failed.");
            return count == 0 ? Array.Empty<CapabilityUsage>() : BuildConsentUsages(output[..count]);
        }
    }

    private PrivacyScanResult ExecuteNativeBackgroundScan(DateTime now, NativeConsentCache cache, bool changed)
    {
        if (vn_privacy_consent_cache_refresh(cache, changed ? (byte)1 : (byte)0) != 0) throw new InvalidOperationException("Privacy cache refresh failed.");
        using var running = new ConsumerProcessProbe();
        return BuildBackgroundScan(now, running.Scan(cache, "microphone"), running.Scan(cache, "webcam"),
            running.Scan(cache, "graphicsCaptureProgrammatic"), running.Scan(cache, "graphicsCaptureWithoutBorder"), running.Scan(cache, "location"), running, nativeConsumers: true);
    }
}
