#pragma warning disable S6640 // Direct unmanaged memory pointers and DIB raster processing are required for high-performance real-time glass rendering

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

public sealed class LiquidGlassController
{
    public const int MaxTargetFps = 240;

    public readonly record struct CaptureRegion(int X, int Y, int Width, int Height,
        double TopCornerRadiusDip = 0, double BottomCornerRadiusDip = 0,
        double SubX = 0, double SubY = 0);

    public struct GlassParams
    {
        public double PowerFactor { get; set; }
        public double RefractionA { get; set; }
        public double RefractionB { get; set; }
        public double RefractionC { get; set; }
        public double RefractionD { get; set; }
        public double FPower { get; set; }
        public double Noise { get; set; }
        public double GlowWeight { get; set; }
        public double GlowBias { get; set; }
        public double GlowEdge0 { get; set; }
        public double GlowEdge1 { get; set; }
        public double Refraction { get; set; }
        public double EdgeBend { get; set; }
        public double ChromaticAberration { get; set; }
        public double Distortion { get; set; }
        public double ZRadius { get; set; }
        public double Saturation { get; set; }
        public double Brightness { get; set; }
        public int BevelMode { get; set; }
        public double TopCornerRadius { get; set; }
        public double BottomCornerRadius { get; set; }

        public static GlassParams Default => new()
        {
            PowerFactor = 3.0,
            RefractionA = 0.7,
            RefractionB = 2.3,
            RefractionC = 5.2,
            RefractionD = 6.9,
            FPower = 1.0,
            Noise = 0.06,
            GlowWeight = 0.25,
            GlowBias = 0.0,
            GlowEdge0 = 0.15,
            GlowEdge1 = 0.0,
            Refraction = 1.0,
            EdgeBend = 1.65,
            ChromaticAberration = 0.56,
            Distortion = 0.32,
            ZRadius = 0.23,
            Saturation = 0.15,
            Brightness = -0.05,
            BevelMode = 0,
            TopCornerRadius = 0.0,
            BottomCornerRadius = 20.0
        };
    }

    private const string LogCategory = "LIQUIDGLASS";
    private const int MaxWidth = 1600;
    private const int MaxHeight = 600;
    private const int GpuSamplingMarginLimit = 160;
    // Largest capture region this instance will process/present. Defaults to the
    private readonly int _maxRegionW;
    private readonly int _maxRegionH;

    private readonly Image _host;
    private readonly Dispatcher _dispatcher;
    private readonly Func<IntPtr> _getHwnd;
    private readonly Func<CaptureRegion?> _regionProvider;

    private double _activeIntervalMs;
    private volatile bool _animating;
    private volatile bool _presentationPaused;

    private readonly object _sync = new();
    private GlassParams _params = GlassParams.Default;
    private int _blurSigma;
    private int[] _blurPassRadii = [0, 0, 0];
    private volatile bool _mapsDirty;

    private Thread? _worker;
    private int _renderGeneration;
    private bool _hasVisibleFrame;
    private volatile bool _isActive;
    private volatile bool _presentInFlight;
    private const uint RenderTimerPeriodMs = 1;
    private int _renderTimerPeriodRequested;

    // Owned exclusively by the worker thread once Start() runs.
    private WriteableBitmap? _bitmap;
    private TranslateTransform? _hostTransform;
    private double _presentSubX, _presentSubY;
    private byte[] _outBuffer = Array.Empty<byte>();
    private byte[] _blurTmp = Array.Empty<byte>();
    private byte[] _sourceBlurBuffer = Array.Empty<byte>();
    private byte[] _sourceBlurTmp = Array.Empty<byte>();

    private IntPtr _memDc;
    private IntPtr _dibBmp;
    private IntPtr _dibBits;
    private IntPtr _oldBmp;
    private int _dibW, _dibH;

    private IntPtr _stagingDc;
    private IntPtr _stagingBmp;
    private IntPtr _stagingBits;
    private IntPtr _stagingOldBmp;
    private int _stagingW, _stagingH;

    private int[] _idxR = Array.Empty<int>();
    private int[] _auxR = Array.Empty<int>();
    private int[] _idxG = Array.Empty<int>();
    private int[] _auxG = Array.Empty<int>();
    private int[] _idxB = Array.Empty<int>();
    private int[] _auxB = Array.Empty<int>();

    private byte[] _edgeMask = Array.Empty<byte>();

    private int _outW, _outH;
    private int _srcW, _srcH;
    private int _margin;
    private double _outScale = 1.0;
    private double _mapTopCornerRadius = -1;
    private double _mapBottomCornerRadius = -1;
    private double _mapZRadius = -1;
    private int _mapNotchW = -1, _mapNotchH = -1, _mapNotchOffX = -1, _mapNotchOffY = -1;
    private int _mapCaptureShiftX = int.MinValue, _mapCaptureShiftY = int.MinValue;

    private MagnifierCaptureSource? _mag;
    private bool _magReady;
    private int _magFailStreak = 0;

    private bool _hideFromCapture;
    private volatile bool _exactBitBltCapture;
    private int _currentDisplayAffinity = -1;
    public bool HideFromScreenCapture
    {
        get => _hideFromCapture;
        set
        {
            if (_hideFromCapture == value) return;
            _hideFromCapture = value;
            if (_isActive)
                SetWindowDisplayAffinitySafe(WDA_NONE);
        }
    }

    private double _bitmapDpi = 96;

    private IntPtr _fgProbeHwnd;
    private bool _fgProbeResult;

    private volatile bool _gpuMode;
    private int _averageBackgroundBrightnessInt = 128;
    public int AverageBackgroundBrightnessInt
    {
        get => Volatile.Read(ref _averageBackgroundBrightnessInt);
        set => Volatile.Write(ref _averageBackgroundBrightnessInt, value);
    }
    public double AverageBackgroundBrightness => AverageBackgroundBrightnessInt / 255.0;
    private int _averageBackgroundColorRgb = 0x808080;
    private int _outsideBackdropColorRgb = 0x808080;
    private int _backgroundLightX1000;
    private int _backgroundLightY1000;
    private int _backgroundContrast1000;

    public readonly record struct BackdropOptics(
        byte Red, byte Green, byte Blue,
        double LightX, double LightY, double Contrast);

    public BackdropOptics CurrentBackdropOptics
    {
        get
        {
            int rgb = Volatile.Read(ref _averageBackgroundColorRgb);
            return new BackdropOptics(
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb,
                Volatile.Read(ref _backgroundLightX1000) / 1000.0,
                Volatile.Read(ref _backgroundLightY1000) / 1000.0,
                Volatile.Read(ref _backgroundContrast1000) / 1000.0);
        }
    }

