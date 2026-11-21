#pragma warning disable S6640 // Direct unmanaged memory pointers and DIB raster processing are required for high-performance real-time glass rendering

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

public sealed partial class LiquidGlassController : IDisposable
{
    private void WorkerLoop(int generation)
    {
        _lastBackdropWakeTicks = 0;
        _compositePublication = 0;
        var clock = Stopwatch.StartNew();
        double nextFrameAtMs = clock.Elapsed.TotalMilliseconds;
        double lastCaptureAtMs = double.NegativeInfinity;
        try
        {
            EnsureWaitableTimer();
            while (ShouldContinueWorker(generation))
            {
                try
                {
                    if (!ExecuteWorkerFrame(generation, clock, ref nextFrameAtMs, ref lastCaptureAtMs))
                        break;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Log(LogCategory, $"[{_logTag}] Capture worker interrupted: {ex.Message}");
                    if (ShouldContinueWorker(generation))
                    {
                        WaitForIdleOrEvent(250);
                        nextFrameAtMs = clock.Elapsed.TotalMilliseconds;
                    }
                }
            }
        }
        finally
        {
            CleanupWorkerResources(generation);
        }
    }

    private bool ShouldContinueWorker(int generation) =>
        _isActive && generation == Volatile.Read(ref _renderGeneration);

    private long _lastBackdropWakeTicks;

    internal bool ExecuteWorkerFrame(int generation, Stopwatch clock, ref double nextFrameAtMs, ref double lastCaptureAtMs)
    {
        double frameStart = clock.Elapsed.TotalMilliseconds;
        RefreshDisplayCadence();
        double activeIntervalMs = ChooseLockedFrameIntervalMs(Volatile.Read(ref _activeIntervalMs));
        double frameIntervalMs = ChooseFrameIntervalMs(activeIntervalMs, _idle, _animating,
            _hasUploadedGpuFrame || _hasPresentedCpuFrame, Environment.TickCount64 - Math.Max(_lastPresentTicks, _lastBackdropWakeTicks),
            ForceRefreshNeeded);
        // GPU work includes completion waits; they must not lower the requested cadence.
        frameIntervalMs = ChooseProcessingIntervalMs(frameIntervalMs, _frameWorkMs, _gpuMode);
        bool backedOff = frameIntervalMs > activeIntervalMs;
        if (_gpuMode)
        {
            if (!_presentationPaused && ((!backedOff && activeIntervalMs < 100) || _d3dPresenter?.HasPendingFrame == true))
                RequestRenderTimerPeriod();
            else
                ReleaseRenderTimerPeriod();
        }

        // Interaction wakes resume active cadence without bypassing its cap.
        double remainingMs = lastCaptureAtMs + (_gpuMode ? frameIntervalMs : activeIntervalMs) - frameStart;
        if (remainingMs > 0)
        {
            if (_gpuMode && _lastGpuWasExactWindows && !WindowAnimationPolicy.InvisibleMode &&
                backedOff && !_presentationPaused &&
                _d3dPresenter is { } presenter &&
                presenter.TryWaitForBackdrop(_idleWakeEvent.SafeWaitHandle, (int)Math.Ceiling(remainingMs), out bool arrived))
            {
                // Resume normal pacing long enough to consume and validate the
                // arriving texture, even for a single change followed by silence.
                if (arrived) _lastBackdropWakeTicks = Environment.TickCount64;
                return ShouldContinueWorker(generation);
            }
            SleepUntilRenderDeadline(remainingMs);
            return ShouldContinueWorker(generation);
        }

        if (ApplyCapturePolicy())
            return true;

        if (Interlocked.Exchange(ref _captureAffinityFlushPending, 0) != 0)
        {
            try { DwmFlush(); } catch { /* affinity was already applied */ }
        }

        if (IsPresentationBlocked(frameIntervalMs, ref nextFrameAtMs, clock))
            return true;

        // A busy UI has not consumed another capture slot. Resume as soon as it
        // presents the pending frame, preserving the previous capture deadline.
        if (_gpuMode && _d3dPresenter?.HasPendingFrame == true)
        {
            _waitingForGpuPresentation = true;
            try
            {
                if (_d3dPresenter?.HasPendingFrame == true)
                    WaitForIdleOrEvent((int)Math.Ceiling(frameIntervalMs));
            }
            finally { _waitingForGpuPresentation = false; }
            return ShouldContinueWorker(generation);
        }

        CaptureRegion? region = GetRegionCached(_animating, frameStart);
        if (!ShouldContinueWorker(generation)) return false;

        if (region == null)
        {
            ReleaseRenderTimerPeriod();
            WaitForIdleOrEvent(200);
            nextFrameAtMs = clock.Elapsed.TotalMilliseconds;
            return true;
        }

        double targetIntervalMs = _gpuMode ? frameIntervalMs : activeIntervalMs;
        double scheduledStartMs = lastCaptureAtMs + targetIntervalMs;
        lastCaptureAtMs = double.IsFinite(scheduledStartMs)
            ? Math.Max(frameStart - 0.5, scheduledStartMs)
            : frameStart;
        TryRenderRegion(region.Value, generation);
        if (!ShouldContinueWorker(generation)) return false;

        TrackDiagnostics(frameStart);
        double nowMs = clock.Elapsed.TotalMilliseconds;
        if (_gpuMode)
        {
            // The start-to-start cap already paces GPU frames, including slow handoffs.
            _frameWorkMs = 0;
            nextFrameAtMs = nowMs;
        }
        else
        {
            double workMs = nowMs - frameStart;
            _frameWorkMs = _frameWorkMs == 0 ? workMs : _frameWorkMs * 0.8 + workMs * 0.2;
            nextFrameAtMs = AdvanceFrameDeadline(nextFrameAtMs, frameIntervalMs, nowMs);
        }
        return true;
    }

