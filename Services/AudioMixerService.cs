using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

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

public sealed partial class AudioMixerService : IDisposable
{
    private sealed class CachedSessionVolume : IDisposable
    {
        public MMDeviceEnumerator Enumerator { get; }
        public MMDevice Device { get; }
        public AudioSessionControl Session { get; }
        public SimpleAudioVolume SimpleVolume { get; }
        public uint ProcessId { get; }
        public DateTime CachedAtUtc { get; set; }

        public CachedSessionVolume(MMDeviceEnumerator enumerator, MMDevice device, AudioSessionControl session, SimpleAudioVolume sv, uint processId)
        {
            Enumerator = enumerator;
            Device = device;
            Session = session;
            SimpleVolume = sv;
            ProcessId = processId;
            CachedAtUtc = DateTime.UtcNow;
        }

        public void Dispose()
        {
            try { SimpleVolume.Dispose(); } catch { }
            try { Session.Dispose(); } catch { }
            try { Device.Dispose(); } catch { }
            try { Enumerator.Dispose(); } catch { }
        }
    }

    private CachedSessionVolume? _setCache;
    private readonly object _setCacheLock = new();
    private const double SetCacheLifetimeMs = 3000;

    private MMDeviceEnumerator? _enumerator;
    private readonly object _enumLock = new();

    private readonly Dictionary<uint, CachedProcessMetadata> _iconCache = new();
    private const int MaxIconCacheSize = 48;

    private sealed class CachedProcessMetadata
    {
        public required string Name { get; init; }
        public ImageSource? Icon { get; set; }
        public bool IconResolved { get; set; }
        public string? ProcessName { get; init; }
        public DateTime? ProcessStartTimeUtc { get; init; }
        public string? ExePath { get; init; }
        public DateTime LastAccessedUtc { get; set; }
        public long LastValidatedTicks { get; set; } = Environment.TickCount64;
    }

    private void SetCacheEntry(uint pid, CachedProcessMetadata entry)
    {
        if (_iconCache.Count >= MaxIconCacheSize && !_iconCache.ContainsKey(pid))
        {
            uint oldestKey = 0;
            DateTime oldestTime = DateTime.MaxValue;
            foreach (var kvp in _iconCache)
            {
                if (kvp.Value.LastAccessedUtc < oldestTime)
                {
                    oldestTime = kvp.Value.LastAccessedUtc;
                    oldestKey = kvp.Key;
                }
            }
            if (oldestKey != 0)
            {
                _iconCache.Remove(oldestKey);
            }
        }
        _iconCache[pid] = entry;
    }

    private void PruneCacheExcept(HashSet<uint> activePids)
    {
        if (_iconCache.Count == 0) return;
        var toRemove = new List<uint>();
        foreach (var pid in _iconCache.Keys)
        {
            if (!activePids.Contains(pid))
            {
                toRemove.Add(pid);
            }
        }
        foreach (var pid in toRemove)
        {
            _iconCache.Remove(pid);
        }
    }