    private (byte Red, byte Green, byte Blue) OutsideBackdropColor
    {
        get
        {
            int rgb = Volatile.Read(ref _outsideBackdropColorRgb);
            return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
    }

    public int LastPresentedCaptureOriginX => _hasPresentedGpuGeometry ? _lastPresentedGpuGeometry.CaptureOriginX : int.MinValue;
    public int LastPresentedCaptureOriginY => _hasPresentedGpuGeometry ? _lastPresentedGpuGeometry.CaptureOriginY : int.MinValue;

    public readonly record struct GpuGeometry(
        double SrcW, double SrcH, double NotchW, double NotchH, double OffX, double OffY,
        double TopCornerR, double BottomCornerR, double PowerFactor, double A, double B,
        double C, double D, double FPower, double Noise, double GlowWeight, double GlowBias,
        double GlowEdge0, double GlowEdge1, double Chroma, double EdgeBend, double SatFactor,
        double BrightAdd, double BevelMode,
        int CaptureOriginX = 0, int CaptureOriginY = 0);
    private Action<GpuGeometry>? _onGpuGeometry;
    private Action<Exception>? _onGpuFailure;
    private GpuGeometry _lastPresentedGpuGeometry;
    private bool _hasPresentedGpuGeometry;
    private D3DImageFramePresenter? _d3dPresenter;
    private int _gpuFailureSignaled;

    // Unchanged-frame suppression. Re-presenting an identical backdrop is a pure
    private const int UnchangedRepresentIntervalMs = 500;
    private ulong _lastCaptureHash;
    private long _lastPresentTicks;
    private bool _hasUploadedGpuFrame;
    private GpuGeometry _lastUploadedGpuGeometry;
    private bool _hasPresentedCpuFrame;
    private double _lastPresentedSubX, _lastPresentedSubY;
    private double _lastPresentedSaturation, _lastPresentedBrightness;

    public bool SetGpuMode(bool enabled, Action<GpuGeometry>? onGeometry, Action<Exception>? onFailure = null)
    {
        _onGpuGeometry = onGeometry;
        _onGpuFailure = onFailure;
        if (!enabled)
        {
            _gpuMode = false;
            Interlocked.Exchange(ref _gpuFailureSignaled, 0);
            ResetGpuGeometryTracking();
            DisposeGpuPresenter();
            _mapsDirty = true;
            return true;
        }

        Interlocked.Exchange(ref _gpuFailureSignaled, 0);
        ResetGpuGeometryTracking();
        if (!TryEnableGpuPresenter(out Exception? error))
        {
            _gpuMode = false;
            _mapsDirty = true;
            RuntimeLog.Log(LogCategory, $"D3DImage presenter unavailable; using CPU fallback: {error?.Message}");
            return false;
        }

        _gpuMode = true;
        _mapsDirty = true;
        return true;
    }

    private bool TryEnableGpuPresenter(out Exception? error)
    {
        try
        {
            if (_dispatcher.CheckAccess())
                EnsureGpuPresenterOnDispatcher();
            else
                _dispatcher.Invoke(EnsureGpuPresenterOnDispatcher, DispatcherPriority.Send, CancellationToken.None);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            DisposeGpuPresenter();
            return false;
        }
    }

    private void EnsureGpuPresenterOnDispatcher()
    {
        if (_d3dPresenter == null)
        {
            IntPtr hwnd = _getHwnd();
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("A window handle is required for the D3DImage presenter.");

            // Use one fixed presentation surface for the full supported region
            _d3dPresenter = new D3DImageFramePresenter(
                _dispatcher,
                hwnd,
                _maxRegionW + GpuSamplingMarginLimit * 2,
                _maxRegionH + GpuSamplingMarginLimit * 2);
            _d3dPresenter.FramePresented += OnD3DFramePresented;
            _d3dPresenter.Failed += OnD3DPresenterFailed;
        }

        _host.Stretch = Stretch.Fill;
        _host.HorizontalAlignment = HorizontalAlignment.Left;
        _host.VerticalAlignment = VerticalAlignment.Top;
        double dpi = _bitmapDpi > 0 ? _bitmapDpi / 96.0 : 1.0;
        _host.Width = SurfaceWidth / dpi;
        _host.Height = SurfaceHeight / dpi;
        _host.RenderTransform = null;
        RenderOptions.SetBitmapScalingMode(_host, BitmapScalingMode.HighQuality);
        // Do not expose the D3DImage until its first captured frame has been
    }

    private void DisposeGpuPresenter()
    {
        if (_d3dPresenter == null)
            return;

        try
        {
            if (_dispatcher.CheckAccess())
                DisposeGpuPresenterOnDispatcher();
            else
                _dispatcher.Invoke(DisposeGpuPresenterOnDispatcher, DispatcherPriority.Send, CancellationToken.None);
        }
        catch
        {
            _d3dPresenter = null;
        }
    }

    private void DisposeGpuPresenterOnDispatcher()
    {
        var presenter = _d3dPresenter;
        if (presenter == null)
            return;

        _d3dPresenter = null;
        presenter.FramePresented -= OnD3DFramePresented;
        presenter.Failed -= OnD3DPresenterFailed;
        if (ReferenceEquals(_host.Source, presenter.ImageSource))
            _host.Source = null;
        presenter.Dispose();
        ResetGpuGeometryTracking();
    }

    private void ResetGpuGeometryTracking()
    {
        _lastPresentedGpuGeometry = default;
        _hasPresentedGpuGeometry = false;
        _hasUploadedGpuFrame = false;
        _hasPresentedCpuFrame = false;
        _hasVisibleFrame = false;
    }

    private void OnD3DFramePresented(object? tag)
    {
        if (!_isActive) return;
        _hasVisibleFrame = true;
        Interlocked.Increment(ref _dbgPresentCount);

        if (tag is GpuGeometry geom &&
            (!_hasPresentedGpuGeometry || !_lastPresentedGpuGeometry.Equals(geom)))
        {
            _lastPresentedGpuGeometry = geom;
            _hasPresentedGpuGeometry = true;
            _onGpuGeometry?.Invoke(geom);
        }

        // Geometry is applied before the initialized source becomes visible so
        var presenter = _d3dPresenter;
        if (_gpuMode && presenter != null &&
            !ReferenceEquals(_host.Source, presenter.ImageSource))
        {
            _host.Source = presenter.ImageSource;
        }
    }

    public ImageSource? ImageSource => _d3dPresenter?.ImageSource;

    // Morphing windows can reveal pixels beyond the previous frame's small
    // bounds before capture catches up. Fill a stable envelope for those hosts.
    public bool CaptureFullSurface { get; set; }

    public int MaxRegionWidth => _maxRegionW;
    public int MaxRegionHeight => _maxRegionH;
    public int SurfaceWidth => _maxRegionW + GpuSamplingMarginLimit * 2;
    public int SurfaceHeight => _maxRegionH + GpuSamplingMarginLimit * 2;

    private void OnD3DPresenterFailed(Exception ex)
    {
        if (Interlocked.Exchange(ref _gpuFailureSignaled, 1) != 0)
            return;

        _gpuMode = false;
        _mapsDirty = true;

        void NotifyFailure()
        {
            try { _onGpuFailure?.Invoke(ex); }
            catch { /* fallback must not take down the UI thread */ }
            if (_mag == null)
            {
                try
                {
                    IntPtr h = _getHwnd();
                    _mag = MagnifierCaptureSource.AcquireShared(h);
                    _magReady = _mag.IsReady;
                }
                catch (Exception magEx)
                {
                    RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier init failed: {magEx.Message}");
                    _magReady = false;
                }
            }
            if (_onGpuFailure == null)
                DisposeGpuPresenter();
        }

        try
        {
            if (_dispatcher.CheckAccess())
                NotifyFailure();
            else
                _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)NotifyFailure);
        }
        catch
        {
            DisposeGpuPresenter();
        }
    }

    private int _dbgFrameCount;
    private int _dbgPresentCount;
    private double _dbgLastLogMs;

    public LiquidGlassController(Image host, Func<IntPtr> getHwnd, Func<CaptureRegion?> regionProvider,
        int activeFps = 60, string logTag = "GLASS",
        int maxRegionWidth = MaxWidth, int maxRegionHeight = MaxHeight)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _getHwnd = getHwnd ?? throw new ArgumentNullException(nameof(getHwnd));
        _regionProvider = regionProvider ?? throw new ArgumentNullException(nameof(regionProvider));
        _dispatcher = host.Dispatcher;
        _logTag = logTag;
        _maxRegionW = Math.Clamp(maxRegionWidth, 64, 4096);
        _maxRegionH = Math.Clamp(maxRegionHeight, 64, 4096);

        int target = (activeFps <= 0 || activeFps == 60) ? AnimationConfig.TargetFps : activeFps;
        int active = Math.Clamp(target, AnimationConfig.MinFps, MaxTargetFps);
        _activeIntervalMs = 1000.0 / active;
    }

    private readonly string _logTag;

    public void UpdateFps(int activeFps)
    {
        int target = (activeFps <= 0 || activeFps == 60) ? AnimationConfig.TargetFps : activeFps;
        int active = Math.Clamp(target, AnimationConfig.MinFps, MaxTargetFps);
        Volatile.Write(ref _activeIntervalMs, 1000.0 / active);
    }

    internal static double ChooseLockedFrameIntervalMs(double configuredIntervalMs) =>
        Math.Max(1000.0 / MaxTargetFps, configuredIntervalMs);

    public bool IsActive => _isActive;

    public void SetAnimating(bool animating) => _animating = animating;

    /// <summary>
    /// Keeps acquiring the desktop at the configured cadence while holding the
    /// last completed visual frame. This lets short WPF hover transforms reuse one
    /// stable GPU texture instead of invalidating the layered window every 8 ms.
    /// </summary>
    public void SetPresentationPaused(bool paused)
    {
        bool wasPaused = _presentationPaused;
        _presentationPaused = paused;
        if (wasPaused && !paused)
        {
            // The held frame used the pre-hover capture rectangle. Force the next
            Volatile.Write(ref _lastRegionFetchMs, double.NegativeInfinity);
        }
    }

    public void SetParams(GlassParams p)
    {
        lock (_sync)
        {
            bool geometryChanged =
                Math.Abs(p.PowerFactor - _params.PowerFactor) > 1e-4 ||
                Math.Abs(p.RefractionA - _params.RefractionA) > 1e-4 ||
                Math.Abs(p.RefractionB - _params.RefractionB) > 1e-4 ||
                Math.Abs(p.RefractionC - _params.RefractionC) > 1e-4 ||
                Math.Abs(p.RefractionD - _params.RefractionD) > 1e-4 ||
                Math.Abs(p.FPower - _params.FPower) > 1e-4 ||
                Math.Abs(p.Noise - _params.Noise) > 1e-4 ||
                Math.Abs(p.GlowWeight - _params.GlowWeight) > 1e-4 ||
                Math.Abs(p.Refraction - _params.Refraction) > 1e-4 ||
                Math.Abs(p.EdgeBend - _params.EdgeBend) > 1e-4 ||
                Math.Abs(p.ChromaticAberration - _params.ChromaticAberration) > 1e-4 ||
                Math.Abs(p.Distortion - _params.Distortion) > 1e-4 ||
                Math.Abs(p.ZRadius - _params.ZRadius) > 1e-4 ||
                Math.Abs(p.TopCornerRadius - _params.TopCornerRadius) > 1e-4 ||
                Math.Abs(p.BottomCornerRadius - _params.BottomCornerRadius) > 1e-4 ||
                p.BevelMode != _params.BevelMode;

            _params = p;
            if (geometryChanged) _mapsDirty = true;
        }
    }

    public void SetBlur(int gaussianSigma)
    {
        int sigma = Math.Clamp(gaussianSigma, 0, 60);
        lock (_sync)
        {
            if (sigma == _blurSigma) return;
            _blurSigma = sigma;
            _blurPassRadii = GaussianBoxRadii(sigma);
            _mapsDirty = true;
        }
    }

    public void SetCaptureExclusion(bool exclude) { /* no-op */ }

    public bool HasPresentedFrame => _hasVisibleFrame;

    public void Start()
    {
        if (_isActive) return;
        _isActive = true;
        ++_renderGeneration;
        _hasVisibleFrame = false;
        _cachedRegion = null;
        _lastRegionFetchMs = double.NegativeInfinity;
        if (_gpuMode && _d3dPresenter == null && !TryEnableGpuPresenter(out var error))
            OnD3DPresenterFailed(error ?? new InvalidOperationException("GPU presenter restart failed."));
        _presentationPaused = false;
        _presentInFlight = false;
        _hasUploadedGpuFrame = false;
        _hasPresentedCpuFrame = false;
        RequestRenderTimerPeriod();

        if (_mag == null)
        {
            try
            {
                IntPtr h = _getHwnd();
                _mag = MagnifierCaptureSource.AcquireShared(h);
                _magReady = _mag.IsReady;
                RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier Hardware Capture acquired: ready={_magReady}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier init failed: {ex.Message}");
                _magReady = false;
            }
        }

        uint dpiNow = GetDpiForWindow(_getHwnd());
        _bitmapDpi = dpiNow > 0 ? dpiNow : 96;
        _captureVisibilityUntilTicks = 0;
        _overlayActiveCached = false;
        _lastOverlayCheckTicks = 0;
        _exactBitBltCapture = SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
        StartWorkerIfNeeded();
    }

    private void StartWorkerIfNeeded()
    {
        if (!_isActive || _worker != null) return;
        int generation = _renderGeneration;
        _worker = new Thread(() => WorkerLoop(generation))
        {
            IsBackground = true,
            Name = "LiquidGlassRender",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    public void Stop()
    {
        if (!_isActive) return;
        _isActive = false;
        ++_renderGeneration;
        _hasVisibleFrame = false;
        ClearLiveRegion();
        _presentationPaused = false;
        _exactBitBltCapture = false;
        ReleaseRenderTimerPeriod();

        // Safety: clear any display affinity a previous build may have set.
        SetWindowDisplayAffinitySafe(WDA_NONE);

        IntPtr h = IntPtr.Zero;
        try { h = _getHwnd(); } catch { /* best-effort handle retrieval during shutdown */ }
        MagnifierCaptureSource.ReleaseShared(h);
        _mag = null;
        _magReady = false;
        _magFailStreak = 0;

        try
        {
            DisposeGpuPresenter();
            _host.Source = null;
            _bitmap = null;
            ResetGpuGeometryTracking();
            // Capture buffers and GDI handles belong to the worker. It may still
            // be copying a native frame; only its finally block may free them.
        }
        catch { /* shutting down */ }
    }

    private void RequestRenderTimerPeriod()
    {
        if (Interlocked.Exchange(ref _renderTimerPeriodRequested, 1) != 0)
            return;

        try
        {
            uint result = TimeBeginPeriod(RenderTimerPeriodMs);
            if (result != 0)
            {
                Interlocked.Exchange(ref _renderTimerPeriodRequested, 0);
                RuntimeLog.Log(LogCategory, $"High-resolution timer request failed: {result}");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _renderTimerPeriodRequested, 0);
            RuntimeLog.Log(LogCategory, $"High-resolution timer unavailable: {ex.Message}");
        }
    }

    private void ReleaseRenderTimerPeriod()
    {
        if (Interlocked.Exchange(ref _renderTimerPeriodRequested, 0) == 0)
            return;

        try { TimeEndPeriod(RenderTimerPeriodMs); }
        catch { /* best-effort cleanup during shutdown */ }
    }

    private bool SetWindowDisplayAffinitySafe(uint affinity)
    {
        if (Volatile.Read(ref _currentDisplayAffinity) == (int)affinity) return true;
        try
        {
            var hwnd = _getHwnd();
            if (hwnd == IntPtr.Zero) return false;
            if (!SetWindowDisplayAffinity(hwnd, affinity)) return false;
            Volatile.Write(ref _currentDisplayAffinity, (int)affinity);
            try { DwmFlush(); } catch { /* affinity still applied */ }
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"SetWindowDisplayAffinity({affinity}) failed: {ex.Message}");
            return false;
        }
    }

    private void WorkerLoop(int generation)
    {
        var clock = Stopwatch.StartNew();
        double nextFrameAtMs = clock.Elapsed.TotalMilliseconds;
        try
        {
            while (ShouldContinueWorker(generation))
            {
                if (!ExecuteWorkerFrame(generation, clock, ref nextFrameAtMs))
                    break;
            }
        }
        finally
        {
            CleanupWorkerResources();
        }
    }

    private bool ShouldContinueWorker(int generation) =>
        _isActive && generation == Volatile.Read(ref _renderGeneration);

    private bool ExecuteWorkerFrame(int generation, Stopwatch clock, ref double nextFrameAtMs)
    {
        double frameStart = clock.Elapsed.TotalMilliseconds;
        double frameIntervalMs = ChooseLockedFrameIntervalMs(Volatile.Read(ref _activeIntervalMs));

        if (HandleCaptureOverlay(frameIntervalMs))
            return true;

        if (IsPresentationBlocked(frameIntervalMs, ref nextFrameAtMs, clock))
            return true;

        CaptureRegion? region = GetRegionCached(_animating, frameStart);
        if (!ShouldContinueWorker(generation)) return false;

        if (region == null)
        {
            SleepWithCapturePolling(200);
            return true;
        }

        TryRenderRegion(region.Value, generation);
        if (!_isActive) return false;

        TrackDiagnostics(frameStart);
        nextFrameAtMs = AdvanceFrameDeadline(nextFrameAtMs, frameIntervalMs, clock.Elapsed.TotalMilliseconds);
        return true;
    }

    private bool IsPresentationBlocked(double frameIntervalMs, ref double nextFrameAtMs, Stopwatch clock)
    {
        if (!_gpuMode && _presentInFlight)
        {
            SleepWithCapturePolling(frameIntervalMs);
            return true;
        }

        if (_presentationPaused)
        {
            SleepWithCapturePolling(frameIntervalMs);
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

    private void CleanupWorkerResources()
    {
        ReleaseGdiResources();
        _outBuffer = _blurTmp = Array.Empty<byte>();
        _idxR = _auxR = _idxG = _auxG = _idxB = _auxB = Array.Empty<int>();
        _edgeMask = Array.Empty<byte>();
        _outW = _outH = _srcW = _srcH = _margin = 0;
        _presentInFlight = false;
        ReleaseRenderTimerPeriod();
        _sourceBlurBuffer = _sourceBlurTmp = Array.Empty<byte>();
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
            {
                _worker = null;
                if (_isActive) RequestRenderTimerPeriod();
                StartWorkerIfNeeded();
            }));
        }
        catch
        {
            // Ignored if dispatcher is shutting down
            _worker = null;
        }
    }

    private bool HandleCaptureOverlay(double frameIntervalMs)
    {
        if (IsCaptureOverlayActive())
        {
            _exactBitBltCapture = false;
            SetWindowDisplayAffinitySafe(WDA_NONE);
            Thread.Sleep((int)Math.Max(15, frameIntervalMs));
            return true;
        }

        if (!_exactBitBltCapture)
        {
            _exactBitBltCapture = SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
        }
        return false;
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
        string geometry = _hasPresentedGpuGeometry
            ? $" src={_lastPresentedGpuGeometry.SrcW:F0}x{_lastPresentedGpuGeometry.SrcH:F0}" +
              $" notch={_lastPresentedGpuGeometry.NotchW:F0}x{_lastPresentedGpuGeometry.NotchH:F0}" +
              $" off={_lastPresentedGpuGeometry.OffX:F1},{_lastPresentedGpuGeometry.OffY:F1}" +
              $" origin={_lastPresentedGpuGeometry.CaptureOriginX},{_lastPresentedGpuGeometry.CaptureOriginY}"
            : string.Empty;
        RuntimeLog.Log(LogCategory,
            $"[{_logTag}] fps={loopFps:F1}/{targetFps:F0} presented={presented} " +
            $"renderer={(_gpuMode ? "GPU" : "CPU")} backdrop=rgb({optics.Red},{optics.Green},{optics.Blue})" +
            $" outside=rgb({outside.Red},{outside.Green},{outside.Blue})" +
            geometry);
        _dbgFrameCount = 0;
        _dbgLastLogMs = frameStart;
    }

    private double AdvanceFrameDeadline(double nextFrameAtMs, double frameIntervalMs, double nowMs)
    {
        nextFrameAtMs += frameIntervalMs;
        if (nextFrameAtMs < nowMs - 250.0)
        {
            return nowMs;
        }
        if (nextFrameAtMs > nowMs)
        {
            SleepWithCapturePolling(nextFrameAtMs - nowMs);
        }
        return nextFrameAtMs;
    }

    private CaptureRegion? _cachedRegion;
    private double _lastRegionFetchMs = double.NegativeInfinity;
    // The collapsed notch does not move between layout changes. Avoid a blocking
    private const double IdleRegionRefreshMs = 1000.0;

    private readonly object _liveRegionSync = new();
    private CaptureRegion? _liveRegion;
    private bool _hasLiveRegion;

    public void SetLiveRegion(CaptureRegion? region)
    {
        lock (_liveRegionSync)
        {
            _liveRegion = region;
            _hasLiveRegion = true;
        }
    }

    public void ClearLiveRegion()
    {
        lock (_liveRegionSync)
        {
            _liveRegion = null;
            _hasLiveRegion = false;
        }
    }

    private CaptureRegion? GetRegionCached(bool animating, double nowMs)
    {
        lock (_liveRegionSync)
        {
            if (_hasLiveRegion)
            {
                _cachedRegion = _liveRegion;
                _lastRegionFetchMs = nowMs;
                return _cachedRegion;
            }
        }

        if (animating || nowMs - _lastRegionFetchMs >= IdleRegionRefreshMs)
        {
            _cachedRegion = TryGetRegionOnUi();
            _lastRegionFetchMs = nowMs;
        }
        return _cachedRegion;
    }

    private static readonly string[] _captureProcessNames =
    {
        "snippingtool", "screenclippinghost", "screensketch", "sharex",
        "greenshot", "lightshot", "snagit32", "snagiteditor", "flameshot",
        "picpick", "screenpresso", "ksnip"
    };

    private long _lastOverlayCheckTicks;
    private bool _overlayActiveCached;
    private long _captureVisibilityUntilTicks;

    private const int VK_SNAPSHOT = 0x2C;
    private const int VK_SHIFT = 0x10;
    private const int VK_S = 0x53;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private bool CaptureHotkeyRequested(long now)
    {
        short snapshotState = GetAsyncKeyState(VK_SNAPSHOT);
        bool snipChord = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 &&
            (GetAsyncKeyState(VK_S) & 0x8000) != 0 &&
            (((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0);
        if ((snapshotState & 0x8001) == 0 && !snipChord) return false;

        _captureVisibilityUntilTicks = now + 1400;
        _overlayActiveCached = true;
        return true;
    }

    private void SleepWithCapturePolling(double milliseconds)
    {
        long wakeAt = Stopwatch.GetTimestamp() + StopwatchTicksFromMilliseconds(Math.Max(0.1, milliseconds));
        while (_isActive)
        {
            long remainingTicks = wakeAt - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return;

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs >= 2.0)
                Thread.Sleep(1);
            else if (remainingMs >= 0.25)
                Thread.Sleep(0);
            else
                Thread.SpinWait(16);

            if (_exactBitBltCapture && CaptureHotkeyRequested(Environment.TickCount64))
            {
                _exactBitBltCapture = false;
                SetWindowDisplayAffinitySafe(WDA_NONE);
                return;
            }
        }
    }

    private static long StopwatchTicksFromMilliseconds(double milliseconds) =>
        Math.Max(1, (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0));

    private bool IsCaptureOverlayActive()
    {
        long now = Environment.TickCount64;
        if (CaptureHotkeyRequested(now))
            return true;

        if (now < _captureVisibilityUntilTicks) return true;

        if (ForegroundIsCaptureTool())
        {
            _overlayActiveCached = true;
            _lastOverlayCheckTicks = now;
            _captureVisibilityUntilTicks = now + 800;
            return true;
        }

        // Hotkeys and foreground capture tools are checked above on every frame.
        if (now - _lastOverlayCheckTicks < 500) return _overlayActiveCached;
        _lastOverlayCheckTicks = now;
        _overlayActiveCached = DetectCaptureOverlay();
        if (_overlayActiveCached)
            _captureVisibilityUntilTicks = now + 800;
        return _overlayActiveCached;
    }

    /// <summary>Cheap check: does the current foreground window belong to a known
    /// snip/screenshot tool? Result is cached per-HWND so the process lookup runs
    /// only when the foreground window actually changes.</summary>
    private bool ForegroundIsCaptureTool()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == _fgProbeHwnd) return _fgProbeResult;

        _fgProbeHwnd = fg;
        _fgProbeResult = false;
        try
        {
            var sb = new StringBuilder(128);
            if (GetClassName(fg, sb, sb.Capacity) > 0 &&
                sb.ToString().IndexOf("ScreenClipping", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _fgProbeResult = true;
                return true;
            }

            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != 0)
            {
                string name = SafeProcessName(pid);
                if (IsCaptureProcess(name))
                {
                    _fgProbeResult = true;
                    return true;
                }
            }
        }
        catch
        {
            /* probe may fail if window handle becomes invalid */
        }
        return _fgProbeResult;
    }

    private static bool IsCaptureProcess(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < _captureProcessNames.Length; i++)
        {
            if (string.Equals(name, _captureProcessNames[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool DetectCaptureOverlay()
    {
        bool found = false;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (IsCaptureOverlayWindow(hwnd))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            /* EnumWindows may fail during desktop switch */
        }
        return found;
    }

    private static bool IsCaptureOverlayWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r))
            return false;

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w < 400 || h < 400)
            return false;

        var sb = new StringBuilder(128);
        if (GetClassName(hwnd, sb, sb.Capacity) > 0 &&
            sb.ToString().IndexOf("ScreenClipping", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return false;

        string name = SafeProcessName(pid);
        return IsCaptureProcess(name);
    }

    private static readonly ConcurrentDictionary<uint, (string Name, long ExpireTicks)> _pidNameCache = new();

    private static string SafeProcessName(uint pid)
    {
        long now = Environment.TickCount64;
        if (_pidNameCache.TryGetValue(pid, out var cached) && now < cached.ExpireTicks)
            return cached.Name;

        string name = string.Empty;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            name = proc.ProcessName;
        }
        catch
        {
            name = string.Empty;
        }

        if (_pidNameCache.Count > 256)
            _pidNameCache.Clear();

        _pidNameCache[pid] = (name, now + 15000);
        return name;
    }

    private CaptureRegion? TryGetRegionOnUi()
    {
        try
        {
            return _dispatcher.Invoke(_regionProvider, DispatcherPriority.Send, CancellationToken.None);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly record struct FrameDimensions(
        int OutW, int OutH, double OutScale,
        int BufW, int BufH,
        int NotchOffX, int NotchOffY,
        double MinHalf, int Margin,
        int SrcW, int SrcH,
        int PhysSrcW, int PhysSrcH,
        int SrcX, int SrcY,
        int CaptureShiftX, int CaptureShiftY,
        int MapCaptureShiftY);

    private readonly record struct BackdropCaptureParams(
        int SrcX, int SrcY, int SrcW, int SrcH,
        int PhysSrcW, int PhysSrcH,
        int RegionY, int DisplayH,
        bool UseMag, IntPtr ScreenDc);

    private readonly record struct FrameRenderContext(
        bool GpuMode, bool UseMag, bool MapsChanged,
        int BlurSigma, int[] BlurPassRadii, int DisplayH);

    private bool ProcessFrame(CaptureRegion region, int generation)
    {
        GlassParams p;
        int blurSigma;
        int[] blurPassRadii;
        lock (_sync)
        {
            p = _params;
            blurSigma = _blurSigma;
            blurPassRadii = _blurPassRadii;
        }

        p.TopCornerRadius = Math.Max(0.0, region.TopCornerRadiusDip);
        p.BottomCornerRadius = Math.Max(0.0, region.BottomCornerRadiusDip);
        _presentSubX = region.SubX;
        _presentSubY = region.SubY;

        int displayW = Math.Min(region.Width, _maxRegionW);
        int displayH = Math.Min(region.Height, _maxRegionH);
        if (displayW <= 1 || displayH <= 1) return false;

        bool gpuMode = _gpuMode;
        bool useMag = _magReady && _mag != null;
        var dims = ComputeFrameDimensions(region, p, gpuMode, useMag, displayW, displayH);
        _outScale = dims.OutScale;

        bool mapsChanged = false;
        if (!gpuMode)
        {
            var mapDimensions = new MapDimensions(
                dims.BufW, dims.BufH, dims.SrcW, dims.SrcH, dims.Margin,
                dims.OutW, dims.OutH, dims.NotchOffX, dims.NotchOffY,
                dims.CaptureShiftX, dims.MapCaptureShiftY);
            mapsChanged = EnsureMaps(p, mapDimensions);
        }

        var ctx = new FrameRenderContext(gpuMode, useMag, mapsChanged, blurSigma, blurPassRadii, displayH);
        return ExecuteFrameRender(p, region, dims, generation, ctx);
    }

    private FrameDimensions ComputeFrameDimensions(
        CaptureRegion region, GlassParams p, bool gpuMode, bool useMag, int displayW, int displayH)
    {
        double scale = 1.0;
        int outW = Math.Max(8, (int)Math.Round(displayW * scale));
        int outH = Math.Max(8, (int)Math.Round(displayH * scale));
        double outScale = displayW > 0 ? (double)outW / displayW * (_bitmapDpi / 96.0) : 1.0;

        int overscan = gpuMode
            ? 0
            : Math.Clamp((int)Math.Round(80 * (_bitmapDpi / 96.0)), 64, 150);
        int bufW = outW + overscan * 2;
        int bufH = outH + overscan;
        int notchOffX = overscan;
        int notchOffY = 0;

        double minHalf = Math.Min(outW, outH) * 0.5;
        double rimWidth = ComputeRimWidth(p.ZRadius, outScale, minHalf);
        int requiredMargin = ComputeSamplingMargin(
            rimWidth, p.Refraction, p.ChromaticAberration, p.Distortion,
            p.BevelMode, p.EdgeBend);

        int margin = requiredMargin;
        if (gpuMode)
        {
            margin = CaptureFullSurface
                ? GpuSamplingMarginLimit
                : Math.Clamp(requiredMargin + 96, 64, GpuSamplingMarginLimit);
        }

        int srcW = (gpuMode && CaptureFullSurface) ? SurfaceWidth : bufW + margin * 2;
        int srcH = (gpuMode && CaptureFullSurface) ? SurfaceHeight : bufH + margin * 2;

        double inv = 1.0 / scale;
        int physMargin = (int)Math.Round(margin * inv);
        int physNotchOffX = (int)Math.Round(notchOffX * inv);
        int physSrcW = (int)Math.Round(srcW * inv);
        int physSrcH = (int)Math.Round(srcH * inv);

        int requestedSrcX = region.X - physNotchOffX - physMargin;
        int requestedSrcY = region.Y - physMargin;
        int srcX = ClampCaptureOriginToVirtualDesktop(requestedSrcX, physSrcW, horizontal: true);
        int srcY = ClampCaptureOriginToVirtualDesktop(requestedSrcY, physSrcH, horizontal: false);
        int captureShiftX = srcX - requestedSrcX;
        int captureShiftY = srcY - requestedSrcY;

        bool spatiallyExactSource = useMag || _exactBitBltCapture;
        int mapCaptureShiftY = spatiallyExactSource ? captureShiftY : 0;

        return new FrameDimensions(
            outW, outH, outScale,
            bufW, bufH,
            notchOffX, notchOffY,
            minHalf, margin,
            srcW, srcH,
            physSrcW, physSrcH,
            srcX, srcY,
            captureShiftX, captureShiftY,
            mapCaptureShiftY);
    }

    private bool ExecuteFrameRender(
        GlassParams p, CaptureRegion region, FrameDimensions dims, int generation, FrameRenderContext ctx)
    {
        bool needsGdi = !(_memDc != IntPtr.Zero && _dibBits != IntPtr.Zero && _dibW == dims.SrcW && _dibH == dims.SrcH);
        IntPtr screenDc = needsGdi ? GetDC(IntPtr.Zero) : IntPtr.Zero;
        try
        {
            if (needsGdi && !EnsureGdiResources(dims.SrcW, dims.SrcH, screenDc)) return false;

            var captureParams = new BackdropCaptureParams(
                dims.SrcX, dims.SrcY, dims.SrcW, dims.SrcH,
                dims.PhysSrcW, dims.PhysSrcH, region.Y, ctx.DisplayH, ctx.UseMag, screenDc);
            if (!CaptureBackdrop(captureParams))
                return false;

            if ((_dbgFrameCount & 3) == 0)
            {
                UpdateBackdropOptics(
                    dims.SrcW, dims.SrcH,
                    dims.Margin + dims.NotchOffX - dims.CaptureShiftX,
                    dims.Margin + dims.NotchOffY - dims.MapCaptureShiftY,
                    dims.OutW, dims.OutH);
            }

            if (_presentationPaused || !_isActive || ctx.GpuMode != _gpuMode || generation != Volatile.Read(ref _renderGeneration))
                return false;

            if (ctx.GpuMode)
                return ProcessGpuFrame(p, dims, generation);

            return ProcessCpuFrame(p, dims.SrcW, dims.SrcH, ctx.MapsChanged, ctx.BlurSigma, ctx.BlurPassRadii);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private bool CaptureBackdrop(BackdropCaptureParams cp)
    {
        if (cp.UseMag && TryMagnifierCapture(cp))
            return true;

        return BitBltCapture(cp);
    }

    private bool TryMagnifierCapture(BackdropCaptureParams cp)
    {
        var mag = _mag;
        if (mag == null) return false;

        bool captured = false;
        if (cp.PhysSrcW == cp.SrcW && cp.PhysSrcH == cp.SrcH)
        {
            if (mag.CaptureInto(cp.SrcX, cp.SrcY, cp.PhysSrcW, cp.PhysSrcH, _dibBits, out _, out _))
            {
                captured = true;
                _magFailStreak = 0;
            }
            else if (++_magFailStreak >= 60)
            {
                _magReady = false;
                RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier failing repeatedly; falling back to BitBlt.");
            }
        }
        else
        {
            IntPtr screenDc = cp.ScreenDc == IntPtr.Zero ? GetDC(IntPtr.Zero) : cp.ScreenDc;
            if (EnsureStagingResources(cp.PhysSrcW, cp.PhysSrcH, screenDc) &&
                mag.CaptureInto(cp.SrcX, cp.SrcY, cp.PhysSrcW, cp.PhysSrcH, _stagingBits, out _, out _) &&
                StretchBlt(_memDc, 0, 0, cp.SrcW, cp.SrcH, _stagingDc, 0, 0, cp.PhysSrcW, cp.PhysSrcH, SRCCOPY))
            {
                GdiFlush();
                captured = true;
                _magFailStreak = 0;
            }
            else if (++_magFailStreak >= 60)
            {
                _magReady = false;
                RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier failing repeatedly; falling back to BitBlt.");
            }
        }
        return captured;
    }

    private bool BitBltCapture(BackdropCaptureParams cp)
    {
        IntPtr screenDc = cp.ScreenDc == IntPtr.Zero ? GetDC(IntPtr.Zero) : cp.ScreenDc;
        int bltSrcY = _exactBitBltCapture ? cp.SrcY : ComputeFallbackSourceY(cp.RegionY, cp.DisplayH);
        if (!StretchBlt(_memDc, 0, 0, cp.SrcW, cp.SrcH, screenDc, cp.SrcX, bltSrcY, cp.PhysSrcW, cp.PhysSrcH, SRCCOPY))
            return false;
        GdiFlush();
        return true;
    }

    private bool ProcessGpuFrame(GlassParams p, FrameDimensions dims, int generation)
    {
        double topR = Math.Clamp(p.TopCornerRadius * _outScale, 0.0, dims.MinHalf);
        double bottomR = Math.Clamp(p.BottomCornerRadius * _outScale, 0.0, dims.MinHalf);

        double offX = Math.Clamp(
            dims.Margin - dims.CaptureShiftX + _presentSubX, 0, Math.Max(0, dims.SrcW - dims.OutW));
        double offY = Math.Clamp(
            dims.Margin - dims.MapCaptureShiftY + _presentSubY, 0, Math.Max(0, dims.SrcH - dims.OutH));

        var geom = new GpuGeometry(
            dims.SrcW, dims.SrcH, dims.OutW, dims.OutH, offX, offY,
            topR, bottomR,
            p.PowerFactor, p.RefractionA, p.RefractionB, p.RefractionC, p.RefractionD,
            p.FPower, p.Noise, p.GlowWeight, p.GlowBias, p.GlowEdge0, p.GlowEdge1,
            Math.Clamp(p.ChromaticAberration, 0.0, 2.0),
            double.IsFinite(p.EdgeBend) ? Math.Max(0.0, p.EdgeBend) : 0.0,
            1.0 + p.Saturation, p.Brightness,
            p.BevelMode >= 1 ? 1.0 : 0.0,
            dims.SrcX, dims.SrcY);

        ulong sourceHash = ComputeSourceHash(dims.SrcW, dims.SrcH);
        long nowTicks = Environment.TickCount64;
        bool unchanged = _hasUploadedGpuFrame &&
            sourceHash == _lastCaptureHash &&
            _lastUploadedGpuGeometry.Equals(geom);
        if (!CaptureFullSurface && unchanged && nowTicks - _lastPresentTicks < UnchangedRepresentIntervalMs)
            return false;

        if (PresentRawGpu(dims.SrcW, dims.SrcH, geom, generation))
        {
            _hasUploadedGpuFrame = true;
            _lastCaptureHash = sourceHash;
            _lastUploadedGpuGeometry = geom;
            _lastPresentTicks = nowTicks;
        }
        return false;
    }

    private bool ProcessCpuFrame(
        GlassParams p, int srcW, int srcH, bool mapsChanged, int blurSigma, int[] blurPassRadii)
    {
        ulong cpuSourceHash = ComputeSourceHash(srcW, srcH);
        long cpuNowTicks = Environment.TickCount64;
        bool cpuUnchanged = _hasPresentedCpuFrame && !mapsChanged &&
            cpuSourceHash == _lastCaptureHash &&
            Math.Abs(_presentSubX - _lastPresentedSubX) < 0.001 &&
            Math.Abs(_presentSubY - _lastPresentedSubY) < 0.001 &&
            Math.Abs(p.Saturation - _lastPresentedSaturation) < 0.001 &&
            Math.Abs(p.Brightness - _lastPresentedBrightness) < 0.001;
        if (cpuUnchanged && cpuNowTicks - _lastPresentTicks < UnchangedRepresentIntervalMs)
            return false;

        byte[]? blurredSource = blurSigma > 0
            ? BlurCapturedSource(srcW, srcH, blurPassRadii)
            : null;
        Refract(p, blurredSource);

        _hasPresentedCpuFrame = true;
        _lastCaptureHash = cpuSourceHash;
        _lastPresentedSubX = _presentSubX;
        _lastPresentedSubY = _presentSubY;
        _lastPresentedSaturation = p.Saturation;
        _lastPresentedBrightness = p.Brightness;
        _lastPresentTicks = cpuNowTicks;
        return true;
    }

    private static int ClampCaptureOriginToVirtualDesktop(int requested, int captureLength, bool horizontal)
    {
        int desktopOrigin = GetSystemMetrics(horizontal ? SM_XVIRTUALSCREEN : SM_YVIRTUALSCREEN);
        int desktopLength = GetSystemMetrics(horizontal ? SM_CXVIRTUALSCREEN : SM_CYVIRTUALSCREEN);
        return ClampCaptureOrigin(requested, captureLength, desktopOrigin, desktopLength);
    }

    internal static int ClampCaptureOrigin(
        int requested, int captureLength, int desktopOrigin, int desktopLength)
    {
        if (captureLength <= 0 || desktopLength <= 0) return requested;
        if (captureLength >= desktopLength) return desktopOrigin;

        long max = (long)desktopOrigin + desktopLength - captureLength;
        return (int)Math.Clamp((long)requested, desktopOrigin, max);
    }

    internal static double ComputeRimWidth(double normalizedRadius, double outputScale, double minHalf) =>
        Math.Clamp(Math.Clamp(normalizedRadius, 0.02, 0.95) * 100.0 * outputScale, 3.0, minHalf);

    internal static double LensProfile(double normalizedDistance, bool broad = false)
    {
        double s = Smoother01(normalizedDistance);
        // Apple-like edge lens: the optical slope peaks in the outer portion of
        double oneMinusS = 1.0 - s;
        double profile = (256.0 / 27.0) * s * oneMinusS * oneMinusS * oneMinusS;
        return broad ? Math.Sqrt(profile) : profile;
    }

    internal static double RefractionAmplitude(
        double rimWidth, double refraction, int bevelMode = 0, double edgeBend = 1.0)
    {
        double r = Math.Max(refraction, 0.0);
        double response = r / Math.Max(0.65 + 0.35 * r, 0.001);
        double travelRatio = EdgeBendGain(edgeBend) * response *
            (bevelMode >= 1 ? 1.08 : 1.0);

        // Extreme mode intentionally allows the ray to travel beyond the optical
        return rimWidth * travelRatio;
    }

    internal static double EdgeBendGain(double edgeBend)
    {
        // Super-linear and intentionally uncapped: values above the former 300%
        double bend = double.IsFinite(edgeBend) ? Math.Max(edgeBend, 0.0) : 0.0;
        return 0.58 * Math.Pow(bend, 1.5);
    }

    internal static double DirectionalLensWidth(
        double rimWidth, double inwardNormalX, double edgeBend)
    {
        double bend = double.IsFinite(edgeBend) ? Math.Max(edgeBend, 0.0) : 0.0;
        // A capsule's side caps need a longer optical run than its flat top and
        double sideAxis = Math.Pow(Math.Clamp(Math.Abs(inwardNormalX), 0.0, 1.0), 2.6);
        return rimWidth * (1.0 + 0.5 * bend * sideAxis);
    }

    internal static bool IsGpuGeometryValid(double srcW, double srcH, double notchW, double notchH) =>
        srcW >= 1.0 && srcH >= 1.0 && notchW >= 1.0 && notchH >= 1.0 &&
        srcW + 1.5 >= notchW && srcH + 1.5 >= notchH;

    internal static int ComputeFallbackSourceY(int regionY, int displayHeight) =>
        checked(regionY + Math.Max(0, displayHeight) + 2);

    internal static int ComputeSamplingMargin(double rimWidth, double refraction, double chroma,
        double distortion, int bevelMode, double edgeBend = 1.0)
    {
        double amplitude = RefractionAmplitude(
            rimWidth, Math.Clamp(refraction, 0.0, 3.0), bevelMode, edgeBend);
        double chromaOffset = Math.Min(Math.Clamp(chroma, 0.0, 2.0) * (1.25 + rimWidth * 0.085), 8.0);
        double fluidOffset = Math.Clamp(distortion, 0.0, 2.0) * 2.25;
        return Math.Clamp((int)Math.Ceiling(amplitude + chromaOffset + fluidOffset + 3.0), 12, 512);
    }

    private void Present(int generation)
    {
        if (!_isActive) return;
        int w = _outW, h = _outH;
        byte[] buffer = _outBuffer;
        double presentDpi = _bitmapDpi < 1.0 ? 96.0 : _bitmapDpi;
        double dpiScale = _bitmapDpi > 0 ? _bitmapDpi / 96.0 : 1.0;
        double txDip = -_presentSubX / dpiScale;
        double tyDip = -_presentSubY / dpiScale;

        _presentInFlight = true;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
            {
                try
                {
                    if (!_isActive || _gpuMode || generation != _renderGeneration) return;

                    UpdatePresentBitmap(w, h, buffer, presentDpi, txDip, tyDip);
                    _hasVisibleFrame = true;
                    Interlocked.Increment(ref _dbgPresentCount);
                }
                finally
                {
                    if (generation == _renderGeneration) _presentInFlight = false;
                }
            }));
        }
        catch (Exception)
        {
            _presentInFlight = false;
        }
    }

    private void UpdatePresentBitmap(int w, int h, byte[] buffer, double presentDpi, double txDip, double tyDip)
    {
        bool dpiChanged = _bitmap != null && Math.Abs(_bitmap.DpiX - presentDpi) > 0.5;
        bool needsBitmap = _bitmap == null || dpiChanged ||
            _bitmap.PixelWidth < w || _bitmap.PixelHeight < h;

        if (needsBitmap)
        {
            int previousW = dpiChanged ? 0 : _bitmap?.PixelWidth ?? 0;
            int previousH = dpiChanged ? 0 : _bitmap?.PixelHeight ?? 0;
            int capacityW = GrowPresentCapacity(previousW, w, 128);
            int capacityH = GrowPresentCapacity(previousH, h, 96);
            _bitmap = new WriteableBitmap(
                capacityW, capacityH, presentDpi, presentDpi, PixelFormats.Bgra32, null);
        }

        var bitmap = _bitmap!;
        int frameX = (bitmap.PixelWidth - w) / 2;
        bitmap.WritePixels(new Int32Rect(frameX, 0, w, h), buffer, w * 4, 0);

        ConfigureHostForBitmap(bitmap, txDip, tyDip);
    }

    private void ConfigureHostForBitmap(WriteableBitmap bitmap, double txDip, double tyDip)
    {
        if (!ReferenceEquals(_host.Source, bitmap))
        {
            _host.Stretch = Stretch.None;
            _host.HorizontalAlignment = HorizontalAlignment.Center;
            _host.VerticalAlignment = VerticalAlignment.Top;
            RenderOptions.SetBitmapScalingMode(_host, BitmapScalingMode.Linear);
            _hostTransform ??= new TranslateTransform();
            _host.RenderTransform = _hostTransform;
            _host.Source = bitmap;
        }
        if (_hostTransform != null)
        {
            _hostTransform.X = txDip;
            _hostTransform.Y = tyDip;
        }
    }

    internal static int GrowPresentCapacity(int current, int required, int slack)
    {
        if (required <= 0) return 0;
        if (current >= required) return current;

        int target = current > 0
            ? Math.Max(required, checked(current + slack))
            : checked(required + slack);
        const int quantum = 64;
        return checked(((target + quantum - 1) / quantum) * quantum);
    }

    private bool PresentRawGpu(int srcW, int srcH, GpuGeometry geom, int generation)
    {
        if (!_isActive || !_gpuMode || generation != Volatile.Read(ref _renderGeneration) || _dibBits == IntPtr.Zero) return false;

        var presenter = _d3dPresenter;
        if (presenter == null)
        {
            if (_isActive && _gpuMode && generation == Volatile.Read(ref _renderGeneration))
                OnD3DPresenterFailed(new InvalidOperationException("GPU presenter is not initialized."));
            return false;
        }

        if (!presenter.UploadFrame(_dibBits, srcW, srcH, srcW * 4, geom))
        {
            if (_isActive && _gpuMode && generation == Volatile.Read(ref _renderGeneration))
                OnD3DPresenterFailed(new InvalidOperationException("GPU frame upload failed."));
            return false;
        }
        return true;
    }


    /// <summary>Exact FNV-1a hash of the captured source frame. Used to skip
    /// presenting frames whose backdrop did not change: an unchanged present
    /// still forces a WPF re-render plus a layered-window readback for the whole
    /// window, which is what saturates the render thread when the notch and the
    /// settings window run glass simultaneously.</summary>
    private ulong ComputeSourceHash(int srcW, int srcH) =>
        ComputeSourceHash(_dibBits, srcW, srcH);

    internal static unsafe ulong ComputeSourceHash(IntPtr dibBits, int srcW, int srcH)
    {
        if (dibBits == IntPtr.Zero || srcW <= 0 || srcH <= 0) return 0;

        const ulong fnvPrime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        byte* src = (byte*)dibBits;
        int rowBytes = srcW * 4;
        int qwordsPerRow = rowBytes >> 3;
        int qwordStep = Math.Max(1, qwordsPerRow / 96);
        int rowStep = Math.Max(1, srcH / 96);

        for (int y = 0; y < srcH; y += rowStep)
        {
            byte* rowStart = src + (long)y * rowBytes;
            ulong* row = (ulong*)rowStart;
            for (int i = 0; i < qwordsPerRow; i += qwordStep)
            {
                hash ^= row[i];
                hash *= fnvPrime;
            }
        }

        return hash;
    }

    private unsafe void UpdateBackdropOptics(
        int srcW, int srcH, int sampleX, int sampleY, int sampleW, int sampleH)
    {
        if (_dibBits == IntPtr.Zero || srcW <= 0 || srcH <= 0 || sampleW <= 0 || sampleH <= 0)
            return;

        const int columns = 8;
        const int rows = 8;
        Span<int> samples = stackalloc int[columns * rows];
        byte* src = (byte*)_dibBits;
        int stride = srcW * 4;

        for (int gy = 0; gy < rows; gy++)
        {
            int y = Math.Clamp(sampleY + (sampleH - 1) * gy / (rows - 1), 0, srcH - 1);
            byte* row = src + (long)y * stride;
            for (int gx = 0; gx < columns; gx++)
            {
                int x = Math.Clamp(sampleX + (sampleW - 1) * gx / (columns - 1), 0, srcW - 1);
                byte* p = row + x * 4;
                samples[gy * columns + gx] = (p[2] << 16) | (p[1] << 8) | p[0];
            }
        }

        BackdropOptics optics = AnalyzeBackdropSamples(samples, columns, rows);
        Volatile.Write(ref _averageBackgroundColorRgb,
            (optics.Red << 16) | (optics.Green << 8) | optics.Blue);
        Volatile.Write(ref _backgroundLightX1000, (int)Math.Round(optics.LightX * 1000.0));
        Volatile.Write(ref _backgroundLightY1000, (int)Math.Round(optics.LightY * 1000.0));
        Volatile.Write(ref _backgroundContrast1000, (int)Math.Round(optics.Contrast * 1000.0));
        AverageBackgroundBrightnessInt = (int)Math.Round(
            0.299 * optics.Red + 0.587 * optics.Green + 0.114 * optics.Blue);

        int outsideX = Math.Clamp(sampleX + sampleW / 2, 0, srcW - 1);
        int belowY = sampleY + sampleH + 16;
        int outsideY = belowY < srcH
            ? belowY
            : Math.Clamp(sampleY - 16, 0, srcH - 1);
        byte* outside = src + (long)outsideY * stride + outsideX * 4;
        Volatile.Write(ref _outsideBackdropColorRgb,
            (outside[2] << 16) | (outside[1] << 8) | outside[0]);
    }

    internal static BackdropOptics AnalyzeBackdropSamples(
        ReadOnlySpan<int> rgbSamples, int columns, int rows)
    {
        int count = columns * rows;
        if (columns <= 0 || rows <= 0 || rgbSamples.Length < count)
            return new BackdropOptics(128, 128, 128, 0, 0, 0);

        Span<double> luminance = count <= 256
            ? stackalloc double[count]
            : new double[count];
        double sumR = 0, sumG = 0, sumB = 0, sumL = 0;
        double minL = 255, maxL = 0;

        for (int i = 0; i < count; i++)
        {
            int rgb = rgbSamples[i];
            int r = (rgb >> 16) & 0xFF;
            int g = (rgb >> 8) & 0xFF;
            int b = rgb & 0xFF;
            double l = 0.299 * r + 0.587 * g + 0.114 * b;
            luminance[i] = l;
            sumR += r; sumG += g; sumB += b; sumL += l;
            minL = Math.Min(minL, l);
            maxL = Math.Max(maxL, l);
        }

        double meanL = sumL / count;
        (double lightX, double lightY, double contrast) = ComputeDirectionalLighting(luminance, columns, rows, meanL, minL, maxL);

        return new BackdropOptics(
            (byte)Math.Clamp((int)Math.Round(sumR / count), 0, 255),
            (byte)Math.Clamp((int)Math.Round(sumG / count), 0, 255),
            (byte)Math.Clamp((int)Math.Round(sumB / count), 0, 255),
            lightX,
            lightY,
            contrast);
    }

    private static (double LightX, double LightY, double Contrast) ComputeDirectionalLighting(
        ReadOnlySpan<double> luminance, int columns, int rows, double meanL, double minL, double maxL)
    {
        double weightedX = 0, weightedY = 0, totalWeight = 0;
        for (int y = 0; y < rows; y++)
        {
            double ny = rows == 1 ? 0 : y * 2.0 / (rows - 1) - 1.0;
            for (int x = 0; x < columns; x++)
            {
                double highlight = Math.Max(luminance[y * columns + x] - meanL, 0.0);
                double weight = highlight * highlight;
                if (weight <= 1e-6) continue;

                double nx = columns == 1 ? 0 : x * 2.0 / (columns - 1) - 1.0;
                weightedX += nx * weight;
                weightedY += ny * weight;
                totalWeight += weight;
            }
        }

        double contrast = Math.Clamp((maxL - minL) / 255.0, 0.0, 1.0);
        double directionalStrength = Math.Clamp(contrast * 2.2, 0.0, 1.0);
        double lightX = totalWeight > 1e-6 ? weightedX / totalWeight * directionalStrength : 0.0;
        double lightY = totalWeight > 1e-6 ? weightedY / totalWeight * directionalStrength : 0.0;

        return (Math.Clamp(lightX, -1.0, 1.0), Math.Clamp(lightY, -1.0, 1.0), contrast);
    }

    private bool EnsureGdiResources(int srcW, int srcH, IntPtr screenDc)
    {
        if (_memDc != IntPtr.Zero && _dibBits != IntPtr.Zero && _dibW == srcW && _dibH == srcH)
            return true;

        ReleaseGdiResources();

        _memDc = CreateCompatibleDC(screenDc);
        if (_memDc == IntPtr.Zero) return false;

        SetStretchBltMode(_memDc, HALFTONE);

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = srcW,
                biHeight = -srcH,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            },
            bmiColors = new uint[256]
        };

        _dibBmp = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out _dibBits, IntPtr.Zero, 0);
        if (_dibBmp == IntPtr.Zero || _dibBits == IntPtr.Zero)
        {
            ReleaseGdiResources();
            return false;
        }

        _oldBmp = SelectObject(_memDc, _dibBmp);
        _dibW = srcW; _dibH = srcH;
        return true;
    }

    private void ReleaseGdiResources()
    {
        if (_memDc != IntPtr.Zero && _oldBmp != IntPtr.Zero) SelectObject(_memDc, _oldBmp);
        if (_dibBmp != IntPtr.Zero) DeleteObject(_dibBmp);
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
        _memDc = _dibBmp = _dibBits = _oldBmp = IntPtr.Zero;
        _dibW = _dibH = 0;

        ReleaseStagingResources();
    }

    private bool EnsureStagingResources(int w, int h, IntPtr screenDc)
    {
        if (w <= 0 || h <= 0) return false;
        if (_stagingDc != IntPtr.Zero && _stagingBits != IntPtr.Zero && _stagingW == w && _stagingH == h)
            return true;

        ReleaseStagingResources();

        _stagingDc = CreateCompatibleDC(screenDc);
        if (_stagingDc == IntPtr.Zero) return false;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            },
            bmiColors = new uint[256]
        };

        _stagingBmp = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out _stagingBits, IntPtr.Zero, 0);
        if (_stagingBmp == IntPtr.Zero || _stagingBits == IntPtr.Zero)
        {
            ReleaseStagingResources();
            return false;
        }

        _stagingOldBmp = SelectObject(_stagingDc, _stagingBmp);
        _stagingW = w; _stagingH = h;
        return true;
    }

    private void ReleaseStagingResources()
    {
        if (_stagingDc != IntPtr.Zero && _stagingOldBmp != IntPtr.Zero) SelectObject(_stagingDc, _stagingOldBmp);
        if (_stagingBmp != IntPtr.Zero) DeleteObject(_stagingBmp);
        if (_stagingDc != IntPtr.Zero) DeleteDC(_stagingDc);
        _stagingDc = _stagingBmp = _stagingBits = _stagingOldBmp = IntPtr.Zero;
        _stagingW = _stagingH = 0;
    }

    private const int ParallelPixelThreshold = 40_000;

    private static readonly ParallelOptions ParallelOpts = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8))
    };

    private readonly record struct ColorAdjustment(bool Adjust, double SatFactor, double BrightAdd);

    private readonly unsafe struct GatherChannelMap
    {
        public readonly int* Idx;
        public readonly int* Aux;
        public GatherChannelMap(int* idx, int* aux) { Idx = idx; Aux = aux; }
    }

    private readonly unsafe struct GatherBuffers
    {
        public readonly byte* Src;
        public readonly byte* Dst;
        public readonly GatherChannelMap R;
        public readonly GatherChannelMap G;
        public readonly GatherChannelMap B;
        public GatherBuffers(byte* src, byte* dst, GatherChannelMap r, GatherChannelMap g, GatherChannelMap b)
        {
            Src = src;
            Dst = dst;
            R = r;
            G = g;
            B = b;
        }
    }

    private unsafe void Refract(GlassParams p, byte[]? sourceOverride)
    {
        if (sourceOverride == null)
        {
            RefractFromSource((byte*)_dibBits, p);
            return;
        }

        fixed (byte* source = sourceOverride)
            RefractFromSource(source, p);
    }

    private unsafe void RefractFromSource(byte* src, GlassParams p)
    {
        int count = _outW * _outH;
        int stride = _srcW * 4;

        double sat = p.Saturation;
        double bright = p.Brightness;
        bool adjust = Math.Abs(sat) > 1e-3 || Math.Abs(bright) > 1e-3;
        double satFactor = 1.0 + sat;
        double brightAdd = bright * 255.0;
        var colorAdj = new ColorAdjustment(adjust, satFactor, brightAdd);

        fixed (byte* dst = _outBuffer)
        fixed (int* ir = _idxR, ar = _auxR, ig = _idxG, ag = _auxG, ib = _idxB, ab = _auxB)
        {
            if (count >= ParallelPixelThreshold)
            {
                IntPtr srcP = (IntPtr)src, dstP = (IntPtr)dst;
                IntPtr irP = (IntPtr)ir, arP = (IntPtr)ar, igP = (IntPtr)ig, agP = (IntPtr)ag, ibP = (IntPtr)ib, abP = (IntPtr)ab;
                int st = stride;

                Parallel.ForEach(Partitioner.Create(0, count), ParallelOpts, range =>
                {
                    var mapR = new GatherChannelMap((int*)irP, (int*)arP);
                    var mapG = new GatherChannelMap((int*)igP, (int*)agP);
                    var mapB = new GatherChannelMap((int*)ibP, (int*)abP);
                    var threadBuffers = new GatherBuffers((byte*)srcP, (byte*)dstP, mapR, mapG, mapB);
                    Gather(threadBuffers, st, range.Item1, range.Item2, colorAdj);
                });
            }
            else
            {
                var mapR = new GatherChannelMap(ir, ar);
                var mapG = new GatherChannelMap(ig, ag);
                var mapB = new GatherChannelMap(ib, ab);
                var buffers = new GatherBuffers(src, dst, mapR, mapG, mapB);
                Gather(buffers, stride, 0, count, colorAdj);
            }
        }
    }

    private static unsafe void Gather(GatherBuffers buffers, int stride, int start, int end, ColorAdjustment color)
    {
        byte* src = buffers.Src;
        byte* dst = buffers.Dst;
        int* idxR = buffers.R.Idx, auxR = buffers.R.Aux;
        int* idxG = buffers.G.Idx, auxG = buffers.G.Aux;
        int* idxB = buffers.B.Idx, auxB = buffers.B.Aux;

        for (int i = start; i < end; i++)
        {
            int o = i << 2;
            int b = Bilerp(src, idxB[i], auxB[i], stride, 0);
            int g = Bilerp(src, idxG[i], auxG[i], stride, 1);
            int r = Bilerp(src, idxR[i], auxR[i], stride, 2);

            if (!color.Adjust)
            {
                dst[o + 0] = (byte)b;
                dst[o + 1] = (byte)g;
                dst[o + 2] = (byte)r;
                dst[o + 3] = 255;
            }
            else
            {
                double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                dst[o + 0] = ClampByte(lum + (b - lum) * color.SatFactor + color.BrightAdd);
                dst[o + 1] = ClampByte(lum + (g - lum) * color.SatFactor + color.BrightAdd);
                dst[o + 2] = ClampByte(lum + (r - lum) * color.SatFactor + color.BrightAdd);
                dst[o + 3] = 255;
            }
        }
    }

    private static unsafe int Bilerp(byte* src, int idx, int aux, int stride, int chan)
    {
        int stepX = (aux & 1) != 0 ? 4 : 0;
        int stepY = (aux & 2) != 0 ? stride : 0;
        int wx = (aux >> 2) & 0x1FF;
        int wy = (aux >> 12) & 0x1FF;

        int bas = idx + chan;
        int p00 = src[bas];
        int p10 = src[bas + stepX];
        int p01 = src[bas + stepY];
        int p11 = src[bas + stepX + stepY];

        int top = p00 * (256 - wx) + p10 * wx;
        int bot = p01 * (256 - wx) + p11 * wx;
        return (top * (256 - wy) + bot * wy) >> 16;
    }

    private static byte ClampByte(double v)
    {
        if (v <= 0) return 0;
        if (v >= 255) return 255;
        return (byte)(v + 0.5);
    }

    internal static int[] GaussianBoxRadii(double sigma, int passes = 3)
    {
        int count = Math.Max(1, passes);
        var radii = new int[count];
        if (sigma <= 0.0) return radii;

        double idealWidth = Math.Sqrt((12.0 * sigma * sigma / count) + 1.0);
        int lowerWidth = (int)Math.Floor(idealWidth);
        if ((lowerWidth & 1) == 0) lowerWidth--;
        lowerWidth = Math.Max(1, lowerWidth);
        int upperWidth = lowerWidth + 2;

        double numerator = 12.0 * sigma * sigma
            - count * lowerWidth * lowerWidth
            - 4.0 * count * lowerWidth
            - 3.0 * count;
        int lowerCount = (int)Math.Round(
            numerator / (-4.0 * lowerWidth - 4.0));
        lowerCount = Math.Clamp(lowerCount, 0, count);

        for (int i = 0; i < count; i++)
        {
            int width = i < lowerCount ? lowerWidth : upperWidth;
            radii[i] = (width - 1) / 2;
        }
        return radii;
    }

    private byte[] BlurCapturedSource(int w, int h, int[] passRadii)
    {
        int bytes = checked(w * h * 4);
        if (_sourceBlurBuffer.Length < bytes)
            _sourceBlurBuffer = new byte[bytes];
        if (_sourceBlurTmp.Length < bytes)
            _sourceBlurTmp = new byte[bytes];

        System.Runtime.InteropServices.Marshal.Copy(
            _dibBits, _sourceBlurBuffer, 0, bytes);

        byte[] a = _sourceBlurBuffer, b = _sourceBlurTmp;
        int maximumRadius = Math.Max(1, Math.Min(w, h) / 2);
        for (int pass = 0; pass < passRadii.Length; pass++)
        {
            int r = Math.Clamp(passRadii[pass], 0, maximumRadius);
            if (r < 1) continue;
            ParallelRange(h, (y0, y1) => BoxBlurHorizontal(a, b, w, r, y0, y1));
            ParallelRange(w, (x0, x1) => BoxBlurVertical(b, a, w, h, r, x0, x1));
        }

        return a;
    }

    private static void ParallelRange(int length, Action<int, int> body)
    {
        if (length >= 64)
            Parallel.ForEach(Partitioner.Create(0, length), ParallelOpts, range => body(range.Item1, range.Item2));
        else
            body(0, length);
    }

    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int radius, int y0, int y1)
    {
        int window = 2 * radius + 1;
        for (int y = y0; y < y1; y++)
        {
            int rowBase = y * w * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = Math.Clamp(dx, 0, w - 1);
                int o = rowBase + nx * 4;
                sumB += src[o]; sumG += src[o + 1]; sumR += src[o + 2]; sumA += src[o + 3];
            }
            for (int x = 0; x < w; x++)
            {
                int t = rowBase + x * 4;
                dst[t] = (byte)(sumB / window);
                dst[t + 1] = (byte)(sumG / window);
                dst[t + 2] = (byte)(sumR / window);
                dst[t + 3] = (byte)(sumA / window);

                int outX = Math.Max(0, x - radius);
                int inX = Math.Min(w - 1, x + 1 + radius);
                int oOut = rowBase + outX * 4;
                int oIn = rowBase + inX * 4;
                sumB += src[oIn] - src[oOut];
                sumG += src[oIn + 1] - src[oOut + 1];
                sumR += src[oIn + 2] - src[oOut + 2];
                sumA += src[oIn + 3] - src[oOut + 3];
            }
        }
    }

    private static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int radius, int x0, int x1)
    {
        int window = 2 * radius + 1;
        int rowStride = w * 4;
        for (int x = x0; x < x1; x++)
        {
            int col = x * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int ny = Math.Clamp(dy, 0, h - 1);
                int o = ny * rowStride + col;
                sumB += src[o]; sumG += src[o + 1]; sumR += src[o + 2]; sumA += src[o + 3];
            }
            for (int y = 0; y < h; y++)
            {
                int t = y * rowStride + col;
                dst[t] = (byte)(sumB / window);
                dst[t + 1] = (byte)(sumG / window);
                dst[t + 2] = (byte)(sumR / window);
                dst[t + 3] = (byte)(sumA / window);

                int outY = Math.Max(0, y - radius);
                int inY = Math.Min(h - 1, y + 1 + radius);
                int oOut = outY * rowStride + col;
                int oIn = inY * rowStride + col;
                sumB += src[oIn] - src[oOut];
                sumG += src[oIn + 1] - src[oOut + 1];
                sumR += src[oIn + 2] - src[oOut + 2];
                sumA += src[oIn + 3] - src[oOut + 3];
            }
        }
    }

    internal readonly record struct MapDimensions(
        int OutW, int OutH,
        int SrcW, int SrcH,
        int Margin,
        int NotchW, int NotchH,
        int NotchOffX, int NotchOffY,
        int CaptureShiftX, int CaptureShiftY);

    private readonly record struct MapGeometry(
        double HalfX, double HalfY, double Cx, double Cy, double MinHalf,
        double TopR, double BottomR, double UChroma, double Distort, double VerticalBalance);

    private readonly record struct SampleBounds(int SrcW, int MaxX, int MaxY);

    private bool EnsureMaps(GlassParams p, MapDimensions d)
    {
        p.TopCornerRadius = Math.Round(p.TopCornerRadius * 2.0) / 2.0;
        p.BottomCornerRadius = Math.Round(p.BottomCornerRadius * 2.0) / 2.0;

        if (IsMapCacheValid(p, d))
            return false;

        UpdateMapCacheState(p, d);
        EnsureMapBuffers(d.OutW, d.OutH);

        var geom = CreateMapGeometry(p, d);
        var bounds = new SampleBounds(d.SrcW, d.SrcW - 1, d.SrcH - 1);

        void BuildRows(int y0, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = 0; x < d.OutW; x++)
                {
                    ComputeRefractionSample(x, y, p, d, geom, bounds);
                }
            }
        }

        if (d.OutH >= 64)
            Parallel.ForEach(Partitioner.Create(0, d.OutH), ParallelOpts, range => BuildRows(range.Item1, range.Item2));
        else
            BuildRows(0, d.OutH);

        return true;
    }

    private bool IsMapCacheValid(GlassParams p, MapDimensions d)
    {
        return !_mapsDirty
            && _outW == d.OutW && _outH == d.OutH
            && _srcW == d.SrcW && _srcH == d.SrcH
            && _margin == d.Margin
            && _idxR.Length == d.OutW * d.OutH
            && _mapNotchW == d.NotchW && _mapNotchH == d.NotchH
            && _mapNotchOffX == d.NotchOffX && _mapNotchOffY == d.NotchOffY
            && _mapCaptureShiftX == d.CaptureShiftX && _mapCaptureShiftY == d.CaptureShiftY
            && Math.Abs(_mapTopCornerRadius - p.TopCornerRadius) < 1e-3
            && Math.Abs(_mapBottomCornerRadius - p.BottomCornerRadius) < 1e-3
            && Math.Abs(_mapZRadius - p.ZRadius) < 1e-4;
    }

    private void UpdateMapCacheState(GlassParams p, MapDimensions d)
    {
        _mapsDirty = false;
        _outW = d.OutW; _outH = d.OutH; _srcW = d.SrcW; _srcH = d.SrcH; _margin = d.Margin;
        _mapNotchW = d.NotchW; _mapNotchH = d.NotchH; _mapNotchOffX = d.NotchOffX; _mapNotchOffY = d.NotchOffY;
        _mapCaptureShiftX = d.CaptureShiftX; _mapCaptureShiftY = d.CaptureShiftY;
        _mapTopCornerRadius = p.TopCornerRadius;
        _mapBottomCornerRadius = p.BottomCornerRadius;
        _mapZRadius = p.ZRadius;
    }

    private void EnsureMapBuffers(int outW, int outH)
    {
        int n = outW * outH;
        int needed = n * 4;
        if (_outBuffer.Length < needed)
        {
            _outBuffer = new byte[needed];
            _blurTmp = new byte[needed];
        }
        if (_idxR.Length < n)
        {
            _idxR = new int[n]; _auxR = new int[n];
            _idxG = new int[n]; _auxG = new int[n];
            _idxB = new int[n]; _auxB = new int[n];
        }
        if (_edgeMask.Length < n)
            _edgeMask = new byte[n];
    }

    private MapGeometry CreateMapGeometry(GlassParams p, MapDimensions d)
    {
        double halfX = d.NotchW * 0.5;
        double halfY = d.NotchH * 0.5;
        double cx = d.NotchOffX + (d.NotchW - 1) * 0.5;
        double cy = d.NotchOffY + (d.NotchH - 1) * 0.5;
        double minHalf = Math.Min(halfX, halfY);

        double topR = Math.Clamp(p.TopCornerRadius * _outScale, 0.0, minHalf);
        double bottomR = Math.Clamp(p.BottomCornerRadius * _outScale, 0.0, minHalf);
        double uChroma = Math.Clamp(p.ChromaticAberration, 0.0, 2.0);
        double distort = Math.Clamp(p.Distortion, 0.0, 2.0);
        double aspect = Math.Clamp(d.NotchH / Math.Max((double)d.NotchW, 1.0) * 2.5, 0.0, 1.0);
        double verticalBalance = 0.68 + 0.32 * aspect;

        return new MapGeometry(halfX, halfY, cx, cy, minHalf, topR, bottomR, uChroma, distort, verticalBalance);
    }

    private void ComputeRefractionSample(
        int x, int y, GlassParams p, MapDimensions d, MapGeometry geom, SampleBounds bounds)
    {
        int idx = y * d.OutW + x;
        double lx = x - geom.Cx;
        double ly = y - geom.Cy;
        double baseX = x + d.Margin - d.CaptureShiftX;
        double baseY = y + d.Margin - d.CaptureShiftY;

        double inside = -RoundedRectSdf(lx, ly, geom.HalfX, geom.HalfY, geom.TopR, geom.BottomR);
        if (inside <= 0.0)
        {
            _edgeMask[idx] = 0;
            SetSample(baseX, baseY, bounds, _idxG, _auxG, idx);
            SetSample(baseX, baseY, bounds, _idxR, _auxR, idx);
            SetSample(baseX, baseY, bounds, _idxB, _auxB, idx);
            return;
        }

        double pxNorm = lx / Math.Max(geom.HalfX, 1.0);
        double pyNorm = ly / Math.Max(geom.HalfY, 1.0);
        double distNorm = Math.Clamp(inside / Math.Max(geom.MinHalf, 1.0), 0.0, 1.0);

        double fVal = ExponentialRefract(distNorm, p.RefractionA, p.RefractionB, p.RefractionC, p.RefractionD);
        double fPow = Math.Max(p.FPower, 0.1);
        double refractFactor = Math.Pow(Math.Max(fVal, 0.0001), fPow);

        double bend = Math.Max(p.EdgeBend, 0.1);
        double dispX = (pxNorm * refractFactor - pxNorm) * geom.HalfX * bend;
        double dispY = (pyNorm * refractFactor - pyNorm) * geom.HalfY * bend;

        if (geom.Distort > 0.0)
        {
            double noiseX = ValueNoise(lx * 0.045, ly * 0.045) * 2.0 - 1.0;
            double noiseY = ValueNoise(lx * 0.045 + 19.7, ly * 0.045 + 43.1) * 2.0 - 1.0;
            dispX += noiseX * geom.Distort * 2.25 * (1.0 - distNorm);
            dispY += noiseY * geom.VerticalBalance * geom.Distort * 2.25 * (1.0 - distNorm);
        }

        _edgeMask[idx] = 0;
        double caS = Math.Min(geom.UChroma * (1.0 - distNorm) * 3.0, 8.0);
        double dispLen = Math.Sqrt(dispX * dispX + dispY * dispY);
        double caDirX = dispLen > 1e-6 ? dispX / dispLen : 0.0;
        double caDirY = dispLen > 1e-6 ? dispY / dispLen : 0.0;
        double caX = caDirX * caS;
        double caY = caDirY * geom.VerticalBalance * caS;

        baseX += dispX;
        baseY += dispY;

        SetSample(baseX, baseY, bounds, _idxG, _auxG, idx);
        SetSample(baseX + caX, baseY + caY, bounds, _idxR, _auxR, idx);
        SetSample(baseX - caX, baseY - caY, bounds, _idxB, _auxB, idx);
    }

    private static double RoundedRectSdf(double px, double py, double bx, double by,
        double topRadius, double bottomRadius)
    {
        double r = py < 0.0 ? topRadius : bottomRadius;
        double qx = Math.Abs(px) - (bx - r);
        double qy = (py < 0.0 ? -py : py) - (by - r);
        if (qx > 0.0 && qy > 0.0) return Math.Sqrt(qx * qx + qy * qy) - r;
        if (qx > 0.0) return qx - r;
        if (qy > 0.0) return qy - r;
        return Math.Max(qx - r, qy - r);
    }

    private static double Smoother01(double x)
    {
        double t = Math.Clamp(x, 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    private static double Hash(double px, double py)
    {
        double s = Math.Sin(px * 127.1 + py * 311.7) * 43758.5453;
        return s - Math.Floor(s);
    }

    private static double ValueNoise(double px, double py)
    {
        double ix = Math.Floor(px), iy = Math.Floor(py);
        double fx = Smoother01(px - ix), fy = Smoother01(py - iy);
        double top = Hash(ix, iy) + (Hash(ix + 1.0, iy) - Hash(ix, iy)) * fx;
        double bottom = Hash(ix, iy + 1.0) + (Hash(ix + 1.0, iy + 1.0) - Hash(ix, iy + 1.0)) * fx;
        return top + (bottom - top) * fy;
    }

    private static void SetSample(double sx, double sy, in SampleBounds bounds,
        int[] idxArr, int[] auxArr, int i)
    {
        int ix = (int)Math.Floor(sx);
        int iy = (int)Math.Floor(sy);
        double fx = sx - ix;
        double fy = sy - iy;

        int flags = 0;
        if (ix < 0) { ix = 0; fx = 0; }
        else if (ix >= bounds.MaxX) { ix = bounds.MaxX; fx = 0; }
        else flags |= 1; // has right neighbour

        if (iy < 0) { iy = 0; fy = 0; }
        else if (iy >= bounds.MaxY) { iy = bounds.MaxY; fy = 0; }
        else flags |= 2; // has bottom neighbour

        int wx = (int)Math.Round(fx * 256.0);
        if (wx < 0) wx = 0; else if (wx > 256) wx = 256;
        int wy = (int)Math.Round(fy * 256.0);
        if (wy < 0) wy = 0; else if (wy > 256) wy = 256;

        idxArr[i] = (iy * bounds.SrcW + ix) << 2;
        auxArr[i] = flags | (wx << 2) | (wy << 12);
    }
    internal static double ExponentialRefract(double x, double a, double b, double c, double d)
    {
        double exponent = -d * x - a;
        double baseVal = Math.Max(c * 2.718281828459045, 0.0001);
        return 1.0 - b * Math.Pow(baseVal, exponent);
    }
}
