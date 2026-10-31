#pragma warning disable S2325 // Public service methods are invoked on the injected AudioMixerService instance
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

internal readonly record struct CachedSessionMetadata(string DisplayName, string? ExePath, ImageSource? Icon);

public sealed partial class AudioMixerService : IDisposable
{
    // Endpoint and session access lives in Native/Services/AudioDevices.cpp and AudioSessions.cpp.
    private const string NativeAudioDll = "VNotch.Native.dll";
    private const int RenderFlow = 0;
    private const int CaptureFlow = 1;
    private const int NativeBufferTooSmall = -2;
    private const int AudioSessionActive = 1;
    private const int AudioSessionExpired = 2;
    private const int MaxMetadataCacheSize = 128;

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_session_get_state(long snapshot, int index, out int state);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_set_session_volume(uint processId, float scalar, out int hresult);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_toggle_session_mute(uint processId, out byte muted);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_invalidate_session_cache();

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_devices_open(int flow, out long list, out int count, out int hresult);

    [LibraryImport(NativeAudioDll)]
    private static partial int vn_audio_devices_close(long list);

    [LibraryImport(NativeAudioDll)]
    private static unsafe partial int vn_audio_device_get_string(
        long list, int index, int kind, char* buffer, int capacity, out int length, out byte isDefault);

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
        Func<string, uint, ImageSource?>? iconResolver = null)
    {
        string normalizedRawId = rawId ?? string.Empty;
        var key = (pid, normalizedRawId);

        lock (_metadataCacheLock)
        {
            if (_metadataCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var resolver = nameResolver ?? ResolveDisplayName;
            var (displayName, exePath) = resolver(pid, normalizedRawId);

            ImageSource? icon = null;
            if (iconResolver != null)
            {
                try { icon = iconResolver(exePath ?? string.Empty, pid); }
                catch (Exception) { /* Icon resolver failed */ }
            }
            else if (!string.IsNullOrEmpty(exePath))
            {
                try { icon = FileIconProvider.GetAppIcon(exePath, small: true); }
                catch (Exception) { /* Icon extraction failed */ }
            }

            // shortcut: cap metadata cache at 128 entries, upgrade to LRU if concurrent session churn exceeds 128
            if (_metadataCache.Count >= MaxMetadataCacheSize)
            {
                foreach (var existingKey in _metadataCache.Keys)
                {
                    _metadataCache.Remove(existingKey);
                    break;
                }
            }

            var resolved = new CachedSessionMetadata(displayName, exePath, icon);
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

#pragma warning disable S3776 // Enumerates active CoreAudio audio sessions with metadata cache and volume state resolution
    internal List<AudioSessionInfo> ReadSessions(bool includeIcons = true)
    {
        var results = new List<AudioSessionInfo>();
        lock (_enumLock)
        {
            if (WindowsMediaAudioSessions.NativeOpen(out long snapshot, out int count, out int hresult) != 0)
            {
                RuntimeLog.Log("AUDIOMIXER", $"GetSessions error: 0x{hresult:X8}");
                return results;
            }

            try
            {
                var seenProcessIds = new HashSet<uint>();
                var activePids = new List<uint>();
                var indexAndStateByPid = new Dictionary<uint, (int ResultIndex, int State)>();
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        if (vn_audio_session_get_state(snapshot, i, out int state) == 0 && state == AudioSessionExpired)
                            continue;
                        if (WindowsMediaAudioSessions.NativeGetInfo(snapshot, i, out uint pid, out byte isSystemSounds) != 0)
                            continue;

                        bool isSystem = isSystemSounds != 0;
                        uint dedupeKey = isSystem ? 0u : pid;
                        if (!seenProcessIds.Add(dedupeKey))
                        {
                            if (state == AudioSessionActive &&
                                indexAndStateByPid.TryGetValue(dedupeKey, out var existing) &&
                                existing.State != AudioSessionActive &&
                                WindowsMediaAudioSessions.NativeOpenVolume(snapshot, i, out long activeLease) == 0)
                            {
                                try
                                {
                                    if (WindowsMediaAudioSessions.NativeVolumeGet(activeLease, out float activeScalar, out byte activeMuteFlag, out _) == 0)
                                    {
                                        results[existing.ResultIndex].Volume = Math.Clamp(activeScalar, 0f, 1f);
                                        results[existing.ResultIndex].IsMuted = activeMuteFlag != 0;
                                        indexAndStateByPid[dedupeKey] = (existing.ResultIndex, state);
                                    }
                                }
                                finally
                                {
                                    WindowsMediaAudioSessions.NativeVolumeClose(activeLease);
                                }
                            }
                            continue;
                        }

                        if (!isSystem)
                        {
                            activePids.Add(pid);
                        }

                        if (WindowsMediaAudioSessions.NativeOpenVolume(snapshot, i, out long lease) != 0)
                            continue;
                        float volume;
                        bool muted;
                        try
                        {
                            if (WindowsMediaAudioSessions.NativeVolumeGet(lease, out float scalar, out byte muteFlag, out _) != 0)
                                continue;
                            volume = Math.Clamp(scalar, 0f, 1f);
                            muted = muteFlag != 0;
                        }
                        finally
                        {
                            WindowsMediaAudioSessions.NativeVolumeClose(lease);
                        }

                        string name;
                        ImageSource? icon = null;

                        if (isSystem)
                        {
                            name = "System Sounds";
                        }
                        else
                        {
                            string rawId = WindowsMediaAudioSessions.ReadString(snapshot, i, 0);
                            var metadata = GetOrResolveSessionMetadata(pid, rawId);
                            name = metadata.DisplayName;
                            icon = includeIcons ? metadata.Icon : null;
                        }

                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        indexAndStateByPid[dedupeKey] = (results.Count, state);
                        results.Add(new AudioSessionInfo
                        {
                            ProcessId = dedupeKey,
                            DisplayName = name,
                            Volume = volume,
                            IsMuted = muted,
                            IsSystemSounds = isSystem,
                            Icon = icon
                        });
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Log("AUDIOMIXER-SESSION", ex.Message);
                    }
                }

                PruneMetadataCache(CollectionsMarshal.AsSpan(activePids));
            }
            finally
            {
                WindowsMediaAudioSessions.NativeClose(snapshot);
            }
        }

        return results
            .OrderByDescending(s => s.IsSystemSounds)
            .ThenBy(s => s.ProcessId)
            .ToList();
    }
#pragma warning restore S3776

    private static (string DisplayName, string? ExePath) ResolveDisplayName(uint pid, string rawIdentifier)
    {
        ResolveProcess(pid, rawIdentifier, out string name, out _, out _, out string? exePath);
        return (name, exePath);
    }

#pragma warning disable S3776 // Resolves process metadata, description, and icon from executable file info
    private static void ResolveProcess(
        uint pid,
        string? sessionDisplayName,
        out string name,
        out string? processName,
        out DateTime? startTimeUtc,
        out string? resolvedExePath)
    {
        name = "";
        processName = null;
        startTimeUtc = null;
        resolvedExePath = null;

        var fastResolved = ProcessNameResolver.TryGetNameAndExecutable(pid);
        processName = fastResolved.Name;
        string? exePath = fastResolved.Executable;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (string.IsNullOrEmpty(processName))
                processName = process.ProcessName;
            try { startTimeUtc = process.StartTime.ToUniversalTime(); } catch { /* Inaccessible for some system processes */ }
            if (string.IsNullOrEmpty(exePath))
            {
                try { exePath = process.MainModule?.FileName; }
                catch (Exception) { /* System/protected process main module inaccessible */ }
            }
        }
        catch (Exception)
        {
            // Limited-information lookup already captured available metadata.
        }
        resolvedExePath = exePath;

        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    name = info.FileDescription.Trim();
            }
            catch (Exception)
            {
                // Executable file info could not be read
            }
        }

        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(processName))
            name = processName;

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var display = sessionDisplayName;
                if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@'))
                    name = display;
            }
            catch (Exception)
            {
                // Session display name query failed
            }
        }

    }