    private static bool IsCachedProcessValid(uint pid, CachedProcessMetadata cached)
    {
        long now = Environment.TickCount64;
        if (now - cached.LastValidatedTicks < 3000) return true;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            if (proc.HasExited) return false;

            if (cached.ProcessStartTimeUtc.HasValue)
            {
                try
                {
                    if (Math.Abs((proc.StartTime.ToUniversalTime() - cached.ProcessStartTimeUtc.Value).TotalSeconds) > 1.0)
                        return false;
                }
                catch
                {
                    if (!string.IsNullOrWhiteSpace(cached.ProcessName) &&
                        !string.Equals(proc.ProcessName, cached.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(cached.ProcessName))
            {
                if (!string.Equals(proc.ProcessName, cached.ProcessName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            cached.LastValidatedTicks = now;
            return true;
        }
        catch
        {
            var resolved = ProcessNameResolver.TryGetNameAndExecutable(pid);
            if (string.IsNullOrEmpty(resolved.Name) && string.IsNullOrEmpty(resolved.Executable))
                return false;
            if (!string.IsNullOrWhiteSpace(cached.ProcessName) &&
                !string.Equals(resolved.Name, cached.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            cached.LastValidatedTicks = now;
            return true;
        }
    }

    private MMDeviceEnumerator GetEnumeratorLocked() => _enumerator ??= new MMDeviceEnumerator();

    private void DisposeEnumerator()
    {
        if (_enumerator != null)
        {
            try { _enumerator.Dispose(); }
            catch (Exception) { /* Best-effort enumerator cleanup */ }
            _enumerator = null;
        }
    }

    public List<AudioSessionInfo> GetSessions() => GetSessions(includeIcons: true);

#pragma warning disable S3776 // Enumerates active CoreAudio audio sessions with icon cache and volume state resolution
    public List<AudioSessionInfo> GetSessions(bool includeIcons)
    {
        var results = new List<AudioSessionInfo>();
        lock (_enumLock)
        {
            try
            {
                var enumerator = GetEnumeratorLocked();
                using var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                var sessions = renderDevice.AudioSessionManager.Sessions;
                if (sessions == null) return results;

                var seenProcessIds = new HashSet<uint>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session == null) continue;

                    try
                    {
                        if (session.State == AudioSessionState.AudioSessionStateExpired)
                            continue;

                        bool isSystem = session.IsSystemSoundsSession;
                        uint pid = session.GetProcessID;

                        uint dedupeKey = isSystem ? 0u : pid;
                        if (!seenProcessIds.Add(dedupeKey))
                            continue;

                        float volume;
                        bool muted;
                        using (var simpleVolume = session.SimpleAudioVolume)
                        {
                            volume = Math.Clamp(simpleVolume.Volume, 0f, 1f);
                            muted = simpleVolume.Mute;
                        }

                        string name;
                        ImageSource? icon = null;

                        if (isSystem)
                        {
                            name = "System Sounds";
                        }
                        else
                        {
                            if (!_iconCache.TryGetValue(pid, out var cached) || !IsCachedProcessValid(pid, cached))
                            {
                                // Resolve the final name in the first background snapshot.
                                // Only shell icon extraction is deferred to the detailed pass.
                                ResolveProcess(pid, session, out name, out var procName, out var startTime, out var exePath);
                                cached = new CachedProcessMetadata
                                {
                                    Name = name,
                                    ProcessName = procName,
                                    ProcessStartTimeUtc = startTime,
                                    ExePath = exePath
                                };
                                if (!string.IsNullOrWhiteSpace(name)) SetCacheEntry(pid, cached);
                            }

                            cached.LastAccessedUtc = DateTime.UtcNow;
                            name = cached.Name;
                            if (includeIcons && !cached.IconResolved)
                            {
                                cached.IconResolved = true;
                                if (!string.IsNullOrEmpty(cached.ExePath))
                                {
                                    try { cached.Icon = FileIconProvider.GetAppIcon(cached.ExePath, small: true); }
                                    catch (Exception) { /* Icon extraction failed */ }
                                }
                            }
                            icon = cached.Icon;
                        }

                        if (string.IsNullOrWhiteSpace(name))
                            continue;

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

                PruneCacheExcept(seenProcessIds);
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("AUDIOMIXER", $"GetSessions error: {ex.Message}");
                DisposeEnumerator();
            }
        }

        return results
            .OrderByDescending(s => s.IsSystemSounds)
            .ThenBy(s => s.ProcessId)
            .ToList();
    }
#pragma warning restore S3776

#pragma warning disable S3776 // Resolves process metadata, description, and icon from executable file info
    private static void ResolveProcess(
        uint pid,
        AudioSessionControl session,
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
                var display = session.DisplayName;
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
        float target = Math.Clamp(volume, 0f, 1f);

        lock (_setCacheLock)
        {
            if (_setCache != null && _setCache.ProcessId == processId &&
                (DateTime.UtcNow - _setCache.CachedAtUtc).TotalMilliseconds < SetCacheLifetimeMs)
            {
                try
                {
                    _setCache.SimpleVolume.Volume = target;
                    if (target > 0.001f && _setCache.SimpleVolume.Mute) _setCache.SimpleVolume.Mute = false;
                    return true;
                }
                catch (Exception)
                {
                    InvalidateSetCacheLocked();
                }
            }

            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                if (sessions != null)
                {
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session == null) continue;
                        bool keepSession = false;
                        try
                        {
                            bool match = processId == 0
                                ? session.IsSystemSoundsSession
                                : (!session.IsSystemSoundsSession && session.GetProcessID == processId);
                            if (!match) continue;

                            var sv = session.SimpleAudioVolume;
                            try
                            {
                                sv.Volume = target;
                                if (target > 0.001f && sv.Mute) sv.Mute = false;
                            }
                            catch
                            {
                                sv.Dispose();
                                throw;
                            }

                            InvalidateSetCacheLocked();
                            _setCache = new CachedSessionVolume(enumerator!, device!, session, sv, processId);
                            enumerator = null;
                            device = null;
                            // Keep and dispose the session together with its volume interface.
                            keepSession = true;
                            return true;
                        }
                        catch (Exception)
                        {
                            // Session volume interface disconnected
                        }
                        finally
                        {
                            if (!keepSession) session.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("AUDIOMIXER-SETVOL", ex.Message);
            }
            finally
            {
                device?.Dispose();
                enumerator?.Dispose();
            }

            return false;
        }
    }

    public bool ToggleSessionMute(uint processId)
    {
        InvalidateSetCache();
        return ResolveSimpleVolume(processId, sv =>
        {
            sv.Mute = !sv.Mute;
            return sv.Mute;
        });
    }

    private static bool ResolveSimpleVolume(uint processId, Func<SimpleAudioVolume, bool> action)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            if (sessions == null) return false;

            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session == null) continue;
                try
                {
                    bool match = processId == 0
                        ? session.IsSystemSoundsSession
                        : (!session.IsSystemSoundsSession && session.GetProcessID == processId);
                    if (!match) continue;

                    using var sv = session.SimpleAudioVolume;
                    return action(sv);
                }
                catch (Exception)
                {
                    // Session volume interface disconnected
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-RESOLVE", ex.Message);
        }
        return false;
    }

    private void InvalidateSetCache()
    {
        lock (_setCacheLock)
        {
            InvalidateSetCacheLocked();
        }
    }

    private void InvalidateSetCacheLocked()
    {
        if (_setCache != null)
        {
            try { _setCache.Dispose(); }
            catch (Exception) { /* Cached volume object already released */ }
            _setCache = null;
        }
    }

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        lock (_enumLock)
        {
            try
            {
                var enumerator = GetEnumeratorLocked();
                string defaultId = "";
                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def.ID;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("AUDIOMIXER", $"No default render endpoint: {ex.Message}");
                }

                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        devices.Add(new AudioDeviceInfo
                        {
                            Id = device.ID,
                            FriendlyName = device.FriendlyName,
                            IsDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("AUDIOMIXER-DEVICES", ex.Message);
                DisposeEnumerator();
            }
        }
        return devices;
    }

    public bool SetDefaultOutputDevice(string deviceId)
    {
        if (!SetDefaultEndpointInternal(deviceId))
            return false;

        InvalidateSetCache();
        lock (_enumLock)
        {
            DisposeEnumerator();
        }
        return true;
    }

    public float GetCaptureVolume()
    {
        try
        {
            var ev = GetCaptureEndpointVolume();
            return ev != null ? Math.Clamp(ev.MasterVolumeLevelScalar, 0f, 1f) : 0.5f;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-CAPVOL", ex.Message);
            return 0.5f;
        }
    }

    public bool SetCaptureVolume(float volume)
    {
        try
        {
            var ev = GetCaptureEndpointVolume();
            if (ev == null) return false;
            ev.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-SETCAPVOL", ex.Message);
            InvalidateCaptureCache();
            return false;
        }
    }

    private MMDeviceEnumerator? _captureEnumerator;
    private MMDevice? _captureDevice;
    private DateTime _captureCacheAtUtc = DateTime.MinValue;
    private readonly object _captureCacheLock = new();

    private AudioEndpointVolume? GetCaptureEndpointVolume()
    {
        lock (_captureCacheLock)
        {
            if (_captureDevice != null && (DateTime.UtcNow - _captureCacheAtUtc).TotalMilliseconds < SetCacheLifetimeMs)
                return _captureDevice.AudioEndpointVolume;

            InvalidateCaptureCacheLocked();
            try
            {
                _captureEnumerator = new MMDeviceEnumerator();
                _captureDevice = _captureEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                _captureCacheAtUtc = DateTime.UtcNow;
                return _captureDevice.AudioEndpointVolume;
            }
            catch (Exception)
            {
                InvalidateCaptureCacheLocked();
                return null;
            }
        }
    }

    private void InvalidateCaptureCache()
    {
        lock (_captureCacheLock)
        {
            InvalidateCaptureCacheLocked();
        }
    }

    private void InvalidateCaptureCacheLocked()
    {
        if (_captureDevice != null)
        {
            try { _captureDevice.Dispose(); }
            catch (Exception) { /* Capture device already disposed */ }
            _captureDevice = null;
        }
        if (_captureEnumerator != null)
        {
            try { _captureEnumerator.Dispose(); }
            catch (Exception) { /* Capture enumerator already disposed */ }
            _captureEnumerator = null;
        }
    }

    public List<AudioDeviceInfo> GetInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        lock (_enumLock)
        {
            try
            {
                var enumerator = GetEnumeratorLocked();
                string defaultId = "";
                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    defaultId = def.ID;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("AUDIOMIXER", $"No default capture endpoint: {ex.Message}");
                }

                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    try
                    {
                        devices.Add(new AudioDeviceInfo
                        {
                            Id = device.ID,
                            FriendlyName = device.FriendlyName,
                            IsDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("AUDIOMIXER-INDEVICES", ex.Message);
                DisposeEnumerator();
            }
        }
        return devices;
    }

    public bool SetDefaultInputDevice(string deviceId)
    {
        if (!SetDefaultEndpointInternal(deviceId))
            return false;

        InvalidateCaptureCache();
        lock (_enumLock)
        {
            DisposeEnumerator();
        }
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
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string id = device.ID;
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

    private void ReleaseSessions()
    {
        InvalidateSetCache();
        InvalidateCaptureCache();
    }

    public void Dispose()
    {
        ReleaseSessions();
        lock (_enumLock)
        {
            DisposeEnumerator();
            _iconCache.Clear();
        }
    }

    public void ReleaseSessionCache()
    {
        ReleaseSessions();
        lock (_enumLock)
        {
            DisposeEnumerator();
        }
    }


}
