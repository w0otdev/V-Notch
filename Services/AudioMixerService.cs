#pragma warning disable S2325 // Public service methods are invoked on the injected AudioMixerService instance
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace VNotch.Services;

public sealed class AudioSessionInfo
{
    public uint ProcessId { get; init; }
    public string DisplayName { get; init; } = "";
    public float Volume { get; set; }
    public bool IsMuted { get; set; }
    public bool IsSystemSounds { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed class AudioDeviceInfo
{
    public string Id { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public bool IsDefault { get; set; }
}

public enum SpatialAudioMode
{
    Off,
    WindowsSonic,
    DolbyAtmos
}

internal readonly record struct CachedSessionMetadata(string DisplayName, string? ExePath, ImageSource? Icon, bool IconResolved = false);

public sealed partial class AudioMixerService : IDisposable
{
    // Endpoint and session access lives in Native/Services/AudioDevices.cpp and AudioSessions.cpp.
    private const string NativeAudioDll = "VNotch.Native.dll";
    private const int RenderFlow = 0;
    private const int CaptureFlow = 1;
    private const int NativeBufferTooSmall = -2;
    private const int MaxMetadataCacheSize = 128;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnAudioSessionState
    {
        public uint ProcessId;
        public float Volume;
        public byte Muted, SystemSounds;
        public int NameOffset, NameLength, PathOffset, PathLength, RawOffset, RawLength;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnAudioDeviceState
    {
        public int IdOffset, IdLength, NameOffset, NameLength;
        public byte IsDefault;
    }
    [LibraryImport(NativeAudioDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_get_sessions_batch(Span<VnAudioSessionState> entries, int capacity,
        Span<char> text, int textCapacity, out int count, out int textLength, out int hresult);
    [LibraryImport(NativeAudioDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_enumerate_devices_batch(int flow, Span<VnAudioDeviceState> entries, int capacity,
        Span<char> text, int textCapacity, out int count, out int textLength, out int hresult);
    private VnAudioSessionState[] _sessionBuffer = new VnAudioSessionState[64];
    private char[] _sessionTextBuffer = new char[16384];

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_set_session_volume(uint processId, float scalar, out int hresult);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_toggle_session_mute(uint processId, out byte muted);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_invalidate_session_cache();

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_capture_volume_get(out float scalar);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_capture_volume_set(float scalar);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_capture_invalidate();

    private readonly object _enumLock = new();
    private readonly Dictionary<(uint Pid, string RawIdentifier), CachedSessionMetadata> _metadataCache = new();
    private readonly object _metadataCacheLock = new();

    internal int MetadataCacheCount
    {
        get
        {
            lock (_metadataCacheLock)
            {
                return _metadataCache.Count;
            }
        }
    }

    internal CachedSessionMetadata GetOrResolveSessionMetadata(
        uint pid,
        string rawId,
        Func<uint, string, (string DisplayName, string? ExePath)>? nameResolver = null,
        Func<string, uint, ImageSource?>? iconResolver = null,
        bool includeIcons = true,
        CachedSessionMetadata? nativeMetadata = null)
    {
        string normalizedRawId = rawId ?? string.Empty;
        var key = (pid, normalizedRawId);

        lock (_metadataCacheLock)
        {
            bool found = _metadataCache.TryGetValue(key, out var cached);
            if (found && (!includeIcons || cached.IconResolved))
            {
                return cached;
            }

            var resolver = nameResolver ?? ResolveDisplayName;
            var (displayName, exePath) = found ? (cached.DisplayName, cached.ExePath) :
                nativeMetadata is { } native ? (native.DisplayName, native.ExePath) : resolver(pid, normalizedRawId);

            ImageSource? icon = null;
            if (includeIcons && iconResolver != null)
            {
                try { icon = iconResolver(exePath ?? string.Empty, pid); }
                catch (Exception) { /* Icon resolver failed */ }
            }
            else if (includeIcons && !string.IsNullOrEmpty(exePath))
            {
                try { icon = FileIconProvider.GetAppIcon(exePath, small: true); }
                catch (Exception) { /* Icon extraction failed */ }
            }

            // shortcut: cap metadata cache at 128 entries, upgrade to LRU if concurrent session churn exceeds 128
            if (!found && _metadataCache.Count >= MaxMetadataCacheSize)
            {
                foreach (var existingKey in _metadataCache.Keys)
                {
                    _metadataCache.Remove(existingKey);
                    break;
                }
            }

            var resolved = new CachedSessionMetadata(displayName, exePath, icon, includeIcons);
            _metadataCache[key] = resolved;
            return resolved;
        }
    }

    internal void PruneMetadataCache(ReadOnlySpan<uint> activePids)
    {
        lock (_metadataCacheLock)
        {
            if (_metadataCache.Count == 0) return;
            if (activePids.IsEmpty)
            {
                _metadataCache.Clear();
                return;
            }

            var toRemove = new List<(uint Pid, string RawIdentifier)>();
            foreach (var key in _metadataCache.Keys)
            {
                if (!ContainsPid(activePids, key.Pid))
                {
                    toRemove.Add(key);
                }
            }

            foreach (var key in toRemove)
            {
                _metadataCache.Remove(key);
            }

            while (_metadataCache.Count > MaxMetadataCacheSize)
            {
                foreach (var existingKey in _metadataCache.Keys)
                {
                    _metadataCache.Remove(existingKey);
                    break;
                }
            }
        }
    }

    private static bool ContainsPid(ReadOnlySpan<uint> pids, uint target)
    {
        for (int i = 0; i < pids.Length; i++)
        {
            if (pids[i] == target) return true;
        }
        return false;
    }

    public List<AudioSessionInfo> GetSessions() => GetSessions(includeIcons: true);

    public List<AudioSessionInfo> GetSessions(bool includeIcons) => ReadSessions(includeIcons);

    internal List<AudioSessionInfo> ResolveSessionIcons(IReadOnlyList<AudioSessionInfo> sessions)
    {
        var result = new List<AudioSessionInfo>(sessions.Count);
        foreach (var session in sessions)
        {
            string? rawId = null;
            lock (_metadataCacheLock)
                foreach (var entry in _metadataCache)
                    if (entry.Key.Pid == session.ProcessId && entry.Value.DisplayName == session.DisplayName)
                    { rawId = entry.Key.RawIdentifier; break; }
            var icon = rawId == null ? session.Icon : GetOrResolveSessionMetadata(session.ProcessId, rawId).Icon;
            result.Add(new AudioSessionInfo
            {
                ProcessId = session.ProcessId,
                DisplayName = session.DisplayName,
                Volume = session.Volume,
                IsMuted = session.IsMuted,
                IsSystemSounds = session.IsSystemSounds,
                Icon = icon
            });
        }
        return result;
    }

    internal List<AudioSessionInfo> ReadSessions(bool includeIcons = true)
    {
        var results = new List<AudioSessionInfo>();
        lock (_enumLock)
        {
            int status = vn_audio_get_sessions_batch(_sessionBuffer, _sessionBuffer.Length,
                _sessionTextBuffer, _sessionTextBuffer.Length, out int count, out int chars, out int hresult);
            for (int retry = 0; status == NativeBufferTooSmall && retry < 3; retry++)
            {
                if (count > _sessionBuffer.Length) Array.Resize(ref _sessionBuffer, count);
                if (chars > _sessionTextBuffer.Length) Array.Resize(ref _sessionTextBuffer, chars);
                status = vn_audio_get_sessions_batch(_sessionBuffer, _sessionBuffer.Length,
                    _sessionTextBuffer, _sessionTextBuffer.Length, out count, out chars, out hresult);
            }
            if (status != 0)
            {
                RuntimeLog.Log("AUDIOMIXER", $"GetSessions error: 0x{hresult:X8}");
                return results;
            }
            Span<uint> activePids = count <= 256 ? stackalloc uint[count] : new uint[count];
            for (int i = 0; i < count; i++)
            {
                ref readonly var entry = ref _sessionBuffer[i];
                activePids[i] = entry.ProcessId;
                string name = new(_sessionTextBuffer, entry.NameOffset, entry.NameLength);
                ImageSource? icon = null;
                if (entry.SystemSounds == 0)
                {
                    string raw = new(_sessionTextBuffer, entry.RawOffset, entry.RawLength);
                    string path = new(_sessionTextBuffer, entry.PathOffset, entry.PathLength);
                    var metadata = GetOrResolveSessionMetadata(entry.ProcessId, raw, includeIcons: includeIcons,
                        nativeMetadata: new CachedSessionMetadata(name, path, null));
                    name = metadata.DisplayName;
                    icon = includeIcons ? metadata.Icon : null;
                }
                results.Add(new AudioSessionInfo
                {
                    ProcessId = entry.ProcessId,
                    DisplayName = name,
                    Volume = entry.Volume,
                    IsMuted = entry.Muted != 0,
                    IsSystemSounds = entry.SystemSounds != 0,
                    Icon = icon
                });
            }
            PruneMetadataCache(activePids);
        }
        return results;
    }

    [LibraryImport(NativeAudioDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_resolve_session_metadata(uint pid, ReadOnlySpan<char> raw, int rawLength,
        out VnAudioSessionState metadata, Span<char> text, int capacity, out int required);

    private static (string DisplayName, string? ExePath) ResolveDisplayName(uint pid, string rawIdentifier)
    {
        Span<char> text = stackalloc char[2048];
        int status = vn_audio_resolve_session_metadata(pid, rawIdentifier, rawIdentifier.Length, out var metadata, text, text.Length, out int required);
        if (status == NativeBufferTooSmall)
        {
            text = new char[required];
            status = vn_audio_resolve_session_metadata(pid, rawIdentifier, rawIdentifier.Length, out metadata, text, text.Length, out _);
        }
        return status == 0 ? (new string(text.Slice(metadata.NameOffset, metadata.NameLength)),
            metadata.PathLength == 0 ? null : new string(text.Slice(metadata.PathOffset, metadata.PathLength))) : (string.Empty, null);
    }

    public bool SetSessionVolume(uint processId, float volume)
    {
        if (vn_audio_set_session_volume(processId, volume, out int hresult) == 0) return true;
        RuntimeLog.Log("AUDIOMIXER-SETVOL", $"0x{hresult:X8}");
        return false;
    }

    public bool ToggleSessionMute(uint processId) =>
        vn_audio_toggle_session_mute(processId, out byte muted) == 0 && muted != 0;

    private static void InvalidateSetCache() => vn_audio_invalidate_session_cache();

    private static List<AudioDeviceInfo> ListDevices(int flow, string logTag)
    {
        var devices = new List<AudioDeviceInfo>();
        Span<VnAudioDeviceState> entries = stackalloc VnAudioDeviceState[32];
        Span<char> text = stackalloc char[8192];
        int status = vn_audio_enumerate_devices_batch(flow, entries, entries.Length, text, text.Length, out int count, out int chars, out int hresult);
        for (int retry = 0; status == NativeBufferTooSmall && retry < 3; retry++)
        {
            entries = new VnAudioDeviceState[count];
            text = new char[chars];
            status = vn_audio_enumerate_devices_batch(flow, entries, entries.Length, text, text.Length, out count, out chars, out hresult);
        }
        if (status != 0)
        {
            RuntimeLog.Log(logTag, $"0x{hresult:X8}");
            return devices;
        }
        foreach (ref readonly var entry in entries[..count])
            devices.Add(new AudioDeviceInfo
            {
                Id = new string(text.Slice(entry.IdOffset, entry.IdLength)),
                FriendlyName = new string(text.Slice(entry.NameOffset, entry.NameLength)),
                IsDefault = entry.IsDefault != 0
            });
        return devices;
    }

    /// <summary>Active render endpoints without needing a mixer instance (settings pickers).</summary>
    public static List<AudioDeviceInfo> EnumerateOutputDevices() => ListDevices(RenderFlow, "AUDIOMIXER-DEVICES");

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        lock (_enumLock) return ListDevices(RenderFlow, "AUDIOMIXER-DEVICES");
    }

    public bool SetDefaultOutputDevice(string deviceId)
    {
        if (!SetDefaultEndpointInternal(deviceId))
            return false;

        InvalidateSetCache();
        return true;
    }

    public float GetCaptureVolume() =>
        vn_audio_capture_volume_get(out float scalar) == 0 ? Math.Clamp(scalar, 0f, 1f) : 0.5f;

    public bool SetCaptureVolume(float volume)
    {
        if (vn_audio_capture_volume_set(Math.Clamp(volume, 0f, 1f)) == 0) return true;
        vn_audio_capture_invalidate();
        return false;
    }

    public List<AudioDeviceInfo> GetInputDevices()
    {
        lock (_enumLock) return ListDevices(CaptureFlow, "AUDIOMIXER-INDEVICES");
    }

    public bool SetDefaultInputDevice(string deviceId)
    {
        if (!SetDefaultEndpointInternal(deviceId))
            return false;

        vn_audio_capture_invalidate();
        return true;
    }
    private static bool SetDefaultEndpointInternal(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        try
        {
            // The native layer owns the COM call; the per-role HRESULTs come back for logging.
            _ = vn_audio_set_default_endpoint(deviceId.AsSpan(), deviceId.Length,
                out int consoleHr, out int multimediaHr, out int communicationsHr);

            if (multimediaHr < 0)
            {
                RuntimeLog.Warn("AUDIOMIXER-SETDEFAULT",
                    $"SetDefaultEndpoint failed: console=0x{consoleHr:X8}, multimedia=0x{multimediaHr:X8}, communications=0x{communicationsHr:X8}");
                return false;
            }

            if (consoleHr < 0 || communicationsHr < 0)
            {
                RuntimeLog.Warn("AUDIOMIXER-SETDEFAULT",
                    $"Default multimedia endpoint changed, but another role failed: console=0x{consoleHr:X8}, communications=0x{communicationsHr:X8}");
            }

            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-SETDEFAULT", ex.Message);
            return false;
        }
    }

    public static SpatialAudioMode GetSpatialAudioMode()
    {
        try
        {
            string id = ListDevices(RenderFlow, "AUDIOMIXER-SPATIAL-DEVICES").FirstOrDefault(d => d.IsDefault)?.Id ?? string.Empty;
            string guid = ExtractEndpointGuid(id);
            if (string.IsNullOrEmpty(guid)) return SpatialAudioMode.Off;

            string keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\FxProperties";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
            var value = key?.GetValue("{e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04},6") as string;
            if (string.IsNullOrWhiteSpace(value))
                return SpatialAudioMode.Off;

            _ = vn_audio_mixer_classify_spatial_audio_value(value.AsSpan(), value.Length, out int mode);
            return mode switch
            {
                1 => SpatialAudioMode.WindowsSonic,
                2 => SpatialAudioMode.DolbyAtmos,
                _ => SpatialAudioMode.Off
            };
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-SPATIAL-GET", ex.Message);
            return SpatialAudioMode.Off;
        }
    }

    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_mixer_extract_endpoint_guid(
        ReadOnlySpan<char> deviceId,
        int deviceIdLen,
        out int outStart,
        out int outLength,
        out byte outFound);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_mixer_classify_spatial_audio_value(
        ReadOnlySpan<char> value,
        int valueLen,
        out int outMode);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_set_default_endpoint(
        ReadOnlySpan<char> deviceId,
        int deviceIdLen,
        out int outConsoleHr,
        out int outMultimediaHr,
        out int outCommunicationsHr);

    private static string ExtractEndpointGuid(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return "";
        if (vn_audio_mixer_extract_endpoint_guid(deviceId.AsSpan(), deviceId.Length, out int start, out int length, out byte found) == 0 &&
            found != 0 && start >= 0 && start + length <= deviceId.Length)
        {
            return deviceId.Substring(start, length);
        }
        return "";
    }

    private static void ReleaseSessions()
    {
        InvalidateSetCache();
        vn_audio_capture_invalidate();
    }

    public void Dispose()
    {
        ReleaseSessions();
        PruneMetadataCache(ReadOnlySpan<uint>.Empty);
    }

    public void ReleaseSessionCache() => ReleaseSessions();
}
