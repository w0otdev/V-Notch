using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VNotch.Services;

public sealed partial class PrivacyIndicatorService : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_privacy_indicator_has_min_active_duration(
        long startFileTime,
        long utcNowTicks,
        long minimumTicks,
        out byte outActive);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_indicator_is_ignored_mic_process(
        ReadOnlySpan<char> processName,
        int processNameLen,
        out byte outIgnored);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privacy_indicator_normalize_app_name_slice(
        ReadOnlySpan<char> raw,
        int rawLen,
        out int outStart,
        out int outLen);

    [LibraryImport(NativeDll)]
    private static partial int vn_privacy_indicator_evaluate_mic_gate(
        byte hasCandidate,
        byte hasActiveSession,
        float peakLevel,
        float signalThreshold,
        long holdDurationTicks,
        long utcNowTicks,
        ref long inoutLastSignalUtcTicks,
        out byte outActive);

    private const string ConsentRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MicrophoneFlowPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan InactiveMicrophoneFlowPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumScreenRecordingDuration = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MicrophoneSignalHoldDuration = TimeSpan.FromMilliseconds(1400);
    internal const float MicrophoneSignalThreshold = 0.0125f;

    private static readonly Func<CapabilityUsage, bool> s_notIgnoredMicRule =
        static usage => !IsIgnoredMicrophoneConsumer(usage.RawName);



    private static readonly Lazy<IReadOnlySet<string>> ServiceExecutablePaths =
        new(LoadServiceExecutablePaths);

    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _activeInterval;
    private readonly Func<string, IReadOnlyList<CapabilityUsage>> _scanCapability;
    private readonly Func<bool>? _registryChanged;
    private readonly MicrophoneActivityGate _microphoneActivityGate = new(
        MicrophoneSignalThreshold,
        MicrophoneSignalHoldDuration);
    private readonly MicrophoneFlowProbe _microphoneFlowProbe = new();
    private readonly ScreenRecordingProbe _screenRecordingProbe;
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
    private PrivacyScanResult? _lastPublishedScan;
    private bool _disposed;
    private bool _started;

    public event EventHandler<PrivacyIndicatorState>? StateChanged;

    public PrivacyIndicatorState CurrentState { get; private set; } = PrivacyIndicatorState.Empty;

    public PrivacyIndicatorService(TimeSpan? pollInterval = null)
        : this(pollInterval, ScanCapability, null, new ScreenRecordingProbe())
    {
    }

    internal PrivacyIndicatorService(TimeSpan? pollInterval,
        Func<string, IReadOnlyList<CapabilityUsage>> scanCapability,
        Func<bool>? registryChanged,
        ScreenRecordingProbe screenRecordingProbe)
    {
        _activeInterval = pollInterval ?? ActivePollInterval;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _scanCapability = scanCapability;
        _registryChanged = registryChanged;
        _screenRecordingProbe = screenRecordingProbe;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _started) return;
            // A resumed worker must publish fresh evidence even when it matches
            // the state observed before its indicator was disabled.
            CurrentState = PrivacyIndicatorState.Empty;
            _lastPublishedScan = null;
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
            _lastPublishedScan = null;
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
        {
            var workersSnapshot = new Task[_workers.Count];
            _workers.CopyTo(workersSnapshot);
            CleanupCompletion = CleanupAfterWorkersAsync(workersSnapshot);
        }
    }

    private Task TrackWorkerLocked(Task worker)
    {
        _workers.RemoveWhere(static task => task.IsCompleted);
        _workers.Add(worker);
        return worker;
    }

    private static void DisposeCancellationWhenFinished(CancellationTokenSource? source, Task? worker)
    {
        if (source == null) return;
        if (worker == null) { source.Dispose(); return; }
        _ = worker.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), source,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task CleanupAfterWorkersAsync(Task[] workers)
    {
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* Expected on shutdown */ }
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
        using var userChanges = _registryChanged == null
            ? new RegistryChangeMonitor(Registry.CurrentUser, ConsentRoot) : null;
        using var machineChanges = _registryChanged == null
            ? new RegistryChangeMonitor(Registry.LocalMachine, ConsentRoot) : null;
        var capabilityCache = new CapabilityUsageCache(_scanCapability);
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
                    bool registryChanged;
                    if (_registryChanged != null)
                    {
                        registryChanged = _registryChanged();
                    }
                    else
                    {
                        bool userChanged = userChanges!.ConsumeChange();
                        bool machineChanged = machineChanges!.ConsumeChange();
                        registryChanged = userChanged || machineChanged;
                    }
                    var capabilities = capabilityCache.GetSnapshot(registryChanged, Environment.TickCount64);
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
        var micUsage = capabilities?[0] ?? _scanCapability("microphone");
        var camUsage = capabilities?[1] ?? _scanCapability("webcam");
        var programmaticCapture = capabilities?[2] ?? _scanCapability("graphicsCaptureProgrammatic");
        var borderlessCapture = capabilities?[3] ?? _scanCapability("graphicsCaptureWithoutBorder");
        var locationUsage = capabilities?[4] ?? _scanCapability("location");

        using var running = new ConsumerProcessProbe();
        var microphoneCandidates = GetRelevantConsumerUsages(
            micUsage,
            running.IsRunning,
            s_notIgnoredMicRule);
        var microphoneCandidateNames = GetConsumerNames(microphoneCandidates);

        var cam = GetRelevantConsumerUsages(camUsage, running.IsRunning);
        var cameraConsumers = GetConsumerNames(cam);
        bool cameraInUse = cameraConsumers.Count > 0;
        var locationConsumers = GetConsumerNames(GetRelevantConsumerUsages(locationUsage, running.IsRunning));
        bool screenRecordingActive =
            DetectScreenRecording(programmaticCapture, running, utcNow) ||
            DetectScreenRecording(borderlessCapture, running, utcNow) ||
            _screenRecordingProbe.IsRecording(running.GetProcessSnapshot);

        return new PrivacyScanResult(
            MicrophoneCandidates: microphoneCandidates,
            MicrophoneCandidateNames: microphoneCandidateNames,
            CameraConsumers: cameraConsumers,
            CameraInUse: cameraInUse,
            ScreenRecordingActive: screenRecordingActive,
            LocationConsumers: locationConsumers,
            UtcNow: utcNow);
    }

    private static bool IsIdleResultUnchanged(PrivacyScanResult? previous, PrivacyScanResult current)
    {
        if (previous == null) return false;
        return previous.MicrophoneCandidates.Count == 0 &&
               current.MicrophoneCandidates.Count == 0 &&
               !previous.CameraInUse &&
               !current.CameraInUse &&
               !previous.ScreenRecordingActive &&
               !current.ScreenRecordingActive &&
               previous.LocationConsumers.Count == 0 &&
               current.LocationConsumers.Count == 0;
    }

    private void PublishSnapshotToUi(PrivacyScanResult result, int generation)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || !_started || generation != _currentGeneration)
                return;

            if (IsIdleResultUnchanged(_lastPublishedScan, result) &&
                !CurrentState.AnyInUse &&
                _micFlowTask == null)
            {
                return;
            }
            _lastPublishedScan = result;
        }

        if (_dispatcher.HasShutdownStarted) return;

        void Apply()
        {
            lock (_lifecycleLock)
            {
                if (_disposed || !_started || generation != _currentGeneration)
                    return;
            }

            ApplyScanResult(result);
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
            // Camera, capture and location changes must not wait for a microphone
            // probe, which can fail or block independently of the registry scan.
            PublishState(CurrentState.MicrophoneInUse, MicrophoneFlowEvidence.Empty);
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
        if (_micFlowTask is { IsCompleted: false } && _micFlowCts is { IsCancellationRequested: false })
            return;

        if (_disposed || !_started) return;

        try { _micFlowCts?.Dispose(); } catch (ObjectDisposedException) { /* Already disposed */ }
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
            bool changed;
            PrivacyIndicatorState next;
            lock (_lifecycleLock)
            {
                if (_disposed || !_started || generation != _micFlowGeneration)
                    return;

                changed = TryUpdateState(microphoneInUse, evidence, out next);
            }

            // Raise outside _lifecycleLock so subscribers cannot deadlock against lifecycle calls.
            if (changed) StateChanged?.Invoke(this, next);
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
        if (TryUpdateState(microphoneInUse, evidence, out var next))
            StateChanged?.Invoke(this, next);
    }

    private bool TryUpdateState(bool microphoneInUse, MicrophoneFlowEvidence evidence, out PrivacyIndicatorState next)
    {
        next = new PrivacyIndicatorState(
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

        if (next.Equals(CurrentState)) return false;

        bool microphoneChanged = next.MicrophoneInUse != CurrentState.MicrophoneInUse;
        CurrentState = next;
        if (microphoneChanged)
        {
            RuntimeLog.Debug("PRIVACY-MIC", () =>
                $"visible={microphoneInUse} session={evidence.HasActiveSession} " +
                $"peak={evidence.PeakLevel:F4} consumers=[{string.Join(", ", _microphoneCandidateNames)}]");
        }
        return true;
    }



    private sealed record PrivacyScanResult(
        IReadOnlyList<CapabilityUsage> MicrophoneCandidates,
        IReadOnlyList<string> MicrophoneCandidateNames,
        IReadOnlyList<string> CameraConsumers,
        bool CameraInUse,
        bool ScreenRecordingActive,
        IReadOnlyList<string> LocationConsumers,
        DateTime UtcNow);

    internal sealed class CapabilityUsageCache(Func<string, IReadOnlyList<CapabilityUsage>> scanCapability)
    {
        private static readonly string[] CapabilityNames =
        {
            "microphone", "webcam", "graphicsCaptureProgrammatic", "graphicsCaptureWithoutBorder", "location"
        };
        private IReadOnlyList<CapabilityUsage>[]? _snapshot;
        private long _nextFullScan;

        internal IReadOnlyList<CapabilityUsage>[] GetSnapshot(bool registryChanged, long now)
        {
            bool fullScan = _snapshot == null || registryChanged || now >= _nextFullScan;
            if (!fullScan && !HasAnyUsages(_snapshot!))
                return _snapshot!;

            // Refresh open intervals each poll without repeatedly scanning idle
            // capabilities. Full scans still reconcile missed start notifications.
            var next = new IReadOnlyList<CapabilityUsage>[CapabilityNames.Length];
            for (int i = 0; i < next.Length; i++)
                next[i] = fullScan || _snapshot![i].Count > 0
                    ? scanCapability(CapabilityNames[i])
                    : _snapshot[i];

            _snapshot = next;
            if (fullScan)
                _nextFullScan = now + 30_000;

            return _snapshot;
        }

        private static bool HasAnyUsages(IReadOnlyList<CapabilityUsage>[] snapshot)
        {
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].Count > 0) return true;
            }
            return false;
        }
    }

    internal static IReadOnlyList<CapabilityUsage> GetRelevantConsumerUsages(
        IEnumerable<CapabilityUsage> usages,
        Func<string, bool> isRunning,
        Func<CapabilityUsage, bool>? additionalRule = null)
    {
        if (usages is IReadOnlyCollection<CapabilityUsage> { Count: 0 })
            return Array.Empty<CapabilityUsage>();

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
        if (latest == null) return Array.Empty<CapabilityUsage>();

        var result = new CapabilityUsage[latest.Count];
        latest.Values.CopyTo(result, 0);
        return result;
    }

    internal static IReadOnlyList<string> GetConsumerNames(IReadOnlyList<CapabilityUsage> usages)
    {
        int count = usages.Count;
        if (count == 0) return Array.Empty<string>();
        if (count == 1) return new[] { usages[0].DisplayName };

        var seen = new HashSet<string>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
            seen.Add(usages[i].DisplayName);

        var result = new string[seen.Count];
        seen.CopyTo(result);
        Array.Sort(result, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    internal static bool UpdateMicrophoneCandidateNames(
        IReadOnlyList<CapabilityUsage> candidates, ref string[] cachedNames)
    {
        bool changed = cachedNames.Length != candidates.Count;
        for (int i = 0; !changed && i < candidates.Count; i++)
            changed = !StringComparer.OrdinalIgnoreCase.Equals(cachedNames[i], candidates[i].RawName);

        if (!changed) return false;

        var names = candidates.Count == 0 ? Array.Empty<string>() : new string[candidates.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = candidates[i].RawName;
        cachedNames = names;
        return true;
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
        IReadOnlyList<CapabilityUsage> usages,
        ConsumerProcessProbe running,
        DateTime utcNow)
    {
        for (int i = 0; i < usages.Count; i++)
        {
            var usage = usages[i];
            if (running.IsRunning(usage.RawName) &&
                HasMinimumActiveDuration(usage.LastStartFileTime, utcNow, MinimumScreenRecordingDuration))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool HasMinimumActiveDuration(long startFileTime, DateTime utcNow, TimeSpan minimum)
    {
        if (vn_privacy_indicator_has_min_active_duration(startFileTime, utcNow.Ticks, minimum.Ticks, out byte active) == 0)
            return active != 0;
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

        ReadOnlySpan<char> procSpan = processName is not null ? processName.AsSpan() : ReadOnlySpan<char>.Empty;
        if (vn_privacy_indicator_is_ignored_mic_process(procSpan, procSpan.Length, out byte ignored) == 0 && ignored != 0)
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

    internal sealed class ConsumerProcessProbe : IDisposable
    {
        private sealed class ProcessConsumerKeyComparer : IEqualityComparer<(uint ProcessId, string RawConsumer)>
        {
            public static readonly ProcessConsumerKeyComparer Instance = new();
            public bool Equals((uint ProcessId, string RawConsumer) x, (uint ProcessId, string RawConsumer) y) =>
                x.ProcessId == y.ProcessId &&
                string.Equals(x.RawConsumer, y.RawConsumer, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((uint ProcessId, string RawConsumer) obj) =>
                HashCode.Combine(obj.ProcessId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.RawConsumer ?? string.Empty));
        }

        private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(uint ProcessId, string RawConsumer), bool> _processMatchCache = new(ProcessConsumerKeyComparer.Instance);
        private PrivacyProcessSnapshot? _processSnapshot;

        internal PrivacyProcessSnapshot GetProcessSnapshot()
            => _processSnapshot ??= PrivacyProcessSnapshot.Capture();

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

            var key = (processId, rawConsumer);
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

            _processSnapshot ??= PrivacyProcessSnapshot.Capture();
            for (int i = 0; i < _processSnapshot.Count; i++)
                if (_processSnapshot.MatchesExecutableName(i, processName) &&
                    IsDesktopExecutableProcess(executablePath, _processSnapshot.GetProcessId(i))) return true;
            return false;
        }

        internal static bool IsDesktopExecutableProcess(string executablePath, uint processId) =>
            Win32Interop.ProcessImagePathEquals(processId, executablePath);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string Family, long ExpireTicks)> _packageFamilyByPidCache = new();
        private const int MaxPackageFamilyPidCacheEntries = 256;

        private static void CachePackageFamilyForPid(uint processId, string family, long expireTicks, long now)
        {
            if (_packageFamilyByPidCache.Count >= MaxPackageFamilyPidCacheEntries &&
                !_packageFamilyByPidCache.ContainsKey(processId))
            {
                foreach (var kvp in _packageFamilyByPidCache)
                {
                    if (now >= kvp.Value.ExpireTicks)
                    {
                        _packageFamilyByPidCache.TryRemove(kvp.Key, out _);
                    }
                }
                if (_packageFamilyByPidCache.Count >= MaxPackageFamilyPidCacheEntries)
                {
                    _packageFamilyByPidCache.Clear();
                }
            }
            _packageFamilyByPidCache[processId] = (family, expireTicks);
        }

        private bool IsPackageFamilyRunning(string packageFamily)
        {
            if (string.IsNullOrWhiteSpace(packageFamily)) return false;

            _processSnapshot ??= PrivacyProcessSnapshot.Capture();
            for (int i = 0; i < _processSnapshot.Count; i++)
                if (IsPackageFamilyProcess(packageFamily, _processSnapshot.GetProcessId(i))) return true;
            return false;
        }

        public void Dispose() => _processSnapshot?.Dispose();

        private static bool IsPackageFamilyProcess(string packageFamily, uint processId)
        {
            if (string.IsNullOrWhiteSpace(packageFamily) || processId == 0) return false;

            long now = Environment.TickCount64;
            if (_packageFamilyByPidCache.TryGetValue(processId, out var cached) && now < cached.ExpireTicks)
            {
                return !string.IsNullOrEmpty(cached.Family) &&
                       string.Equals(cached.Family, packageFamily, StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                string? family = Win32Interop.GetProcessPackageFamilyName(processId, out int failure);
                if (family != null)
                {
                    CachePackageFamilyForPid(processId, family, now + 60000, now);
                    return string.Equals(family, packageFamily, StringComparison.OrdinalIgnoreCase);
                }

                bool openFailed = failure == Win32Interop.ProcessPackageFamilyOpenFailed;
                CachePackageFamilyForPid(processId, string.Empty, now + (openFailed ? 15000 : 30000), now);
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
    // Capture sessions, meters and endpoint notifications live in Native/Services/MicrophoneProbe.cpp. This class
    // keeps the topology-refresh schedule and decides which processes count as a consumer.
    private sealed unsafe partial class MicrophoneFlowProbe : IDisposable
    {

        [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_create")]
        private static partial int NativeCreate(nint callback, long context, out long handle, out int hresult);

        [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_destroy")]
        private static partial int NativeDestroy(long handle);

        [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_clear")]
        private static partial int NativeClear(long handle);

        [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_rebuild")]
        private static partial int NativeRebuild(long handle, out int count, out int hresult);

        [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_get_process_id")]
        private static partial int NativeGetProcessId(long handle, int index, out uint processId);

        [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_keep")]
        private static partial int NativeKeep(long handle, uint* processIds, int count, out int remaining);

        [LibraryImport(NativeDll, EntryPoint = "vn_mic_probe_sample")]
        private static partial int NativeSample(long handle, out byte hasActive, out float peak, out byte anyInvalid);

        private long _probeHandle;
        private GCHandle _self;
        private int _meterCount;
        private string[] _cachedCandidateNames = Array.Empty<string>();
        private bool _isTopologyValid;
        private int _deviceVersion;
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

                var (hasActiveSession, peakLevel, anyInvalid) = SampleLocked();
                // A session that expired, was muted or failed means the cached topology is stale.
                if (anyInvalid) Volatile.Write(ref _isTopologyValid, false);

                // If topology became invalid during sampling and we had no active session,
                // rebuild topology immediately if rate limit allows.
                if (!Volatile.Read(ref _isTopologyValid) && !hasActiveSession)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsedSec = (double)(now - _lastRebuildTicks) / Stopwatch.Frequency;
                    if (elapsedSec >= 1.0)
                    {
                        RebuildTopologyLocked(candidates);
                        var (rebuiltActive, rebuiltPeak, _) = SampleLocked();
                        hasActiveSession |= rebuiltActive;
                        if (rebuiltPeak > peakLevel) peakLevel = rebuiltPeak;
                    }
                }

                if (!float.IsFinite(peakLevel) || peakLevel < 0)
                    peakLevel = 0;

                return new MicrophoneFlowEvidence(
                    hasActiveSession,
                    Math.Clamp(peakLevel, 0, 1));
            }
        }

        private (bool HasActive, float Peak, bool AnyInvalid) SampleLocked()
        {
            if (_probeHandle == 0 || _meterCount == 0) return (false, 0f, false);
            return NativeSample(_probeHandle, out byte hasActive, out float peak, out byte anyInvalid) == 0
                ? (hasActive != 0, peak, anyInvalid != 0)
                : (false, 0f, true);
        }

        private void EnsureTopologyLocked(IReadOnlyList<CapabilityUsage> candidates)
        {
            if (UpdateMicrophoneCandidateNames(candidates, ref _cachedCandidateNames))
            {
                Volatile.Write(ref _isTopologyValid, false);
            }

            if (Volatile.Read(ref _isTopologyValid) && _meterCount > 0)
                return;

            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (double)(now - _lastRebuildTicks) / Stopwatch.Frequency;

            // If topology is invalid, or if we found 0 active sessions and at least 1s elapsed since last attempt:
            if (!Volatile.Read(ref _isTopologyValid) || (_meterCount == 0 && elapsedSec >= 1.0))
            {
                RebuildTopologyLocked(candidates);
            }
        }

        private void RebuildTopologyLocked(IReadOnlyList<CapabilityUsage> candidates)
        {
            _lastRebuildTicks = Stopwatch.GetTimestamp();
            int version = Volatile.Read(ref _deviceVersion);
            InvalidateCacheLocked();

            using var processProbe = new ConsumerProcessProbe();

            try
            {
                EnsureProbeLocked();
                if (_probeHandle == 0) return;

                if (NativeRebuild(_probeHandle, out int count, out int hresult) != 0)
                {
                    RuntimeLog.Warn("PRIVACY-MIC", $"Topology rebuild failed: 0x{hresult:X8}");
                    InvalidateCacheLocked();
                    DisposeProbeLocked();
                    return;
                }

                // Keep only sessions that belong to one of the consumers reported by the capability store.
                var keep = new List<uint>(Math.Max(0, count));
                for (int i = 0; i < count; i++)
                {
                    if (NativeGetProcessId(_probeHandle, i, out uint processId) != 0) continue;
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        if (processProbe.MatchesProcess(candidates[c].RawName, processId))
                        {
                            keep.Add(processId);
                            break;
                        }
                    }
                }

                if (keep.Count == 0)
                {
                    NativeKeep(_probeHandle, null, 0, out int remaining);
                    _meterCount = remaining;
                }
                else
                {
                    Span<uint> keepSpan = keep.Count <= 64 ? stackalloc uint[keep.Count] : new uint[keep.Count];
                    for (int i = 0; i < keep.Count; i++)
                        keepSpan[i] = keep[i];
                    fixed (uint* pinned = keepSpan)
                    {
                        NativeKeep(_probeHandle, pinned, keepSpan.Length, out int remaining);
                        _meterCount = remaining;
                    }
                }

                Volatile.Write(ref _isTopologyValid, Volatile.Read(ref _deviceVersion) == version);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("PRIVACY-MIC", $"Topology rebuild failed: {ex.Message}");
                InvalidateCacheLocked();
                DisposeProbeLocked();
            }
        }

        private void EnsureProbeLocked()
        {
            if (_probeHandle != 0) return;
            _self = GCHandle.Alloc(this);
            delegate* unmanaged[Stdcall]<long, void> callback = &OnNativeInvalidated;
            if (NativeCreate((nint)callback, GCHandle.ToIntPtr(_self), out long handle, out int hresult) != 0)
            {
                RuntimeLog.Warn("PRIVACY-MIC", $"Failed to register endpoint notification callback: 0x{hresult:X8}");
                _self.Free();
                return;
            }

            _probeHandle = handle;
        }

        // Runs on a Core Audio thread: only flag the topology as stale.
        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static void OnNativeInvalidated(long context)
        {
            try
            {
                if (GCHandle.FromIntPtr((IntPtr)context).Target is MicrophoneFlowProbe probe)
                    probe.OnDeviceNotification();
            }
            catch (Exception)
            {
                // Exceptions must not unwind into the native notification thread.
            }
        }

        private void OnDeviceNotification()
        {
            Interlocked.Increment(ref _deviceVersion);
            Volatile.Write(ref _isTopologyValid, false);
        }

        private void InvalidateCacheLocked()
        {
            Volatile.Write(ref _isTopologyValid, false);
            if (_probeHandle != 0) NativeClear(_probeHandle);
            _meterCount = 0;
        }

        public void Dispose()
        {
            lock (_probeLock)
            {
                InvalidateCacheLocked();
                DisposeProbeLocked();
            }
        }

        private void DisposeProbeLocked()
        {
            if (_probeHandle == 0) return;
            NativeDestroy(_probeHandle);
            _probeHandle = 0;
            if (_self.IsAllocated) _self.Free();
        }
    }
    internal sealed class MicrophoneActivityGate
    {
        private readonly float _signalThreshold;
        private readonly TimeSpan _holdDuration;
        private long _lastSignalUtcTicks = DateTime.MinValue.Ticks;

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
            long lastTicks = Interlocked.Read(ref _lastSignalUtcTicks);
            if (vn_privacy_indicator_evaluate_mic_gate(
                    hasCandidate ? (byte)1 : (byte)0,
                    hasActiveSession ? (byte)1 : (byte)0,
                    peakLevel,
                    _signalThreshold,
                    _holdDuration.Ticks,
                    utcNow.Ticks,
                    ref lastTicks,
                    out byte active) == 0)
            {
                Interlocked.Exchange(ref _lastSignalUtcTicks, lastTicks);
                return active != 0;
            }

            if (!hasCandidate || !hasActiveSession)
            {
                Reset();
                return false;
            }

            if (float.IsFinite(peakLevel) && peakLevel >= _signalThreshold)
            {
                lastTicks = utcNow.Ticks;
                Interlocked.Exchange(ref _lastSignalUtcTicks, lastTicks);
            }

            if (lastTicks == DateTime.MinValue.Ticks || utcNow.Ticks < lastTicks)
                return false;

            return (utcNow.Ticks - lastTicks) <= _holdDuration.Ticks;
        }

        public void Reset() => Interlocked.Exchange(ref _lastSignalUtcTicks, DateTime.MinValue.Ticks);
    }

    private static string NormalizeAppName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        ReadOnlySpan<char> rawSpan = raw.AsSpan();
        if (vn_privacy_indicator_normalize_app_name_slice(rawSpan, rawSpan.Length, out int start, out int len) == 0)
        {
            return start == 0 && len == raw.Length ? raw : raw.Substring(start, len);
        }

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
        for (int i = 0; i < MicrophoneConsumers.Count; i++)
            hash = HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(MicrophoneConsumers[i]));
        for (int i = 0; i < CameraConsumers.Count; i++)
            hash = HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(CameraConsumers[i]));
        for (int i = 0; i < LocationConsumers.Count; i++)
            hash = HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(LocationConsumers[i]));
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