    private bool IsPresentationBlocked(double frameIntervalMs, ref double nextFrameAtMs, Stopwatch clock)
    {
        if (!_gpuMode && _presentInFlight)
        {
            // Do not keep capturing/uploading over a frame the UI has not consumed.
            WaitForIdleOrEvent(2);
            nextFrameAtMs = clock.Elapsed.TotalMilliseconds;
            return true;
        }

        if (_presentationPaused)
        {
            WaitForIdleOrEvent((int)Math.Max(16, frameIntervalMs));
            nextFrameAtMs = clock.Elapsed.TotalMilliseconds;
            return true;
        }

        return false;
    }

    private void TrackDiagnostics(double frameStart)
    {
        _dbgFrameCount++;
        double sinceLastLog = frameStart - _dbgLastLogMs;
        if (sinceLastLog >= 5000.0)
            LogPeriodicFpsDiagnostics(frameStart, sinceLastLog);
    }

    private void CleanupWorkerResources(int generation)
    {
        // Start can arrive before the old worker's finally block. Reset all
        // worker-owned frame state here before permitting its replacement.
        if (generation == Volatile.Read(ref _renderGeneration))
        {
            _hasUploadedGpuFrame = false;
            _hasLastSubmittedGpuFrame = false;
            _hasPresentedCpuFrame = false;
            _cachedRegion = null;
            _lastRegionFetchMs = double.NegativeInfinity;
            _presentInFlight = false;
        }
        _fullSurfaceCaptureBounds = null;
        _dbgFrameCount = 0;
        _dbgLastLogMs = 0;
        _frameWorkMs = 0;
        _frameHistory.Clear();
        ReleaseGdiResources();
        _outBuffer = Array.Empty<byte>();
        _refractMap = Array.Empty<int>();
        _outW = _outH = _srcW = _srcH = _margin = 0;
        ReleaseRenderTimerPeriod();
        DisposeWaitableTimer();
        _sourceBlurBuffer = _sourceBlurTmp = Array.Empty<byte>();
        Volatile.Write(ref _worker, null);
        if (Volatile.Read(ref _disposed) != 0)
        {
            try { _idleWakeEvent.Dispose(); }
            catch { /* best-effort handle cleanup */ }
            return;
        }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
            {
                StartWorkerIfNeeded();
            }));
        }
        catch
        {
            // Dispatcher shutdown does not need a replacement worker.
        }
    }

    private bool ApplyCapturePolicy()
    {
        if (!WindowAnimationPolicy.InvisibleMode)
        {
            _exactBitBltCapture = false;
            SetWindowDisplayAffinitySafe(WDA_NONE);
            // Magnifier excludes our windows from its own capture, independently of
            // global display affinity. Screenshots and recordings can always see us.
            if (_mag == null) RequestMagnifierFallback();
            return false;
        }
        if (_gpuMode)
        {
            // Desktop Duplication needs our top-level window excluded to avoid feedback.
            _exactBitBltCapture = SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
            if (!_exactBitBltCapture)
            {
                OnD3DPresenterFailed(new InvalidOperationException("Desktop capture exclusion could not be applied."));
                WaitForIdleOrEvent(100);
                return true;
            }
            return false;
        }
        if (!_exactBitBltCapture)
        {
            _exactBitBltCapture = SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
            if (!_exactBitBltCapture && _mag == null) RequestMagnifierFallback();
        }
        return false;
    }

    private void RequestMagnifierFallback()
    {
        if (Environment.TickCount64 < _nextMagnifierRetryTicks ||
            Interlocked.CompareExchange(ref _magnifierInitQueued, 1, 0) != 0) return;
        int generation = Volatile.Read(ref _renderGeneration);
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                try
                {
                    if (!_isActive || _exactBitBltCapture || _mag != null ||
                        generation != Volatile.Read(ref _renderGeneration)) return;
                    AcquireMagnifier();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier fallback init failed: {ex.Message}");
                }
                finally
                {
                    _nextMagnifierRetryTicks = Environment.TickCount64 + 3000;
                    Interlocked.Exchange(ref _magnifierInitQueued, 0);
                }
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _magnifierInitQueued, 0);
        }
    }

    private void TryRenderRegion(CaptureRegion region, int generation)
    {
        try
        {
            if (ProcessFrame(region, generation))
                Present(generation);
        }
        catch (Exception ex)
        {
            _presentInFlight = false;
            RuntimeLog.Log(LogCategory, $"Render failed: {ex.Message}");
        }
    }

    private void LogPeriodicFpsDiagnostics(double frameStart, double sinceLastLog)
    {
        int presented = Interlocked.Exchange(ref _dbgPresentCount, 0);
        double loopFps = _dbgFrameCount * 1000.0 / sinceLastLog;
        double targetFps = 1000.0 / _activeIntervalMs;
        BackdropOptics optics = CurrentBackdropOptics;
        var outside = OutsideBackdropColor;
        GpuGeometry lastGeom;
        bool hasGeom;
        lock (_sync)
        {
            hasGeom = _hasPresentedGpuGeometry;
            lastGeom = _lastPresentedGpuGeometry;
        }
        string geometry = hasGeom
            ? $" src={lastGeom.SrcW:F0}x{lastGeom.SrcH:F0}" +
              $" notch={lastGeom.NotchW:F0}x{lastGeom.NotchH:F0}" +
              $" off={lastGeom.OffX:F1},{lastGeom.OffY:F1}" +
              $" origin={lastGeom.CaptureOriginX},{lastGeom.CaptureOriginY}"
            : string.Empty;
        RuntimeLog.Log(LogCategory,
            $"[{_logTag}] target={targetFps:F0} loop={loopFps:F1} presented={presented * 1000.0 / sinceLastLog:F1} fps " +
            $"renderer={(_gpuMode ? "GPU" : "CPU")} backdrop=rgb({optics.Red},{optics.Green},{optics.Blue})" +
            $" outside=rgb({outside.Red},{outside.Green},{outside.Blue})" +
            geometry);
        if (_gpuMode && _d3dPresenter is { } presenter)
        {
            var stats = presenter.TakeStatistics();
            RuntimeLog.Log(LogCategory,
                $"[{_logTag}] GPU capture={presenter.TakeCaptureCount() * 1000.0 / sinceLastLog:F1} fps submit={stats.Frames * 1000.0 / sinceLastLog:F1} fps " +
                $"upload-to-submit={stats.MeanQueueMs:F2} ms (excludes capture/display latency); {presenter.CaptureStatus}");
        }
        _dbgFrameCount = 0;
        _dbgLastLogMs = frameStart;
    }

    private double AdvanceFrameDeadline(double nextFrameAtMs, double frameIntervalMs, double nowMs)
    {
        // A wake during an idle wait must not leave a future idle deadline in
        // front of the first animation frames.
        nextFrameAtMs = ComputeNextFrameDeadline(nextFrameAtMs, frameIntervalMs, nowMs);
        if (nextFrameAtMs <= nowMs)
        {
            // Discard timing debt and yield instead of running continuously when
            // overloaded. Input/render work needs time to drain as well.
            double recoveryDelayMs = Math.Max(2, Math.Min(16, frameIntervalMs * 0.2));
            SleepUntilRenderDeadline(recoveryDelayMs);
            return nowMs + recoveryDelayMs;
        }
        if (nextFrameAtMs > nowMs)
        {
            SleepUntilRenderDeadline(nextFrameAtMs - nowMs);
        }
        return nextFrameAtMs;
    }

    internal static double ComputeNextFrameDeadline(double previousDeadlineMs, double intervalMs, double nowMs) =>
        Math.Min(previousDeadlineMs + intervalMs, nowMs + intervalMs);

    private CaptureRegion? _cachedRegion;
    private double _lastRegionFetchMs = double.NegativeInfinity;
    // The collapsed notch does not move between layout changes. Avoid a blocking
    private const double IdleRegionRefreshMs = 1000.0;

    private readonly object _liveRegionSync = new();
    private CaptureRegion? _liveRegion;
    private bool _hasLiveRegion;
    private long _liveRegionRevision;

    public void SetLiveRegion(CaptureRegion? region)
    {
        lock (_liveRegionSync)
        {
            // Ignore duplicate region updates per frame to preserve capture pacing
            // without unnecessary wakeups when geometry is unchanged.
            if (_hasLiveRegion && _liveRegion == region) return;
            _liveRegion = region;
            _hasLiveRegion = true;
            _liveRegionRevision++;
            _regionQueryCompleted = false;
        }
        SignalWakeEventSafe();
    }

    public void ClearLiveRegion()
    {
        lock (_liveRegionSync)
        {
            if (_hasLiveRegion && _liveRegion != null)
                _cachedRegion = _liveRegion;
            _liveRegion = null;
            _hasLiveRegion = false;
            _liveRegionRevision++;
            _regionQueryCompleted = false;
            _lastRegionFetchMs = double.NegativeInfinity;
        }
    }

    private CaptureRegion? GetRegionCached(bool animating, double nowMs)
    {
        lock (_liveRegionSync)
        {
            if (_regionQueryCompleted && _queriedRegionGeneration == Volatile.Read(ref _renderGeneration))
            {
                _cachedRegion = _queriedRegion;
                _lastRegionFetchMs = nowMs;
                _regionQueryCompleted = false;
            }
            if (_hasLiveRegion)
            {
                _cachedRegion = _liveRegion;
                _lastRegionFetchMs = nowMs;
                return _cachedRegion;
            }
        }

        double minInterval = animating ? 16.0 : IdleRegionRefreshMs;
        if (_cachedRegion == null || nowMs - _lastRegionFetchMs >= minInterval)
        {
            _cachedRegion = TryGetRegionOnUi();
            _lastRegionFetchMs = nowMs;
        }
        return _cachedRegion;
    }

    private void WaitForIdleOrEvent(int timeoutMs)
    {
        if (!_isActive) return;
        try
        {
            _idleWakeEvent.WaitOne(Math.Max(1, timeoutMs));
        }
        catch (ThreadAbortException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best effort wait; ignore interruption
        }
    }

    internal int RenderTimerPeriodRequested => Volatile.Read(ref _renderTimerPeriodRequested);
    internal WaitHandle WorkerWake => _idleWakeEvent;
    internal void ResetWorkerWake() => _idleWakeEvent.Reset();
    internal void SetWaitingForPresentation(bool waiting) => _waitingForGpuPresentation = waiting;
    internal void ConfigureGpuForTest(bool enabled, D3DImageFramePresenter? presenter = null)
    {
        _gpuMode = enabled;
        _d3dPresenter = presenter;
    }

    internal void SetActiveForTest(bool active) => _isActive = active;
    internal void EnsureWaitableTimerForTest() => EnsureWaitableTimer();
    internal void DisposeWaitableTimerForTest() => DisposeWaitableTimer();

    private void EnsureWaitableTimer()
    {
        if (_waitableTimer != null && !_waitableTimer.IsInvalid && !_waitableTimer.IsClosed)
            return;

        try
        {
            _waitableTimer = CreateHighResolutionTimer();

            if (_waitableTimer != null && !_waitableTimer.IsInvalid)
            {
#pragma warning disable S3884, S3869 // Raw handles required for Win32 WaitForMultipleObjects
                _renderWaitHandles = new IntPtr[2]
                {
                    _idleWakeEvent.SafeWaitHandle.DangerousGetHandle(),
                    _waitableTimer.DangerousGetHandle()
                };
#pragma warning restore S3884, S3869
            }
            else
            {
                _waitableTimer = null;
                _renderWaitHandles = null;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"High-resolution waitable timer creation failed: {ex.Message}");
            _waitableTimer = null;
            _renderWaitHandles = null;
        }
    }

    private void DisposeWaitableTimer()
    {
        try
        {
            if (_waitableTimer != null && !_waitableTimer.IsClosed)
            {
                CancelWaitableTimer(_waitableTimer);
                _waitableTimer.Dispose();
            }
        }
        catch
        {
            // Best effort disposal
        }
        finally
        {
            _waitableTimer = null;
            _renderWaitHandles = null;
        }
    }

    internal void SleepUntilRenderDeadline(double milliseconds)
    {
        long targetTicks = Stopwatch.GetTimestamp() + StopwatchTicksFromMilliseconds(Math.Max(0.1, milliseconds));

        bool isAnimating = _animating;

        while (_isActive)
        {
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return;

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;

            if (!isAnimating)
            {
                // Prioritize CPU/battery savings in steady state with zero spin wait,
                // maintaining precise sleep intervals to prevent capture cadence jitter.
                if (!WaitDeadlineSlice(remainingMs)) return;
            }
            else
            {
                // When actively animating, prioritize smooth pacing while eliminating the long spin loop.
                // High-resolution waitable timer sleeps down to ~0.15 ms before deadline.
                if (remainingMs > 0.35)
                {
                    double waitMs = remainingMs - 0.15;
                    if (!WaitDeadlineSlice(waitMs))
                    {
                        return;
                    }
                    continue;
                }

                // Final slice (<= 0.35 ms): yield to OS scheduler instead of busy spinning GPU/CPU threads.
                Thread.Yield();
            }
        }
    }

    private bool WaitDeadlineSlice(double waitMs)
    {
        if (waitMs <= 0.05 || !_isActive) return false;

        var timer = _waitableTimer;
        var handles = _renderWaitHandles;

        if (timer != null && !timer.IsInvalid && !timer.IsClosed && handles != null && SetWaitableTimerRelative(timer, waitMs))
        {
            // One-shot relative due time derived from the requested wait.
            if (!_gpuMode && Volatile.Read(ref _renderTimerPeriodRequested) != 0)
                ReleaseRenderTimerPeriod();
            uint timeout = (uint)Math.Ceiling(waitMs) + 100;
            int waitResult = WaitForAnyOfTwo(handles[0], handles[1], timeout);

            if (waitResult == WAIT_OBJECT_0)
            {
                // _idleWakeEvent was signaled (e.g. Stop, layout change, SetAnimating)
                return false;
            }

            return true;
        }

        // Fallback when high-resolution waitable timer is not available:
        // Wait on _idleWakeEvent with timeout to remain interruptible.
        RequestRenderTimerPeriod();
        int fallbackMs = (int)Math.Max(1, Math.Round(waitMs));
        bool signaled = _idleWakeEvent.WaitOne(fallbackMs);
        if (signaled)
        {
            return false;
        }

        return true;
    }

    private static long StopwatchTicksFromMilliseconds(double milliseconds) =>
        Math.Max(1, (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0));

    private int _regionQueryQueued;
    private CaptureRegion? _queriedRegion;
    private int _queriedRegionGeneration = -1;
    private bool _regionQueryCompleted;

}
