using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    private static readonly TimeSpan MicrophoneFlowPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan InactiveMicrophoneFlowPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumScreenRecordingDuration = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MicrophoneSignalHoldDuration = TimeSpan.FromMilliseconds(1400);
    internal const float MicrophoneSignalThreshold = 0.0125f;

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

    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _activeInterval;
    private readonly MicrophoneActivityGate _microphoneActivityGate = new(
        MicrophoneSignalThreshold,
        MicrophoneSignalHoldDuration);
    private readonly MicrophoneFlowProbe _microphoneFlowProbe = new();
    private readonly ScreenRecordingProbe _screenRecordingProbe = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _micFlowGate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private readonly HashSet<Task> _workers = new();
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private int _currentGeneration;

    private CancellationTokenSource? _micFlowCts;
    private Task? _micFlowTask;
    private int _micFlowGeneration;

    private IReadOnlyList<CapabilityUsage> _microphoneCandidates = Array.Empty<CapabilityUsage>();
    private IReadOnlyList<string> _microphoneCandidateNames = Array.Empty<string>();
    private IReadOnlyList<string> _cameraConsumers = Array.Empty<string>();
    private bool _cameraInUse;
    private bool _screenRecordingActive;
    private bool _locationInUse;
    private IReadOnlyList<string> _locationConsumers = Array.Empty<string>();
    private bool _disposed;
    private bool _started;

    public event EventHandler<PrivacyIndicatorState>? StateChanged;

    public PrivacyIndicatorState CurrentState { get; private set; } = PrivacyIndicatorState.Empty;

    public PrivacyIndicatorService(TimeSpan? pollInterval = null)
    {
        _activeInterval = pollInterval ?? ActivePollInterval;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _started) return;
            _started = true;
            _currentGeneration++;
            int generation = _currentGeneration;

            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;

            _workerTask = TrackWorkerLocked(Task.Run(() => WorkerLoopAsync(generation, token), token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? ctsToCancel;
        Task? worker;
        lock (_lifecycleLock)
        {
            if (!_started) return;
            _started = false;
            _currentGeneration++;
            ctsToCancel = _workerCts;
            worker = _workerTask;
            _workerCts = null;
            _workerTask = null;

            StopMicrophoneFlowWorkerLocked();
        }

        ctsToCancel?.Cancel();
        DisposeCancellationWhenFinished(ctsToCancel, worker);
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        lock (_lifecycleLock)
            CleanupCompletion = CleanupAfterWorkersAsync(_workers.ToArray());
    }

    private Task TrackWorkerLocked(Task worker)
    {
        _workers.RemoveWhere(task => task.IsCompleted);
        _workers.Add(worker);
        return worker;
    }

    private static void DisposeCancellationWhenFinished(CancellationTokenSource? source, Task? worker)
    {
        if (source == null) return;
        if (worker == null) { source.Dispose(); return; }
        _ = worker.ContinueWith(_ => source.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task CleanupAfterWorkersAsync(Task[] workers)
    {
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RuntimeLog.Error("PRIVACY", ex, "Privacy worker failed during shutdown"); }
        finally
        {
            // Dispose is synchronous on the UI thread, but gates and COM probes
            // must remain alive until every current/retired worker has exited.
            _scanGate.Dispose();
            _micFlowGate.Dispose();
            _microphoneFlowProbe.Dispose();
            lock (_lifecycleLock) _workers.Clear();
        }
    }

    private async Task WorkerLoopAsync(int generation, CancellationToken token)
    {
        using var userChanges = new RegistryChangeMonitor(Registry.CurrentUser, ConsentRoot);
        using var machineChanges = new RegistryChangeMonitor(Registry.LocalMachine, ConsentRoot);
        IReadOnlyList<CapabilityUsage>[]? capabilities = null;
        long nextFullScan = 0;
        while (!token.IsCancellationRequested)
        {
            PrivacyScanResult? result = null;
            try
            {
                await _scanGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested || generation != _currentGeneration)
                        break;

                    DateTime utcNow = DateTime.UtcNow;
                    bool registryChanged = userChanges.ConsumeChange() | machineChanges.ConsumeChange();
                    long now = Environment.TickCount64;
                    if (capabilities == null || registryChanged || now >= nextFullScan)
                    {
                        capabilities = new[]
                        {
                            ScanCapability("microphone"), ScanCapability("webcam"),
                            ScanCapability("graphicsCaptureProgrammatic"), ScanCapability("graphicsCaptureWithoutBorder"),
                            ScanCapability("location")
                        };
                        nextFullScan = now + 30_000;
                    }
                    // Process exits and recording-duration thresholds still need reconciliation.
                    result = ExecuteBackgroundScan(utcNow, capabilities);
                }
                finally
                {
                    _scanGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("PRIVACY", ex, "PrivacyIndicatorService background scan failed");
            }

            if (token.IsCancellationRequested || generation != _currentGeneration)
                break;

            if (result != null)
            {
                PublishSnapshotToUi(result, generation);
            }

            TimeSpan delay = CurrentState.AnyInUse ? _activeInterval : IdlePollInterval;
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private PrivacyScanResult ExecuteBackgroundScan(DateTime utcNow, IReadOnlyList<CapabilityUsage>[]? capabilities = null)
    {
        var micUsage = capabilities?[0] ?? ScanCapability("microphone");
        var camUsage = capabilities?[1] ?? ScanCapability("webcam");
        var programmaticCapture = capabilities?[2] ?? ScanCapability("graphicsCaptureProgrammatic");
        var borderlessCapture = capabilities?[3] ?? ScanCapability("graphicsCaptureWithoutBorder");
        var locationUsage = capabilities?[4] ?? ScanCapability("location");

        var running = new ConsumerProcessProbe();
        var microphoneCandidates = GetRelevantConsumerUsages(
            micUsage,
            running.IsRunning,
            usage => !IsIgnoredMicrophoneConsumer(usage.RawName));
        var microphoneCandidateNames = GetConsumerNames(microphoneCandidates);

        var cam = GetRelevantConsumerUsages(camUsage, running.IsRunning);
        var cameraConsumers = GetConsumerNames(cam);
        bool cameraInUse = cameraConsumers.Count > 0;
        var locationConsumers = GetConsumerNames(GetRelevantConsumerUsages(locationUsage, running.IsRunning));
        bool screenRecordingActive = DetectScreenRecording(
            programmaticCapture.Concat(borderlessCapture), running, utcNow) ||
            _screenRecordingProbe.IsRecording();

        return new PrivacyScanResult(
            MicrophoneCandidates: microphoneCandidates,
            MicrophoneCandidateNames: microphoneCandidateNames,
            CameraConsumers: cameraConsumers,
            CameraInUse: cameraInUse,
            ScreenRecordingActive: screenRecordingActive,
            LocationConsumers: locationConsumers,
            UtcNow: utcNow);
    }

    private void PublishSnapshotToUi(PrivacyScanResult result, int generation)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || !_started || generation != _currentGeneration)
                return;
        }

        if (_dispatcher.HasShutdownStarted) return;

        void Apply()
        {
            lock (_lifecycleLock)
            {
                if (_disposed || !_started || generation != _currentGeneration)
                    return;

                ApplyScanResult(result);
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Apply));
        }
    }

    private void ApplyScanResult(PrivacyScanResult result)
    {
        _microphoneCandidates = result.MicrophoneCandidates;
        _microphoneCandidateNames = result.MicrophoneCandidateNames;
        _cameraConsumers = result.CameraConsumers;
        _cameraInUse = result.CameraInUse;
        _screenRecordingActive = result.ScreenRecordingActive;
        _locationConsumers = result.LocationConsumers;
        _locationInUse = _locationConsumers.Count > 0;

        if (_microphoneCandidates.Count > 0)
        {
            lock (_lifecycleLock)
            {
                StartMicrophoneFlowWorkerLocked();
            }
        }
        else
        {
            lock (_lifecycleLock)
            {
                StopMicrophoneFlowWorkerLocked();
            }
            _microphoneActivityGate.Reset();
            PublishState(microphoneInUse: false, MicrophoneFlowEvidence.Empty);
        }
    }

    private void StartMicrophoneFlowWorkerLocked()
    {
        if (_micFlowTask != null && _micFlowCts != null && !_micFlowCts.IsCancellationRequested)
            return;

        if (_disposed || !_started) return;

        _micFlowGeneration++;
        int generation = _micFlowGeneration;
        _micFlowCts = new CancellationTokenSource();
        var token = _micFlowCts.Token;
        _micFlowTask = TrackWorkerLocked(Task.Run(() => MicrophoneFlowWorkerLoopAsync(generation, token), token));
    }

    private void StopMicrophoneFlowWorkerLocked()
    {
        _micFlowGeneration++;
        var cts = _micFlowCts;
        var worker = _micFlowTask;
        _micFlowCts = null;
        _micFlowTask = null;
        cts?.Cancel();
        DisposeCancellationWhenFinished(cts, worker);
    }

    private async Task MicrophoneFlowWorkerLoopAsync(int generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TimeSpan pollInterval = InactiveMicrophoneFlowPollInterval;
            try
            {
                await _micFlowGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested || generation != _micFlowGeneration)
                        break;

                    var candidates = _microphoneCandidates;
                    if (candidates.Count == 0)
                        break;

                    DateTime utcNow = DateTime.UtcNow;
                    MicrophoneFlowEvidence evidence = _microphoneFlowProbe.Probe(candidates);
                    if (evidence.HasActiveSession)
                        pollInterval = MicrophoneFlowPollInterval;

                    bool microphoneInUse = _microphoneActivityGate.Evaluate(
                        hasCandidate: candidates.Count > 0,
                        hasActiveSession: evidence.HasActiveSession,
                        peakLevel: evidence.PeakLevel,
                        utcNow);

                    PublishMicStateToUi(microphoneInUse, evidence, generation);
                }
                finally
                {
                    _micFlowGate.Release();
                }

                await Task.Delay(pollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("PRIVACY-MIC", ex, "Microphone flow worker loop error");
                try
                {
                    await Task.Delay(InactiveMicrophoneFlowPollInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void PublishMicStateToUi(bool microphoneInUse, MicrophoneFlowEvidence evidence, int generation)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || !_started || generation != _micFlowGeneration)
                return;
        }

        if (_dispatcher.HasShutdownStarted) return;

        void Apply()
        {
            lock (_lifecycleLock)
            {
                if (_disposed || !_started || generation != _micFlowGeneration)
                    return;

                PublishState(microphoneInUse, evidence);
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Apply));
        }
    }

    internal void PollMicrophoneFlow()
    {
        try
        {
            var candidates = _microphoneCandidates;
            MicrophoneFlowEvidence evidence = candidates.Count > 0
                ? _microphoneFlowProbe.Probe(candidates)
                : MicrophoneFlowEvidence.Empty;

            bool microphoneInUse = _microphoneActivityGate.Evaluate(
                hasCandidate: candidates.Count > 0,
                hasActiveSession: evidence.HasActiveSession,
                peakLevel: evidence.PeakLevel,
                DateTime.UtcNow);

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
            CameraConsumers: _cameraConsumers)
        {
            LocationInUse = _locationInUse,
            LocationConsumers = _locationConsumers
        };

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
        // Poll intervals are dynamically resolved per worker cycle (1s when active, 2s when idle).
    }

    private sealed record PrivacyScanResult(
        IReadOnlyList<CapabilityUsage> MicrophoneCandidates,
        IReadOnlyList<string> MicrophoneCandidateNames,
        IReadOnlyList<string> CameraConsumers,
        bool CameraInUse,
        bool ScreenRecordingActive,
        IReadOnlyList<string> LocationConsumers,
        DateTime UtcNow);

    internal static IReadOnlyList<CapabilityUsage> GetRelevantConsumerUsages(
        IEnumerable<CapabilityUsage> usages,
        Func<string, bool> isRunning,
        Func<CapabilityUsage, bool>? additionalRule = null)
    {
        Dictionary<string, CapabilityUsage>? latest = null;
        foreach (var usage in usages)
        {
            if ((additionalRule != null && !additionalRule(usage)) || !isRunning(usage.RawName))
                continue;

            latest ??= new(StringComparer.OrdinalIgnoreCase);
            if (!latest.TryGetValue(usage.RawName, out var previous) ||
                usage.LastStartFileTime > previous.LastStartFileTime)
                latest[usage.RawName] = usage;
        }
        return latest == null ? Array.Empty<CapabilityUsage>() : latest.Values.ToArray();
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
                    ScanNonPackagedKey(subKey, consumers);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, $"Scan {capability} ({hive.Name}) failed");
        }
    }

    private static void ScanNonPackagedKey(RegistryKey nonPackagedKey, List<CapabilityUsage> consumers)
    {
        foreach (var npName in nonPackagedKey.GetSubKeyNames())
        {
            using var npKey = nonPackagedKey.OpenSubKey(npName, writable: false);
            if (npKey != null && TryDetectInUse(npKey, out long lastStart))
            {
                consumers.Add(new CapabilityUsage(
                    npName, NormalizeAppName(npName), lastStart));
            }
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

    internal sealed class ConsumerProcessProbe
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern int GetPackageFamilyName(IntPtr process, ref uint packageFamilyNameLength, IntPtr packageFamilyName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern unsafe bool QueryFullProcessImageName(
            IntPtr process, uint flags, char* path, ref uint size);

        private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _processMatchCache = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<uint>>? _processIdsByName;

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

        private bool IsDesktopExecutableRunning(string executablePath)
        {
            string processName = Path.GetFileName(executablePath);
            if (string.IsNullOrWhiteSpace(processName)) return false;

            _processIdsByName ??= PrivacyProcessSnapshot.Capture();
            if (!_processIdsByName.TryGetValue(processName, out var ids)) return false;
            foreach (uint pid in ids)
                if (IsDesktopExecutableProcess(executablePath, pid)) return true;
            return false;
        }

        internal static unsafe bool IsDesktopExecutableProcess(string executablePath, uint processId)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return false;
            try
            {
                char* path = stackalloc char[1024];
                uint length = 1024;
                if (QueryFullProcessImageName(handle, 0, path, ref length))
                    return string.Equals(new string(path, 0, (int)length), executablePath,
                        StringComparison.OrdinalIgnoreCase);

                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return false;
                char[] largePath = System.Buffers.ArrayPool<char>.Shared.Rent(32768);
                try
                {
                    length = 32768;
                    fixed (char* buffer = largePath)
                        return QueryFullProcessImageName(handle, 0, buffer, ref length) &&
                            string.Equals(new string(buffer, 0, (int)length), executablePath,
                                StringComparison.OrdinalIgnoreCase);
                }
                finally { System.Buffers.ArrayPool<char>.Shared.Return(largePath); }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string Family, long ExpireTicks)> _packageFamilyByPidCache = new();

        private bool IsPackageFamilyRunning(string packageFamily)
        {
            if (string.IsNullOrWhiteSpace(packageFamily)) return false;

            _processIdsByName ??= PrivacyProcessSnapshot.Capture();
            foreach (var ids in _processIdsByName.Values)
                foreach (uint pid in ids)
                    if (IsPackageFamilyProcess(packageFamily, pid)) return true;
            return false;
        }

        private static bool IsPackageFamilyProcess(string packageFamily, uint processId)
        {
            if (string.IsNullOrWhiteSpace(packageFamily) || processId == 0) return false;

            long now = Environment.TickCount64;
            if (_packageFamilyByPidCache.TryGetValue(processId, out var cached) && now < cached.ExpireTicks)
            {
                return !string.IsNullOrEmpty(cached.Family) &&
                       string.Equals(cached.Family, packageFamily, StringComparison.OrdinalIgnoreCase);
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
                if (handle == IntPtr.Zero)
                {
                    _packageFamilyByPidCache[processId] = (string.Empty, now + 15000);
                    return false;
                }

                unsafe
                {
                    uint chars = 128;
                    char* buffer = stackalloc char[128];
                    int result = GetPackageFamilyName(handle, ref chars, (IntPtr)buffer);
                    if (result == 0 && chars > 1)
                    {
                        string family = new string(buffer, 0, (int)chars - 1);
                        _packageFamilyByPidCache[processId] = (family, now + 60000);
                        return string.Equals(family, packageFamily, StringComparison.OrdinalIgnoreCase);
                    }
                    if (result == ErrorInsufficientBuffer && chars > 128 && chars <= 1024)
                    {
                        char* bigBuffer = stackalloc char[(int)chars];
                        if (GetPackageFamilyName(handle, ref chars, (IntPtr)bigBuffer) == 0 && chars > 1)
                        {
                            string family = new string(bigBuffer, 0, (int)chars - 1);
                            _packageFamilyByPidCache[processId] = (family, now + 60000);
                            return string.Equals(family, packageFamily, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                _packageFamilyByPidCache[processId] = (string.Empty, now + 30000);
                return false;
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

    private sealed class CachedSessionMeter : IDisposable
    {
        public MMDevice Device { get; }
        public AudioSessionControl Session { get; }
        public uint ProcessId { get; }

        public CachedSessionMeter(MMDevice device, AudioSessionControl session, uint processId)
        {
            Device = device;
            Session = session;
            ProcessId = processId;
        }

        public (bool IsValid, float Peak) SamplePeak()
        {
            try
            {
                if (Session.State != AudioSessionState.AudioSessionStateActive)
                    return (false, 0f);

                using (var volume = Session.SimpleAudioVolume)
                {
                    if (volume.Mute) return (true, 0f);
                }

                try
                {
                    if (Device.AudioEndpointVolume.Mute) return (true, 0f);
                }
                catch
                {
                    // Some virtual endpoints do not expose endpoint mute.
                }

                float sessionPeak = GetSessionPeak(Session);
                float endpointPeak = sessionPeak > 0 ? 0 : GetEndpointPeak(Device);
                return (true, Math.Max(sessionPeak, endpointPeak));
            }
            catch
            {
                // COM error, session invalidated, or device disconnected
                return (false, 0f);
            }
        }

        private static float GetSessionPeak(AudioSessionControl session)
        {
            try
            {
                return session.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static float GetEndpointPeak(MMDevice device)
        {
            try
            {
                return device.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { Device.Dispose(); } catch { }
        }
    }

    private sealed class AudioNotificationClient : IMMNotificationClient
    {
        private readonly Action _onInvalidated;

        public AudioNotificationClient(Action onInvalidated)
        {
            _onInvalidated = onInvalidated;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _onInvalidated();
        public void OnDeviceAdded(string pwstrDeviceId) => _onInvalidated();
        public void OnDeviceRemoved(string deviceId) => _onInvalidated();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _onInvalidated();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private sealed class MicrophoneFlowProbe : IDisposable
    {
        private MMDeviceEnumerator? _enumerator;
        private AudioNotificationClient? _notificationClient;
        private readonly List<CachedSessionMeter> _cachedMeters = new();
        private string[] _cachedCandidateNames = Array.Empty<string>();
        private bool _isTopologyValid;
        private long _lastRebuildTicks;
        private readonly object _probeLock = new();

        public MicrophoneFlowEvidence Probe(IReadOnlyList<CapabilityUsage> candidates)
        {
            if (candidates.Count == 0)
            {
                lock (_probeLock)
                {
                    InvalidateCacheLocked();
                }
                return MicrophoneFlowEvidence.Empty;
            }

            lock (_probeLock)
            {
                EnsureTopologyLocked(candidates);

                bool hasActiveSession = false;
                float peakLevel = 0;

                for (int i = _cachedMeters.Count - 1; i >= 0; i--)
                {
                    var meter = _cachedMeters[i];
                    var (isValid, peak) = meter.SamplePeak();
                    if (!isValid)
                    {
                        // Session expired, muted, or error -> invalidate topology to refresh
                        _isTopologyValid = false;
                        continue;
                    }

                    hasActiveSession = true;
                    if (peak > peakLevel)
                        peakLevel = peak;
                }

                // If topology became invalid during sampling and we had no active session,
                // rebuild topology immediately if rate limit allows.
                if (!_isTopologyValid && !hasActiveSession)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsedSec = (double)(now - _lastRebuildTicks) / Stopwatch.Frequency;
                    if (elapsedSec >= 1.0)
                    {
                        RebuildTopologyLocked(candidates);
                        for (int i = 0; i < _cachedMeters.Count; i++)
                        {
                            var (isValid, peak) = _cachedMeters[i].SamplePeak();
                            if (isValid)
                            {
                                hasActiveSession = true;
                                if (peak > peakLevel)
                                    peakLevel = peak;
                            }
                        }
                    }
                }

                if (!float.IsFinite(peakLevel) || peakLevel < 0)
                    peakLevel = 0;

                return new MicrophoneFlowEvidence(
                    hasActiveSession,
                    Math.Clamp(peakLevel, 0, 1));
            }
        }

        private void EnsureTopologyLocked(IReadOnlyList<CapabilityUsage> candidates)
        {
            var currentNames = candidates.Select(c => c.RawName).ToArray();
            if (!_cachedCandidateNames.SequenceEqual(currentNames, StringComparer.OrdinalIgnoreCase))
            {
                _cachedCandidateNames = currentNames;
                _isTopologyValid = false;
            }

            if (_isTopologyValid && _cachedMeters.Count > 0)
                return;

            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (double)(now - _lastRebuildTicks) / Stopwatch.Frequency;

            // If topology is invalid, or if we found 0 active sessions and at least 1s elapsed since last attempt:
            if (!_isTopologyValid || (_cachedMeters.Count == 0 && elapsedSec >= 1.0))
            {
                RebuildTopologyLocked(candidates);
            }
        }

        private void RebuildTopologyLocked(IReadOnlyList<CapabilityUsage> candidates)
        {
            _lastRebuildTicks = Stopwatch.GetTimestamp();
            InvalidateCacheLocked();

            var processProbe = new ConsumerProcessProbe();

            try
            {
                EnsureEnumeratorLocked();
                if (_enumerator == null) return;

                var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var device in devices)
                {
                    bool deviceRetained = false;
                    try
                    {
                        try
                        {
                            if (device.AudioEndpointVolume.Mute)
                            {
                                continue;
                            }
                        }
                        catch
                        {
                            // Some virtual endpoints do not expose endpoint mute.
                        }

                        var sessions = device.AudioSessionManager.Sessions;
                        if (sessions != null)
                        {
                            for (int i = 0; i < sessions.Count; i++)
                            {
                                var session = sessions[i];
                                if (session == null) continue;

                                bool sessionRetained = false;
                                try
                                {
                                    if (session.State == AudioSessionState.AudioSessionStateActive)
                                    {
                                        uint processId = session.GetProcessID;
                                        if (candidates.Any(candidate => processProbe.MatchesProcess(candidate.RawName, processId)))
                                        {
                                            using var volume = session.SimpleAudioVolume;
                                            if (!volume.Mute)
                                            {
                                                _cachedMeters.Add(new CachedSessionMeter(device, session, processId));
                                                deviceRetained = true;
                                                sessionRetained = true;
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    if (!sessionRetained)
                                    {
                                        try { session.Dispose(); } catch { }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (!deviceRetained)
                        {
                            try { device.Dispose(); } catch { }
                        }
                    }
                }

                _isTopologyValid = true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("PRIVACY-MIC", $"Topology rebuild failed: {ex.Message}");
                InvalidateCacheLocked();
                DisposeEnumeratorLocked();
            }
        }

        private void EnsureEnumeratorLocked()
        {
            if (_enumerator != null) return;
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _notificationClient = new AudioNotificationClient(OnDeviceNotification);
                _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("PRIVACY-MIC", $"Failed to register endpoint notification callback: {ex.Message}");
                DisposeEnumeratorLocked();
            }
        }

        private void OnDeviceNotification()
        {
            lock (_probeLock)
            {
                _isTopologyValid = false;
            }
        }

        private void InvalidateCacheLocked()
        {
            _isTopologyValid = false;
            for (int i = 0; i < _cachedMeters.Count; i++)
            {
                _cachedMeters[i].Dispose();
            }
            _cachedMeters.Clear();
        }

        public void Dispose()
        {
            lock (_probeLock)
            {
                InvalidateCacheLocked();
                DisposeEnumeratorLocked();
            }
        }

        private void DisposeEnumeratorLocked()
        {
            if (_enumerator == null) return;
            try
            {
                if (_notificationClient != null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
            }
            catch { }
            _notificationClient = null;

            try
            {
                _enumerator.Dispose();
            }
            catch { }
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
    public bool LocationInUse { get; init; }
    public IReadOnlyList<string> LocationConsumers { get; init; } = Array.Empty<string>();

    public static readonly PrivacyIndicatorState Empty = new(
        false, false, false, Array.Empty<string>(), Array.Empty<string>());

    public bool AnyInUse => MicrophoneInUse || CameraInUse || ScreenRecordingActive || LocationInUse;

    public bool Equals(PrivacyIndicatorState? other)
    {
        if (other is null) return false;
        if (MicrophoneInUse != other.MicrophoneInUse) return false;
        if (CameraInUse != other.CameraInUse) return false;
        if (ScreenRecordingActive != other.ScreenRecordingActive) return false;
        if (LocationInUse != other.LocationInUse) return false;
        return SequenceEquals(MicrophoneConsumers, other.MicrophoneConsumers)
            && SequenceEquals(CameraConsumers, other.CameraConsumers)
            && SequenceEquals(LocationConsumers, other.LocationConsumers);
    }

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(MicrophoneInUse, CameraInUse, ScreenRecordingActive, LocationInUse);
        foreach (var s in MicrophoneConsumers) hash = HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(s));
        foreach (var s in CameraConsumers) hash = HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(s));
        foreach (var s in LocationConsumers) hash = HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(s));
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
