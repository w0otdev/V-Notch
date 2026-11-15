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
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnGlassMapParams
    {
        public int OutW;
        public int OutH;
        public int SrcW;
        public int SrcH;
        public int Margin;
        public int NotchW;
        public int NotchH;
        public int NotchOffX;
        public int NotchOffY;
        public int CaptureShiftX;
        public int CaptureShiftY;
        public double OutScale;
        public double TopCornerRadius;
        public double BottomCornerRadius;
        public double EdgeBend;
        public double ChromaticAberration;
        public double Distortion;
        public double FPower;
        public double RefractionA;
        public double RefractionB;
        public double RefractionC;
        public double RefractionD;
        public double Refraction;
        public double ZRadius;
        public int BevelMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnGlassBackdropOpticsOut
    {
        public byte Red;
        public byte Green;
        public byte Blue;
        public byte Reserved;
        public double LightX;
        public double LightY;
        public double Contrast;
        public int OutsideRgb;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_build_refraction_lut")]
    private static unsafe partial int NativeBuildRefractionLut(
        double fPower,
        double refractionA,
        double refractionB,
        double refractionC,
        double refractionD,
        double* outLut,
        int lutLen);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_build_refraction_rows")]
    private static unsafe partial int NativeBuildRefractionRows(
        in VnGlassMapParams mapParams,
        double* lut,
        int lutLen,
        int yStart,
        int yEnd,
        int* outMap);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_refract_gather")]
    private static unsafe partial int NativeRefractGather(
        byte* src,
        byte* dst,
        int* refractMap,
        int srcStride,
        int startPixel,
        int endPixel,
        byte adjustColor,
        float satFactor,
        float brightAdd);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_analyze_backdrop_samples")]
    private static unsafe partial int NativeAnalyzeBackdropSamples(
        int* rgbSamples,
        int sampleLen,
        int columns,
        int rows,
        out VnGlassBackdropOpticsOut outOptics);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_sample_backdrop_optics")]
    private static unsafe partial int NativeSampleBackdropOptics(
        byte* dibBits,
        int srcW,
        int srcH,
        int sampleX,
        int sampleY,
        int sampleW,
        int sampleH,
        out VnGlassBackdropOpticsOut outOptics);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gaussian_box_radii")]
    private static unsafe partial int NativeGaussianBoxRadii(
        double sigma,
        int passes,
        int* outRadii,
        int radiiCap,
        out int outWritten);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_compute_capture_interval_ms")]
    private static partial int NativeComputeCaptureIntervalMs(
        int configuredFps,
        int displayRefreshHz,
        byte ultimateGpu,
        out double outIntervalMs);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_should_submit_frame")]
    private static partial int NativeShouldSubmitFrame(
        long nowTicks,
        long lastSubmitTicks,
        long stopwatchFreq,
        double activeIntervalMs,
        byte forcePresent,
        out byte outShouldSubmit);

    public const int MaxTargetFps = 1000;

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
        public double EdgeHighlight { get; set; }
        public double Specular { get; set; }
        public double Fresnel { get; set; }
        public int Variant { get; set; }
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
    private int _configuredFps;
    private double _frameWorkMs;
    private long _nextDisplayRefreshProbe;
    private int _displayRefreshHz;
    private volatile bool _animating;
    private volatile bool _presentationPaused;
    private readonly AutoResetEvent _idleWakeEvent = new(false);
    private int _disposed;
    private long _refreshVersion;
    private long _renderedRefreshVersion;
    private bool ForceRefreshNeeded => Volatile.Read(ref _refreshVersion) != Volatile.Read(ref _renderedRefreshVersion);

    // A completed frame acknowledges only requests captured before it started.
    private void CompleteRefresh(long version) => Volatile.Write(ref _renderedRefreshVersion, version);

    private readonly object _sync = new();
    private GlassParams _params = GlassParams.Default;
    private double _blurSigma;
    private int[] _blurPassRadii = [0, 0, 0];
    private volatile bool _mapsDirty;

    private Thread? _worker;
    private int _renderGeneration;
    private bool _hasVisibleFrame;
    private volatile bool _isActive;
    private volatile bool _presentInFlight;
    private volatile bool _waitingForGpuPresentation;
    private const uint RenderTimerPeriodMs = 1;
    private int _renderTimerPeriodRequested;
    private SafeWaitHandle? _waitableTimer;
    private IntPtr[]? _renderWaitHandles;

    // Owned exclusively by the worker thread once Start() runs.
    private WriteableBitmap? _bitmap;
    private TranslateTransform? _hostTransform;
    private double _presentSubX, _presentSubY;
    private byte[] _outBuffer = Array.Empty<byte>();
    private byte[] _sourceBlurBuffer = Array.Empty<byte>();
    private byte[] _sourceBlurTmp = Array.Empty<byte>();

    private IntPtr _memDc;
    private IntPtr _dibBmp;
    private IntPtr _dibBits;
    private IntPtr _oldBmp;
    private int _dibW, _dibH;

    private int[] _refractMap = Array.Empty<int>();

    private int _outW, _outH;
    private int _srcW, _srcH;
    private int _margin;
    private double _outScale = 1.0;
    private double _mapTopCornerRadius = -1;
    private double _mapBottomCornerRadius = -1;
    private int _mapNotchW = -1, _mapNotchH = -1, _mapNotchOffX = -1, _mapNotchOffY = -1;
    private int _mapCaptureShiftX = int.MinValue, _mapCaptureShiftY = int.MinValue;

    private MagnifierCaptureSource? _mag;
    private bool _magReady;
    private int _magFailStreak = 0;
    private long _nextMagnifierRetryTicks;
    private long _magnifierProbeUntilTicks;
    private int _magnifierInitQueued;

    private volatile bool _hideFromCapture;
    private volatile bool _exactBitBltCapture;
    private int _currentDisplayAffinity = -1;
    private IntPtr _currentDisplayAffinityHwnd;
    private readonly object _displayAffinitySync = new();
    private int _captureAffinityFlushPending;
    // Legacy API retained for saved presets. Only Ghost mode may hide windows.
    public bool HideFromScreenCapture
    {
        get => _hideFromCapture;
        set
        {
            if (_hideFromCapture == value) return;
            _hideFromCapture = value;
            if (_isActive)
            {
                ForceRefresh();
            }
        }
    }

    private double _bitmapDpi = 96;

    private volatile bool _gpuMode;
    private volatile bool _ultimateGpuMode;
    public bool UseUltimateGpu
    {
        get => _ultimateGpuMode;
        set
        {
            if (_ultimateGpuMode == value) return;
            _ultimateGpuMode = value;
            double nextInterval = ComputeCaptureIntervalMs(Volatile.Read(ref _configuredFps), Volatile.Read(ref _displayRefreshHz), value);
            Volatile.Write(ref _activeIntervalMs, nextInterval);
            if (_gpuMode)
            {
                DisposeGpuPresenter();
                if (TryEnableGpuPresenter(out Exception? error))
                    ForceRefresh();
                else
                    OnD3DPresenterFailed(error ?? new InvalidOperationException("GPU presenter reinit failed."));
            }
        }
    }
    public double ActiveIntervalMs => Volatile.Read(ref _activeIntervalMs);

    public bool ShouldThrottleFrameUpdate(ref long lastTick, bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        double intervalMs = Volatile.Read(ref _activeIntervalMs);
        byte forceFlag = force ? (byte)1 : (byte)0;
        if (NativeShouldSubmitFrame(now, lastTick, Stopwatch.Frequency, intervalMs, forceFlag, out byte shouldSubmit) == 0 &&
            shouldSubmit == 0)
        {
            return true;
        }
        lastTick = now;
        return false;
    }
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

    public int LastPresentedCaptureOriginX
    {
        get
        {
            lock (_sync)
                return _hasPresentedGpuGeometry ? _lastPresentedGpuGeometry.CaptureOriginX : int.MinValue;
        }
    }

    public int LastPresentedCaptureOriginY
    {
        get
        {
            lock (_sync)
                return _hasPresentedGpuGeometry ? _lastPresentedGpuGeometry.CaptureOriginY : int.MinValue;
        }
    }

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
    private GlassCaptureImage? _gpuCaptureImage;
    public bool CropGpuPresentationToCapture { get; set; }
    private int _gpuFailureSignaled;

    // Unchanged-frame suppression. Re-presenting an identical backdrop is a pure
    private volatile bool _idle;
    internal Action? OnGpuFrameUploaded { get; set; }
    private readonly GlassFrameHistory _frameHistory = new();
    private long _lastPresentTicks;
    private bool _hasUploadedGpuFrame;
    private bool _hasPresentedCpuFrame;
    private double _lastPresentedSubX, _lastPresentedSubY;
    private double _lastPresentedSaturation, _lastPresentedBrightness;

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

        UpdateFps(activeFps);
    }

    private readonly string _logTag;

    public void UpdateFps(int activeFps)
    {
        int target = activeFps <= 0 ? AnimationConfig.TargetFps : activeFps;
        int active = Math.Clamp(target, AnimationConfig.MinFps, MaxTargetFps);
        Volatile.Write(ref _configuredFps, active);
        double intervalMs = ComputeCaptureIntervalMs(active, Volatile.Read(ref _displayRefreshHz), _ultimateGpuMode);
        Volatile.Write(ref _activeIntervalMs, intervalMs);
        _d3dPresenter?.SetFrameIntervalMs(intervalMs);
        if (_isActive) { Interlocked.Increment(ref _refreshVersion); SignalWakeEventSafe(); }
    }

    internal static double ComputeCaptureIntervalMs(int configuredFps, int displayRefreshHz, bool ultimateGpu = false) =>
        NativeComputeCaptureIntervalMs(configuredFps, displayRefreshHz, ultimateGpu ? (byte)1 : (byte)0, out double intervalMs) == 0 && intervalMs > 0.0
            ? intervalMs
            : 1000.0 / (displayRefreshHz > 0 && !ultimateGpu ? Math.Min(configuredFps, displayRefreshHz) : Math.Max(1, configuredFps));

    private void RefreshDisplayCadence()
    {
        long now = Environment.TickCount64;
        if (now < _nextDisplayRefreshProbe) return;
        _nextDisplayRefreshProbe = now + 1000;
        int? detected = GlassDisplayCadence.GetRefreshRate(_getHwnd());
        int hz = detected ?? 0;
        if (hz == _displayRefreshHz) return;
        Volatile.Write(ref _displayRefreshHz, hz);
        double intervalMs = ComputeCaptureIntervalMs(Volatile.Read(ref _configuredFps), hz, _ultimateGpuMode);
        Volatile.Write(ref _activeIntervalMs, intervalMs);
        _d3dPresenter?.SetFrameIntervalMs(intervalMs);
        RuntimeLog.Log(LogCategory, $"[{_logTag}] Display: {hz} Hz; capture limit: {Volatile.Read(ref _configuredFps)} FPS (ultimateGpu={_ultimateGpuMode})");
    }

    internal static double ChooseLockedFrameIntervalMs(double configuredIntervalMs) =>
        Math.Max(1000.0 / MaxTargetFps, configuredIntervalMs);

    public bool IsActive => _isActive;

    public void SetAnimating(bool animating)
    {
        if (_animating == animating) return;
        _animating = animating;
        ForceRefresh();
    }

    public void SetIdle(bool idle)
    {
        if (_idle == idle) return;
        _idle = idle;
        if (!idle) ForceRefresh();
    }

    internal static double ChooseFrameIntervalMs(double activeIntervalMs, bool idle, bool animating,
        bool hasFrame, long unchangedMilliseconds, bool forceRefresh = false) =>
        idle && !animating && hasFrame && unchangedMilliseconds >= 250 && !forceRefresh
            ? Math.Max(100, activeIntervalMs) : activeIntervalMs;

    internal static double ChooseProcessingIntervalMs(double intervalMs, double workMs, bool gpuMode) =>
        gpuMode ? intervalMs : Math.Max(intervalMs, Math.Min(100, workMs * 1.25));

    public void SetPresentationPaused(bool paused)
    {
        bool wasPaused = _presentationPaused;
        _presentationPaused = paused;
        if (paused) ReleaseRenderTimerPeriod();
        if (wasPaused && !paused)
        {
            // The held frame used the pre-hover capture rectangle. Force the next
            Volatile.Write(ref _lastRegionFetchMs, double.NegativeInfinity);
            Interlocked.Increment(ref _refreshVersion);
            SignalWakeEventSafe();
        }
    }

    public void ForceRefresh()
    {
        Interlocked.Increment(ref _refreshVersion);
        if (!_waitingForGpuPresentation)
            SignalWakeEventSafe();
    }

    private void SignalWakeEventSafe()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _idleWakeEvent.Set(); }
        catch (ObjectDisposedException) { /* disposed concurrently */ }
    }

    public void SetParams(GlassParams p)
    {
        lock (_sync)
        {
            bool cpuMapChanged =
                Math.Abs(p.PowerFactor - _params.PowerFactor) > 1e-4 ||
                Math.Abs(p.RefractionA - _params.RefractionA) > 1e-4 ||
                Math.Abs(p.RefractionB - _params.RefractionB) > 1e-4 ||
                Math.Abs(p.RefractionC - _params.RefractionC) > 1e-4 ||
                Math.Abs(p.RefractionD - _params.RefractionD) > 1e-4 ||
                Math.Abs(p.FPower - _params.FPower) > 1e-4 ||
                Math.Abs(p.EdgeBend - _params.EdgeBend) > 1e-4 ||
                Math.Abs(p.ChromaticAberration - _params.ChromaticAberration) > 1e-4 ||
                Math.Abs(p.Distortion - _params.Distortion) > 1e-4 ||
                Math.Abs(p.TopCornerRadius - _params.TopCornerRadius) > 1e-4 ||
                Math.Abs(p.BottomCornerRadius - _params.BottomCornerRadius) > 1e-4 ||
                Math.Abs(p.Refraction - _params.Refraction) > 1e-4 ||
                Math.Abs(p.ZRadius - _params.ZRadius) > 1e-4 ||
                p.BevelMode != _params.BevelMode;
            bool anyChanged = cpuMapChanged ||
                Math.Abs(p.Noise - _params.Noise) > 1e-4 ||
                Math.Abs(p.GlowWeight - _params.GlowWeight) > 1e-4 ||
                Math.Abs(p.GlowBias - _params.GlowBias) > 1e-4 ||
                Math.Abs(p.GlowEdge0 - _params.GlowEdge0) > 1e-4 ||
                Math.Abs(p.GlowEdge1 - _params.GlowEdge1) > 1e-4 ||
                Math.Abs(p.Saturation - _params.Saturation) > 1e-4 ||
                Math.Abs(p.Brightness - _params.Brightness) > 1e-4 ||
                Math.Abs(p.EdgeHighlight - _params.EdgeHighlight) > 1e-4 ||
                Math.Abs(p.Specular - _params.Specular) > 1e-4 ||
                Math.Abs(p.Fresnel - _params.Fresnel) > 1e-4 ||
                p.Variant != _params.Variant;

            _params = p;
            if (cpuMapChanged) _mapsDirty = true;
            if (anyChanged)
            {
                Interlocked.Increment(ref _refreshVersion);
                SignalWakeEventSafe();
            }
        }
    }

    internal static double BlurAmountToSigma(double amount, double dpiScale = 1.0)
    {
        double normalized = double.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : 0;
        // Fine control for clear glass near zero; retain the maximum frosting.
        return 28.0 * normalized * normalized * Math.Clamp(dpiScale, 0.5, 4.0);
    }

    public void SetBlur(double gaussianSigma)
    {
        double sigma = double.IsFinite(gaussianSigma) ? Math.Clamp(gaussianSigma, 0, 60) : 0;
        lock (_sync)
        {
            if (sigma == _blurSigma) return;
            _blurSigma = sigma;
            _blurPassRadii = GaussianBoxRadii(sigma);
            SignalWakeEventSafe();
            Interlocked.Increment(ref _refreshVersion);
            _mapsDirty = true;
        }
    }

    public void SetCaptureExclusion(bool exclude) { /* no-op */ }

    public bool HasPresentedFrame => _hasVisibleFrame;
    internal long FrameSubmissionCount => Interlocked.Read(ref _totalPresentedFrames);
    internal double LastSubmissionLatencyMs => _d3dPresenter?.LastQueueMs ?? 0;
    private long _totalPresentedFrames;

    public void Start()
    {
        if (_isActive || Volatile.Read(ref _disposed) != 0) return;
        _isActive = true;
        _nextDisplayRefreshProbe = 0;
        ++_renderGeneration;
        _hasVisibleFrame = false;
        _cachedRegion = null;
        _lastRegionFetchMs = double.NegativeInfinity;
        if (_gpuMode && _d3dPresenter == null && !TryEnableGpuPresenter(out var error))
            OnD3DPresenterFailed(error ?? new InvalidOperationException("GPU presenter restart failed."));
        if (_gpuMode) _d3dPresenter?.BeginSession(Volatile.Read(ref _renderGeneration));
        _presentationPaused = false;
        _presentInFlight = false;
        _hasUploadedGpuFrame = false;
        _hasLastSubmittedGpuFrame = false;
        _hasPresentedCpuFrame = false;
        Interlocked.Increment(ref _refreshVersion);
        SignalWakeEventSafe();

        _exactBitBltCapture = WindowAnimationPolicy.InvisibleMode && SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
        if (!WindowAnimationPolicy.InvisibleMode) SetWindowDisplayAffinitySafe(WDA_NONE);
        if (!_exactBitBltCapture && _mag == null)
        {
            try
            {
                AcquireMagnifier();
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

    public void PrepareGpuResources()
    {
        if (!_gpuMode) return;
        try { _d3dPresenter?.PrepareResources(); }
        catch (Exception ex) { OnD3DPresenterFailed(ex); }
    }

    /// <summary>
    /// Acquires the shared magnifier source and registers this window as excluded
    /// from capture. Every acquire path must go through here so Stop() can pair
    /// the release with UnregisterExcludeHwndProvider. Callers handle exceptions.
    /// </summary>
    private void AcquireMagnifier()
    {
        _mag = MagnifierCaptureSource.AcquireShared(_getHwnd());
        _mag.RegisterExcludeHwndProvider(_getHwnd);
        _magReady = _mag.IsReady;
    }

    public void Dispose()
    {
        ReleaseGpuLens();
        Stop(retainGpuResources: false);
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _worker == null)
        {
            ReleaseRenderTimerPeriod();
            DisposeWaitableTimer();
            try { _idleWakeEvent.Dispose(); }
            catch { /* best-effort handle cleanup */ }
        }
    }

    public void Stop() => Stop(retainGpuResources: false);

    public void Stop(bool retainGpuResources)
    {
        if (!_isActive)
        {
            if (!retainGpuResources) DisposeGpuPresenter();
            return;
        }
        _isActive = false;
        ++_renderGeneration;
        _hasVisibleFrame = false;
        ClearLiveRegion();
        _presentationPaused = false;
        _exactBitBltCapture = false;
        SignalWakeEventSafe();
        // The worker owns the timer until its wait has returned and it exits.
        // Closing that handle here races WaitForMultipleObjects during Stop.

        // Safety: clear any display affinity a previous build may have set.
        SetWindowDisplayAffinitySafe(WDA_NONE);

        IntPtr h = IntPtr.Zero;
        try { h = _getHwnd(); } catch { /* best-effort handle retrieval during shutdown */ }
        if (_mag != null)
        {
            _mag.UnregisterExcludeHwndProvider(_getHwnd);
            MagnifierCaptureSource.ReleaseShared(h);
        }
        _mag = null;
        _magReady = false;
        _magFailStreak = 0;

        try
        {
            if (!retainGpuResources) DisposeGpuPresenter();
            if (_dispatcher.CheckAccess())
                _host.Source = null;
            else
                _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => _host.Source = null));
        }
        catch { /* shutting down */ }

        try
        {
            if (retainGpuResources) _d3dPresenter?.SuspendPresentation();
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
        try
        {
            lock (_displayAffinitySync)
            {
                var hwnd = _getHwnd();
                if (hwnd == IntPtr.Zero)
                {
                    _currentDisplayAffinityHwnd = IntPtr.Zero;
                    _currentDisplayAffinity = -1;
                    return false;
                }
                if (_currentDisplayAffinityHwnd == hwnd && _currentDisplayAffinity == (int)affinity) return true;
                if (!SetWindowDisplayAffinity(hwnd, affinity)) return false;
                _currentDisplayAffinityHwnd = hwnd;
                _currentDisplayAffinity = (int)affinity;
                // The worker must see the committed exclusion before capturing, but
                // the UI thread must not wait for a compositor refresh on hotkey open.
                Interlocked.Exchange(ref _captureAffinityFlushPending, 1);
                return true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"SetWindowDisplayAffinity({affinity}) failed: {ex.Message}");
            return false;
        }
    }

    private CaptureRegion? TryGetRegionOnUi()
    {
        int generation = Volatile.Read(ref _renderGeneration);
        long revision;
        lock (_liveRegionSync) revision = _liveRegionRevision;
        if (Interlocked.CompareExchange(ref _regionQueryQueued, 1, 0) == 0)
        {
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
                {
                    try
                    {
                        if (!_isActive || generation != Volatile.Read(ref _renderGeneration)) return;
                        var region = _regionProvider();
                        lock (_liveRegionSync)
                        {
                            if (revision != _liveRegionRevision) return;
                            _queriedRegion = region;
                            _queriedRegionGeneration = generation;
                            _regionQueryCompleted = true;
                        }
                        SignalWakeEventSafe();
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Log(LogCategory, $"Region query failed: {ex.Message}");
                    }
                    finally { Interlocked.Exchange(ref _regionQueryQueued, 0); }
                }));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _regionQueryQueued, 0);
            }
        }
        lock (_liveRegionSync)
            return _cachedRegion;
    }
}
