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

public sealed class AudioMixerService : IDisposable
{
    private SimpleAudioVolume? _setCacheVolume;
    private uint _setCachePid = uint.MaxValue;
    private DateTime _setCacheAtUtc = DateTime.MinValue;
    private const double SetCacheLifetimeMs = 3000;

    private MMDeviceEnumerator? _enumerator;
    private readonly object _enumLock = new();

    private readonly Dictionary<uint, (string Name, ImageSource? Icon)> _iconCache = new();

    private MMDeviceEnumerator GetEnumeratorLocked() => _enumerator ??= new MMDeviceEnumerator();

    private void DisposeEnumerator()
    {
        if (_enumerator != null)
        {
            try { _enumerator.Dispose(); } catch { }
            _enumerator = null;
        }
    }

    public List<AudioSessionInfo> GetSessions() => GetSessions(includeIcons: true);

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
                    var session = sessions[i];
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
                        else if (includeIcons)
                        {
                            if (_iconCache.TryGetValue(pid, out var cached))
                            {
                                name = cached.Name;
                                icon = cached.Icon;
                            }
                            else
                            {
                                ResolveProcess(pid, session, out name, out icon);
                                if (!string.IsNullOrWhiteSpace(name))
                                    _iconCache[pid] = (name, icon);
                            }
                        }
                        else
                        {
                            name = ResolveProcessNameFast(pid, session);
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

    private static string ResolveProcessNameFast(uint pid, AudioSessionControl session)
    {
        try
        {
            string display = session.DisplayName;
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("@"))
                return display;
        }
        catch { }

        // ProcessName does not open the executable or invoke the shell, so it is
        // suitable for the first lightweight mixer snapshot.
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!string.IsNullOrWhiteSpace(process.ProcessName))
                return process.ProcessName;
        }
        catch { }

        return "App";
    }

    private static void ResolveProcess(uint pid, AudioSessionControl session, out string name, out ImageSource? icon)
    {
        name = "";
        icon = null;

        string? exePath = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            try { exePath = process.MainModule?.FileName; } catch { }

            if (!string.IsNullOrEmpty(exePath))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(info.FileDescription))
                        name = info.FileDescription!.Trim();
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(name))
                name = process.ProcessName;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var display = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("@"))
                    name = display;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(exePath))
        {
            try { icon = FileIconProvider.GetFileIcon(exePath); } catch { }
        }
    }

    public bool SetSessionVolume(uint processId, float volume)
    {
        float target = Math.Clamp(volume, 0f, 1f);

        if (_setCacheVolume != null && _setCachePid == processId &&
            (DateTime.UtcNow - _setCacheAtUtc).TotalMilliseconds < SetCacheLifetimeMs)
        {
            try
            {
                _setCacheVolume.Volume = target;
                if (target > 0.001f && _setCacheVolume.Mute) _setCacheVolume.Mute = false;
                return true;
            }
            catch { InvalidateSetCache(); }
        }

        return ResolveSimpleVolume(processId, sv =>
        {
            _setCacheVolume?.Dispose();
            _setCacheVolume = sv;
            _setCachePid = processId;
            _setCacheAtUtc = DateTime.UtcNow;

            sv.Volume = target;
            if (target > 0.001f && sv.Mute) sv.Mute = false;
            return true;
        }, keepAlive: true);
    }

    public bool ToggleSessionMute(uint processId)
    {
        InvalidateSetCache();
        return ResolveSimpleVolume(processId, sv =>
        {
            sv.Mute = !sv.Mute;
            return sv.Mute;
        }, keepAlive: false);
    }

    private bool ResolveSimpleVolume(uint processId, Func<SimpleAudioVolume, bool> action, bool keepAlive)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            if (sessions == null) return false;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null) continue;
                try
                {
                    bool match = processId == 0
                        ? session.IsSystemSoundsSession
                        : (!session.IsSystemSoundsSession && session.GetProcessID == processId);
                    if (!match) continue;

                    var sv = session.SimpleAudioVolume;
                    bool result = action(sv);
                    if (!keepAlive) sv.Dispose();
                    return result;
                }
                catch { }
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
        if (_setCacheVolume != null)
        {
            try { _setCacheVolume.Dispose(); } catch { }
            _setCacheVolume = null;
        }
        _setCachePid = uint.MaxValue;
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

    private MMDevice? _captureDevice;
    private DateTime _captureCacheAtUtc = DateTime.MinValue;

    private AudioEndpointVolume? GetCaptureEndpointVolume()
    {
        if (_captureDevice != null && (DateTime.UtcNow - _captureCacheAtUtc).TotalMilliseconds < SetCacheLifetimeMs)
            return _captureDevice.AudioEndpointVolume;

        InvalidateCaptureCache();
        using var enumerator = new MMDeviceEnumerator();
        _captureDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        _captureCacheAtUtc = DateTime.UtcNow;
        return _captureDevice.AudioEndpointVolume;
    }

    private void InvalidateCaptureCache()
    {
        if (_captureDevice != null)
        {
            try { _captureDevice.Dispose(); } catch { }
            _captureDevice = null;
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

    private bool SetDefaultEndpointInternal(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        IPolicyConfig? policyConfig = null;
        try
        {
            policyConfig = (IPolicyConfig)new PolicyConfigClient();
            int consoleHr = policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
            int multimediaHr = policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            int communicationsHr = policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);

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
        finally
        {
            if (policyConfig != null)
            {
                try { Marshal.ReleaseComObject(policyConfig); } catch { }
            }
        }
    }

    public SpatialAudioMode GetSpatialAudioMode()
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

            if (value.Contains("a44d5fd2", StringComparison.OrdinalIgnoreCase))
                return SpatialAudioMode.WindowsSonic;
            if (value.Contains("265d4a5e", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("dolby", StringComparison.OrdinalIgnoreCase))
                return SpatialAudioMode.DolbyAtmos;

            return SpatialAudioMode.Off;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-SPATIAL-GET", ex.Message);
            return SpatialAudioMode.Off;
        }
    }

    private static string ExtractEndpointGuid(string deviceId)
    {
        int idx = deviceId.LastIndexOf('}');
        int open = deviceId.LastIndexOf('{');
        if (open >= 0 && idx > open)
            return deviceId.Substring(open, idx - open + 1);
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

    public void ReleaseSessionCache() => ReleaseSessions();

    #region IPolicyConfig COM interop

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, bool def, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceId, bool store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceId, bool store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(string deviceId, bool visible);
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    #endregion
}
