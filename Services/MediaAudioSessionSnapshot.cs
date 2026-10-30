using System.Runtime.InteropServices;

namespace VNotch.Services;

internal interface IMediaAudioVolume : IDisposable
{
    float Volume { get; set; }
    bool Mute { get; set; }
    bool IsActive => true;
}

internal sealed record MediaAudioSessionSnapshot(
    uint GetProcessID, string? DisplayName, string? GetSessionIdentifier,
    string? GetSessionInstanceIdentifier, bool IsSystemSoundsSession,
    Func<float> ReadPeak, Func<IMediaAudioVolume> OpenVolume, int State = 1)
{
    internal IMediaAudioVolume SimpleAudioVolume => OpenVolume();
}

internal sealed class MediaAudioSessions(IReadOnlyList<MediaAudioSessionSnapshot> sessions, Action? dispose = null) : IDisposable
{
    internal IReadOnlyList<MediaAudioSessionSnapshot> Sessions { get; } = sessions;
    public void Dispose() => dispose?.Invoke();
}

// Session enumeration, peak meters and per-session volume live in Native/Services/AudioSessions.cpp.
internal static unsafe partial class WindowsMediaAudioSessions
{
    private const string NativeDll = "VNotch.Native.dll";
    private const int EFail = unchecked((int)0x80004005);
    private const int NativeBufferTooSmall = -2;

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_sessions_open")]
    internal static partial int NativeOpen(out long snapshot, out int count, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_sessions_open_all")]
    private static partial int NativeOpenAll(out long snapshot, out int count, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_session_get_state")]
    private static partial int NativeGetState(long snapshot, int index, out int state);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_sessions_close")]
    internal static partial int NativeClose(long snapshot);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_session_get_info")]
    internal static partial int NativeGetInfo(long snapshot, int index, out uint processId, out byte isSystemSounds);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_session_get_string")]
    private static partial int NativeGetString(long snapshot, int index, int kind, char* buffer, int capacity, out int length);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_session_read_peak")]
    private static partial int NativeReadPeak(long snapshot, int index, out float peak);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_session_open_volume")]
    internal static partial int NativeOpenVolume(long snapshot, int index, out long volume);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_volume_get")]
    internal static partial int NativeVolumeGet(long volume, out float scalar, out byte muted, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_volume_set_volume")]
    private static partial int NativeVolumeSet(long volume, float scalar, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_volume_set_mute")]
    private static partial int NativeVolumeSetMute(long volume, byte muted, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_volume_close")]
    internal static partial int NativeVolumeClose(long volume);

    [LibraryImport(NativeDll, EntryPoint = "vn_audio_volume_get_state")]
    private static partial int NativeVolumeGetState(long volume, out int state);

    internal static MediaAudioSessions Read() => Read(false);

    internal static MediaAudioSessions ReadAllEndpoints() => Read(true);

    private static MediaAudioSessions Read(bool allEndpoints)
    {
        long snapshot;
        int count, hresult;
        int status = allEndpoints ? NativeOpenAll(out snapshot, out count, out hresult) : NativeOpen(out snapshot, out count, out hresult);
        if (status != 0)
            Marshal.ThrowExceptionForHR(hresult < 0 ? hresult : EFail);

        try
        {
            var snapshots = new List<MediaAudioSessionSnapshot>(count);
            for (int i = 0; i < count; i++)
            {
                int index = i;
                if (NativeGetInfo(snapshot, index, out uint processId, out byte isSystemSounds) != 0) continue;
                if (NativeGetState(snapshot, index, out int state) != 0) continue;
                snapshots.Add(new(processId, ReadString(snapshot, index, 0), ReadString(snapshot, index, 1),
                    ReadString(snapshot, index, 2), isSystemSounds != 0,
                    () => NativeReadPeak(snapshot, index, out float peak) == 0 ? peak : 0f,
                    () => OpenVolume(snapshot, index), state));
            }

            return new MediaAudioSessions(snapshots, () => NativeClose(snapshot));
        }
        catch
        {
            NativeClose(snapshot);
            throw;
        }
    }

    internal static string ReadString(long snapshot, int index, int kind)
    {
        char* stack = stackalloc char[512];
        int status = NativeGetString(snapshot, index, kind, stack, 512, out int length);
        if (status == 0) return new string(stack, 0, length);
        if (status != NativeBufferTooSmall || length <= 0 || length > 1 << 16) return string.Empty;
        char[] heap = new char[length];
        fixed (char* pinned = heap)
            return NativeGetString(snapshot, index, kind, pinned, length, out int written) == 0 ? new string(pinned, 0, written) : string.Empty;
    }

    private static IMediaAudioVolume OpenVolume(long snapshot, int index)
    {
        if (NativeOpenVolume(snapshot, index, out long volume) != 0) throw new InvalidOperationException("The audio session has no volume control.");
        return new VolumeLease(volume);
    }

    private sealed class VolumeLease(long handle) : IMediaAudioVolume
    {
        public bool IsActive => NativeVolumeGetState(handle, out int state) == 0 && state == 1;

        public float Volume
        {
            get
            {
                if (NativeVolumeGet(handle, out float scalar, out _, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
                return scalar;
            }
            set
            {
                if (NativeVolumeSet(handle, value, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }

        public bool Mute
        {
            get
            {
                if (NativeVolumeGet(handle, out _, out byte muted, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
                return muted != 0;
            }
            set
            {
                if (NativeVolumeSetMute(handle, value ? (byte)1 : (byte)0, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }

        public void Dispose() => NativeVolumeClose(handle);
    }
}