#pragma warning restore S3776

    public bool SetSessionVolume(uint processId, float volume)
    {
        if (vn_audio_set_session_volume(processId, volume, out int hresult) == 0) return true;
        RuntimeLog.Log("AUDIOMIXER-SETVOL", $"0x{hresult:X8}");
        return false;
    }

    public bool ToggleSessionMute(uint processId) =>
        vn_audio_toggle_session_mute(processId, out byte muted) == 0 && muted != 0;

    private static void InvalidateSetCache() => vn_audio_invalidate_session_cache();

    private static unsafe List<AudioDeviceInfo> ListDevices(int flow, string logTag)
    {
        var devices = new List<AudioDeviceInfo>();
        if (vn_audio_devices_open(flow, out long list, out int count, out int hresult) != 0)
        {
            RuntimeLog.Log(logTag, $"0x{hresult:X8}");
            return devices;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                string id = ReadDeviceString(list, i, 0, out byte isDefault);
                string name = ReadDeviceString(list, i, 1, out _);
                devices.Add(new AudioDeviceInfo { Id = id, FriendlyName = name, IsDefault = isDefault != 0 });
            }
        }
        finally
        {
            vn_audio_devices_close(list);
        }

        return devices;
    }

    private static unsafe string ReadDeviceString(long list, int index, int kind, out byte isDefault)
    {
        char* stack = stackalloc char[256];
        int status = vn_audio_device_get_string(list, index, kind, stack, 256, out int length, out isDefault);
        if (status == 0) return new string(stack, 0, length);
        if (status != NativeBufferTooSmall || length <= 0 || length > 1 << 15) return string.Empty;
        char[] heap = new char[length];
        fixed (char* pinned = heap)
            return vn_audio_device_get_string(list, index, kind, pinned, length, out int written, out isDefault) == 0
                ? new string(pinned, 0, written)
                : string.Empty;
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
