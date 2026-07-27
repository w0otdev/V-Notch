using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VNotch.Services;

public sealed class PrivacyIndicatorService : IDisposable
{
    private const string ConsentRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MicrophoneFlowPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MinimumScreenRecordingDuration = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MicrophoneSignalHoldDuration = TimeSpan.FromMilliseconds(1400);
    internal const float MicrophoneSignalThreshold = 0.0125f;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly string[] IgnoredMicrophoneProcessSuffixes =
    {
        "service", "services", "svc", "daemon"
    };

    private static readonly HashSet<string> IgnoredMicrophoneProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "audiodg", "svchost", "system", "registry", "elgato.wavelink"
        };

    private static readonly Lazy<IReadOnlySet<string>> ServiceExecutablePaths =
        new(LoadServiceExecutablePaths);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint packageFamilyNameLength, IntPtr packageFamilyName);

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _microphoneFlowTimer;
    private readonly TimeSpan _activeInterval;
    private readonly MicrophoneActivityGate _microphoneActivityGate = new(
        MicrophoneSignalThreshold,
        MicrophoneSignalHoldDuration);
    private readonly MicrophoneFlowProbe _microphoneFlowProbe = new();
    private IReadOnlyList<CapabilityUsage> _microphoneCandidates = Array.Empty<CapabilityUsage>();
    private IReadOnlyList<string> _microphoneCandidateNames = Array.Empty<string>();
    private IReadOnlyList<string> _cameraConsumers = Array.Empty<string>();
    private bool _cameraInUse;
    private bool _screenRecordingActive;
    private bool _disposed;
    private bool _started;

    public event EventHandler<PrivacyIndicatorState>? StateChanged;

    public PrivacyIndicatorState CurrentState { get; private set; } = PrivacyIndicatorState.Empty;

    public PrivacyIndicatorService(TimeSpan? pollInterval = null)
    {
        _activeInterval = pollInterval ?? ActivePollInterval;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = _activeInterval
        };
        _timer.Tick += (_, _) => Poll();

        _microphoneFlowTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = MicrophoneFlowPollInterval
        };
        _microphoneFlowTimer.Tick += (_, _) => PollMicrophoneFlow();
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        Poll();
        _timer.Start();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _timer.Stop();
        _microphoneFlowTimer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _microphoneFlowProbe.Dispose();
    }

    private void Poll()
    {
        try
        {
            DateTime utcNow = DateTime.UtcNow;
            var micUsage = ScanCapability("microphone");
            var camUsage = ScanCapability("webcam");
            var programmaticCapture = ScanCapability("graphicsCaptureProgrammatic");
            var borderlessCapture = ScanCapability("graphicsCaptureWithoutBorder");

            var running = new ConsumerProcessProbe();
            _microphoneCandidates = GetRelevantConsumerUsages(
                micUsage,
                running,
                usage => !IsIgnoredMicrophoneConsumer(usage.RawName));
            _microphoneCandidateNames = GetConsumerNames(_microphoneCandidates);

            var cam = GetRelevantConsumerUsages(camUsage, running);
            _cameraConsumers = GetConsumerNames(cam);
            _cameraInUse = _cameraConsumers.Count > 0;
            _screenRecordingActive = DetectScreenRecording(
                programmaticCapture.Concat(borderlessCapture), running, utcNow);

            if (_microphoneCandidates.Count > 0)
            {
                if (!_microphoneFlowTimer.IsEnabled)
                    _microphoneFlowTimer.Start();
            }
            else
            {
                _microphoneFlowTimer.Stop();
            }

            PollMicrophoneFlow(utcNow);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, "PrivacyIndicatorService poll failed");
        }
        finally
        {
            AdaptInterval();
        }
    }

    private void PollMicrophoneFlow() => PollMicrophoneFlow(DateTime.UtcNow);

    private void PollMicrophoneFlow(DateTime utcNow)
    {
        try
        {
            MicrophoneFlowEvidence evidence = _microphoneCandidates.Count > 0
                ? _microphoneFlowProbe.Probe(_microphoneCandidates)
                : MicrophoneFlowEvidence.Empty;

            bool microphoneInUse = _microphoneActivityGate.Evaluate(
                hasCandidate: _microphoneCandidates.Count > 0,
                hasActiveSession: evidence.HasActiveSession,
                peakLevel: evidence.PeakLevel,
                utcNow);

            PublishState(microphoneInUse, evidence);
        }
        catch (Exception ex)
        {
            _microphoneActivityGate.Reset();
            PublishState(microphoneInUse: false, MicrophoneFlowEvidence.Empty);
            RuntimeLog.Error("PRIVACY-MIC", ex, "Microphone flow probe failed");
        }
    }

    private void PublishState(bool microphoneInUse, MicrophoneFlowEvidence evidence)
    {
        var next = new PrivacyIndicatorState(
            MicrophoneInUse: microphoneInUse,
            CameraInUse: _cameraInUse,
            ScreenRecordingActive: _screenRecordingActive,
            MicrophoneConsumers: microphoneInUse
                ? _microphoneCandidateNames
                : Array.Empty<string>(),
            CameraConsumers: _cameraConsumers);

        if (next.Equals(CurrentState)) return;

        bool microphoneChanged = next.MicrophoneInUse != CurrentState.MicrophoneInUse;
        CurrentState = next;
        if (microphoneChanged)
        {
            RuntimeLog.Debug("PRIVACY-MIC", () =>
                $"visible={microphoneInUse} session={evidence.HasActiveSession} " +
                $"peak={evidence.PeakLevel:F4} consumers=[{string.Join(", ", _microphoneCandidateNames)}]");
        }
        StateChanged?.Invoke(this, next);
    }

    private void AdaptInterval()
    {
        if (!_started) return;
        var desired = CurrentState.AnyInUse ? _activeInterval : IdlePollInterval;
        if (_timer.Interval != desired)
            _timer.Interval = desired;
    }

    private static IReadOnlyList<CapabilityUsage> GetRelevantConsumerUsages(
        IEnumerable<CapabilityUsage> usages,
        ConsumerProcessProbe running,
        Func<CapabilityUsage, bool>? additionalRule = null)
    {
        return usages
            .Where(usage => running.IsRunning(usage.RawName))
            .Where(usage => additionalRule == null || additionalRule(usage))
            .GroupBy(usage => usage.RawName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(usage => usage.LastStartFileTime).First())
            .ToArray();
    }

    private static IReadOnlyList<string> GetConsumerNames(IEnumerable<CapabilityUsage> usages)
    {
        return usages
            .Select(usage => usage.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CapabilityUsage> ScanCapability(string capability)
    {
        var consumers = new List<CapabilityUsage>();

        ScanCapabilityHive(Registry.CurrentUser, capability, consumers);
        ScanCapabilityHive(Registry.LocalMachine, capability, consumers);

        return consumers.Count == 0 ? Array.Empty<CapabilityUsage>() : consumers;
    }

    private static void ScanCapabilityHive(RegistryKey hive, string capability, List<CapabilityUsage> consumers)
    {
        try
        {
            using var capRoot = hive.OpenSubKey(
                $"{ConsentRoot}\\{capability}", writable: false);
            if (capRoot == null) return;

            foreach (var subKeyName in capRoot.GetSubKeyNames())
            {
                using var subKey = capRoot.OpenSubKey(subKeyName, writable: false);
                if (subKey == null) continue;

                if (TryDetectInUse(subKey, out long lastStart))
                {
                    consumers.Add(new CapabilityUsage(
                        subKeyName, NormalizeAppName(subKeyName), lastStart));
                    continue;
                }

                if (string.Equals(subKeyName, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var npName in subKey.GetSubKeyNames())
                    {
                        using var npKey = subKey.OpenSubKey(npName, writable: false);
                        if (npKey == null) continue;
                        if (TryDetectInUse(npKey, out lastStart))
                        {
                            consumers.Add(new CapabilityUsage(
                                npName, NormalizeAppName(npName), lastStart));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, $"Scan {capability} ({hive.Name}) failed");
        }
    }

    private static bool TryDetectInUse(RegistryKey key, out long lastStart)
    {
        var startObj = key.GetValue("LastUsedTimeStart");
        var stopObj = key.GetValue("LastUsedTimeStop");
        lastStart = startObj is long start ? start : 0;
        return IsActiveUsage(lastStart, stopObj is long stop ? stop : null);
    }

    internal static bool IsActiveUsage(long? lastStart, long? lastStop) =>
        lastStart is > 0 && lastStop == 0;

    private static bool DetectScreenRecording(
        IEnumerable<CapabilityUsage> usages,
        ConsumerProcessProbe running,
        DateTime utcNow)
    {
        return usages.Any(usage =>
            running.IsRunning(usage.RawName) &&
            HasMinimumActiveDuration(usage.LastStartFileTime, utcNow, MinimumScreenRecordingDuration));
    }

    internal static bool HasMinimumActiveDuration(long startFileTime, DateTime utcNow, TimeSpan minimum)
    {
        if (startFileTime <= 0 || minimum < TimeSpan.Zero) return false;
        try
        {
            DateTime start = DateTime.FromFileTimeUtc(startFileTime);
            return start <= utcNow && utcNow - start >= minimum;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool IsIgnoredMicrophoneConsumer(
        string rawConsumer,
        IReadOnlySet<string>? serviceExecutablePaths = null)
    {
        string? executablePath = TryDecodeDesktopConsumerPath(rawConsumer);
        string processName = executablePath == null
            ? NormalizeAppName(rawConsumer)
            : Path.GetFileNameWithoutExtension(executablePath);

        if (IgnoredMicrophoneProcessNames.Contains(processName)) return true;
        if (IgnoredMicrophoneProcessSuffixes.Any(suffix =>
            processName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (executablePath == null) return false;
        var servicePaths = serviceExecutablePaths ?? ServiceExecutablePaths.Value;
        return servicePaths.Contains(NormalizeExecutablePath(executablePath));
    }

    internal static string? TryDecodeDesktopConsumerPath(string rawConsumer)
    {
        if (string.IsNullOrWhiteSpace(rawConsumer) || !rawConsumer.Contains('#')) return null;

        string decoded = rawConsumer.Replace('#', Path.DirectorySeparatorChar);
        try
        {
            return NormalizeExecutablePath(decoded);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeExecutablePath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                expanded[12..]);
        }
        return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static IReadOnlySet<string> LoadServiceExecutablePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services", writable: false);
            if (services == null) return paths;

            foreach (string name in services.GetSubKeyNames())
            {
                using var service = services.OpenSubKey(name, writable: false);
                if (service?.GetValue("ImagePath") is not string imagePath) continue;
                string? executable = ExtractExecutablePath(imagePath);
                if (executable != null) paths.Add(executable);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, "Service executable scan failed");
        }
        return paths;
    }

    private static string? ExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string candidate;

        if (expanded[0] == '"')
        {
            int endQuote = expanded.IndexOf('"', 1);
            if (endQuote <= 1) return null;
            candidate = expanded[1..endQuote];
        }
        else
        {
            int exeEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0) return null;
            candidate = expanded[..(exeEnd + 4)];
        }

        try
        {
            return NormalizeExecutablePath(candidate);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ConsumerProcessProbe
    {
        private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _processMatchCache = new(StringComparer.OrdinalIgnoreCase);

        public bool IsRunning(string rawConsumer)
        {
            if (_cache.TryGetValue(rawConsumer, out bool running)) return running;
            running = TryDecodeDesktopConsumerPath(rawConsumer) is { } path
                ? IsDesktopExecutableRunning(path)
                : IsPackageFamilyRunning(rawConsumer);
            _cache[rawConsumer] = running;
            return running;
        }

        public bool MatchesProcess(string rawConsumer, uint processId)
        {
            if (processId == 0) return false;

            string key = $"{processId}|{rawConsumer}";
            if (_processMatchCache.TryGetValue(key, out bool matches)) return matches;

            matches = TryDecodeDesktopConsumerPath(rawConsumer) is { } path
                ? IsDesktopExecutableProcess(path, processId)
                : IsPackageFamilyProcess(rawConsumer, processId);
            _processMatchCache[key] = matches;
            return matches;
        }

        private static bool IsDesktopExecutableRunning(string executablePath)
        {
            string processName = Path.GetFileNameWithoutExtension(executablePath);
            if (string.IsNullOrWhiteSpace(processName)) return false;

            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        string? runningPath = process.MainModule?.FileName;
                        if (runningPath != null && string.Equals(
                            NormalizeExecutablePath(runningPath), executablePath,
                            StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // Strict privacy evidence: an unverifiable process does not
                        // keep an indicator alive from a possibly stale registry key.
                    }
                }
            }
            return false;
        }

        private static bool IsDesktopExecutableProcess(string executablePath, uint processId)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                string? runningPath = process.MainModule?.FileName;
                return runningPath != null && string.Equals(
                    NormalizeExecutablePath(runningPath),
                    executablePath,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPackageFamilyRunning(string packageFamily)
        {
            if (string.IsNullOrWhiteSpace(packageFamily)) return false;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (IsPackageFamilyProcess(packageFamily, (uint)process.Id))
                        return true;
                }
            }
            return false;
        }

        private static bool IsPackageFamilyProcess(string packageFamily, uint processId)
        {
            if (string.IsNullOrWhiteSpace(packageFamily) || processId == 0) return false;

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
                if (handle == IntPtr.Zero) return false;

                uint chars = 0;
                int result = GetPackageFamilyName(handle, ref chars, IntPtr.Zero);
                if (result != ErrorInsufficientBuffer || chars == 0) return false;

                IntPtr buffer = Marshal.AllocHGlobal(checked((int)chars * sizeof(char)));
                try
                {
                    result = GetPackageFamilyName(handle, ref chars, buffer);
                    if (result != 0) return false;
                    string? family = Marshal.PtrToStringUni(buffer);
                    return string.Equals(family, packageFamily, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
    }

    private sealed class MicrophoneFlowProbe : IDisposable
    {
        private MMDeviceEnumerator? _enumerator;

        public MicrophoneFlowEvidence Probe(IReadOnlyList<CapabilityUsage> candidates)
        {
            if (candidates.Count == 0) return MicrophoneFlowEvidence.Empty;

            bool hasActiveSession = false;
            float peakLevel = 0;
            var processProbe = new ConsumerProcessProbe();

            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var device in devices)
                {
                    using (device)
                    {
                        try
                        {
                            if (device.AudioEndpointVolume.Mute) continue;
                        }
                        catch
                        {
                            // Some virtual endpoints do not expose endpoint mute.
                        }

                        var sessions = device.AudioSessionManager.Sessions;
                        if (sessions == null) continue;

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            if (session == null) continue;

                            try
                            {
                                if (session.State != AudioSessionState.AudioSessionStateActive)
                                    continue;

                                uint processId = session.GetProcessID;
                                if (!candidates.Any(candidate =>
                                        processProbe.MatchesProcess(candidate.RawName, processId)))
                                    continue;

                                using (var volume = session.SimpleAudioVolume)
                                {
                                    if (volume.Mute) continue;
                                }

                                hasActiveSession = true;

                                float sessionPeak = 0;
                                bool sessionPeakAvailable = false;
                                try
                                {
                                    sessionPeak = session.AudioMeterInformation.MasterPeakValue;
                                    sessionPeakAvailable = true;
                                }
                                catch
                                {
                                    // Fall back to the endpoint meter below.
                                }

                                float endpointPeak = 0;
                                if (!sessionPeakAvailable)
                                {
                                    try
                                    {
                                        endpointPeak = device.AudioMeterInformation.MasterPeakValue;
                                    }
                                    catch
                                    {
                                        // A matched active session is still valid evidence,
                                        // but silence must not turn the indicator on.
                                    }
                                }

                                peakLevel = Math.Max(peakLevel, Math.Max(sessionPeak, endpointPeak));
                            }
                            finally
                            {
                                try { session.Dispose(); } catch { }
                            }
                        }
                    }
                }
            }
            catch
            {
                DisposeEnumerator();
                throw;
            }

            if (!float.IsFinite(peakLevel) || peakLevel < 0)
                peakLevel = 0;

            return new MicrophoneFlowEvidence(
                hasActiveSession,
                Math.Clamp(peakLevel, 0, 1));
        }

        public void Dispose() => DisposeEnumerator();

        private void DisposeEnumerator()
        {
            if (_enumerator == null) return;
            try { _enumerator.Dispose(); } catch { }
            _enumerator = null;
        }
    }

    internal sealed class MicrophoneActivityGate
    {
        private readonly float _signalThreshold;
        private readonly TimeSpan _holdDuration;
        private DateTime _lastSignalUtc = DateTime.MinValue;

        public MicrophoneActivityGate(float signalThreshold, TimeSpan holdDuration)
        {
            _signalThreshold = Math.Max(0, signalThreshold);
            _holdDuration = holdDuration < TimeSpan.Zero ? TimeSpan.Zero : holdDuration;
        }

        public bool Evaluate(
            bool hasCandidate,
            bool hasActiveSession,
            float peakLevel,
            DateTime utcNow)
        {
            if (!hasCandidate || !hasActiveSession)
            {
                Reset();
                return false;
            }

            if (float.IsFinite(peakLevel) && peakLevel >= _signalThreshold)
                _lastSignalUtc = utcNow;

            if (_lastSignalUtc == DateTime.MinValue || utcNow < _lastSignalUtc)
                return false;

            return utcNow - _lastSignalUtc <= _holdDuration;
        }

        public void Reset() => _lastSignalUtc = DateTime.MinValue;
    }

    private static string NormalizeAppName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        if (raw.Contains('#'))
        {
            var parts = raw.Split('#');
            var last = parts[^1];
            if (!string.IsNullOrWhiteSpace(last)) return last;
        }

        var underscore = raw.IndexOf('_');
        if (underscore > 0)
        {
            return raw[..underscore];
        }

        return raw;
    }
}

internal readonly record struct CapabilityUsage(
    string RawName,
    string DisplayName,
    long LastStartFileTime);

internal readonly record struct MicrophoneFlowEvidence(
    bool HasActiveSession,
    float PeakLevel)
{
    public static readonly MicrophoneFlowEvidence Empty = new(false, 0);
}

public sealed record PrivacyIndicatorState(
    bool MicrophoneInUse,
    bool CameraInUse,
    bool ScreenRecordingActive,
    IReadOnlyList<string> MicrophoneConsumers,
    IReadOnlyList<string> CameraConsumers)
{
    public static readonly PrivacyIndicatorState Empty = new(
        false, false, false, Array.Empty<string>(), Array.Empty<string>());

    public bool AnyInUse => MicrophoneInUse || CameraInUse || ScreenRecordingActive;

    public bool Equals(PrivacyIndicatorState? other)
    {
        if (other is null) return false;
        if (MicrophoneInUse != other.MicrophoneInUse) return false;
        if (CameraInUse != other.CameraInUse) return false;
        if (ScreenRecordingActive != other.ScreenRecordingActive) return false;
        return SequenceEquals(MicrophoneConsumers, other.MicrophoneConsumers)
            && SequenceEquals(CameraConsumers, other.CameraConsumers);
    }

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(MicrophoneInUse, CameraInUse, ScreenRecordingActive);
        foreach (var s in MicrophoneConsumers) hash = HashCode.Combine(hash, s);
        foreach (var s in CameraConsumers) hash = HashCode.Combine(hash, s);
        return hash;
    }

    private static bool SequenceEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
