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
    private int _idleWakeDisposed;
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

    private AnimationFrameClock _geometryFrameClock;
    private TimeSpan? _geometryRenderingTime;

    public bool ShouldThrottleFrameUpdate(ref long lastTick, bool force = false, TimeSpan? renderingTime = null) =>
        ShouldThrottleFrameUpdate(ref lastTick, Stopwatch.GetTimestamp(), force, renderingTime);

    internal bool ShouldThrottleFrameUpdate(ref long lastTick, long now, bool force = false, TimeSpan? renderingTime = null)
    {
        if (force || lastTick <= 0)
        {
            _geometryFrameClock.Reset(now);
            _geometryRenderingTime = renderingTime;
            lastTick = now;
            return false;
        }

        // WPF can raise Rendering again for the same composition frame after layout.
        // Callback wall time can cross a deadline without a new frame to display.
        if (renderingTime.HasValue)
        {
            if (_geometryRenderingTime == renderingTime) return true;
            _geometryRenderingTime = renderingTime;
        }

        // Advance deadlines rather than starting a new interval at each callback.
        // Otherwise a 120 FPS target on a 144 Hz compositor falls to 72 updates/s.
        double intervalMs = Volatile.Read(ref _activeIntervalMs);
        if (!_geometryFrameClock.TryAdvance(now, 1000.0 / intervalMs, out _)) return true;
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

    public bool SetGpuMode(bool enabled, Action<GpuGeometry>? onGeometry, Action<Exception>? onFailure = null, bool ultimateGpu = false)
    {
        _onGpuGeometry = onGeometry;
        _onGpuFailure = onFailure;
        bool adapterModeChanged = _ultimateGpuMode != ultimateGpu;
        _ultimateGpuMode = ultimateGpu;
        double nextInterval = ComputeCaptureIntervalMs(Volatile.Read(ref _configuredFps), Volatile.Read(ref _displayRefreshHz), ultimateGpu);
        Volatile.Write(ref _activeIntervalMs, nextInterval);
        if (!enabled)
        {
            UsesNativeRefraction = false;
            ReleaseGpuLens();
            _gpuMode = false;
            Interlocked.Exchange(ref _gpuFailureSignaled, 0);
            ResetGpuGeometryTracking();
            DisposeGpuPresenter();
            _mapsDirty = true;
            return true;
        }

        if (adapterModeChanged && _d3dPresenter != null)
            DisposeGpuPresenter();

        Interlocked.Exchange(ref _gpuFailureSignaled, 0);
        ResetGpuGeometryTracking();
        if (!TryEnableGpuPresenter(out Exception? error))
        {
            _gpuMode = false;
            _mapsDirty = true;
            RuntimeLog.Log(LogCategory, $"D3DImage presenter unavailable; using CPU fallback: {error?.Message}");
            return false;
        }

        _d3dPresenter?.SetFrameIntervalMs(nextInterval);
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
                _maxRegionH + GpuSamplingMarginLimit * 2,
                ultimateGpuMode: _ultimateGpuMode,
                useGpuCapture: true);
            _d3dPresenter.SetFrameIntervalMs(Volatile.Read(ref _activeIntervalMs));
            _d3dPresenter.FramePresented += OnD3DFramePresented;
            _d3dPresenter.RefreshRequested += ForceRefresh;
            _d3dPresenter.Failed += OnD3DPresenterFailed;
            if (_isActive) _d3dPresenter.BeginSession(Volatile.Read(ref _renderGeneration));
        }

        RefreshBitmapDpi(updateHostDimensions: false);
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
        presenter.RefreshRequested -= ForceRefresh;
        presenter.Failed -= OnD3DPresenterFailed;
        if (ReferenceEquals(_host.Source, presenter.ImageSource) || ReferenceEquals(_host.Source, _gpuCaptureImage?.Image))
            _host.Source = null;
        _gpuCaptureImage = null;
        presenter.Dispose();
        ResetGpuGeometryTracking();
    }

    private void ResetGpuGeometryTracking()
    {
        lock (_sync)
        {
            _lastPresentedGpuGeometry = default;
            _hasPresentedGpuGeometry = false;
        }
        _hasUploadedGpuFrame = false;
        _hasPresentedCpuFrame = false;
        _hasVisibleFrame = false;
        Interlocked.Increment(ref _refreshVersion);
    }

    private void OnD3DFramePresented(GpuGeometry geom)
    {
        if (!_isActive) return;
        _hasVisibleFrame = true;
        Interlocked.Increment(ref _dbgPresentCount);
        Interlocked.Increment(ref _totalPresentedFrames);

        bool geometryChanged;
        lock (_sync)
        {
            geometryChanged = !_hasPresentedGpuGeometry || !_lastPresentedGpuGeometry.Equals(geom);
            if (geometryChanged)
            {
                _lastPresentedGpuGeometry = geom;
                _hasPresentedGpuGeometry = true;
            }
        }
        if (geometryChanged)
            _onGpuGeometry?.Invoke(geom);

        // Geometry is applied before the initialized source becomes visible so
        var presenter = _d3dPresenter;
        if (_gpuMode && presenter != null)
        {
            ImageSource source = presenter.ImageSource;
            if (CropGpuPresentationToCapture)
            {
                if (_gpuCaptureImage == null || !ReferenceEquals(_gpuCaptureImage.Source, source))
                    _gpuCaptureImage = new GlassCaptureImage(source, SurfaceWidth, SurfaceHeight);
                _gpuCaptureImage.SetCaptureSize((int)geom.SrcW, (int)geom.SrcH);
                source = _gpuCaptureImage.Image;
            }
            if (!ReferenceEquals(_host.Source, source)) _host.Source = source;
        }
        if (_waitingForGpuPresentation) SignalWakeEventSafe();
    }

    public ImageSource? ImageSource => _d3dPresenter?.ImageSource;

    // Morphing windows can reveal pixels beyond the previous frame's small
    // bounds before capture catches up. Fill a stable envelope for those hosts.
    private volatile bool _captureFullSurface;
    public bool CaptureFullSurface
    {
        get => _captureFullSurface;
        set
        {
            if (_captureFullSurface == value) return;
            _captureFullSurface = value;
            Interlocked.Increment(ref _refreshVersion);
        }
    }

    // Keep the desktop texture stationary while the lens moves inside it.
    // Recentring every capture makes texture uploads and HWND movement race.
    private (int X, int Y, int Width, int Height)? _fullSurfaceCaptureBounds;

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
        int failureGeneration = Volatile.Read(ref _renderGeneration);

        void NotifyFailure()
        {
            if (failureGeneration != Volatile.Read(ref _renderGeneration)) return;
            try { _onGpuFailure?.Invoke(ex); }
            catch { /* fallback must not take down the UI thread */ }
            if (_isActive && !_exactBitBltCapture && _mag == null)
            {
                try
                {
                    AcquireMagnifier();
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

    private bool RefreshBitmapDpi(bool updateHostDimensions)
    {
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = _getHwnd(); }
        catch { /* best-effort handle retrieval */ }
        if (hwnd == IntPtr.Zero) return false;

        uint dpiNow = GetDpiForWindow(hwnd);
        double nextDpi = dpiNow > 0 ? dpiNow : 96.0;
        double prevDpi = Volatile.Read(ref _bitmapDpi);
        if (Math.Abs(prevDpi - nextDpi) <= 1e-4) return false;

        Volatile.Write(ref _bitmapDpi, nextDpi);
        _mapsDirty = true;
        Interlocked.Increment(ref _refreshVersion);
        if (updateHostDimensions && _gpuMode && _d3dPresenter != null)
        {
            void SyncHostDimensions()
            {
                if (!_gpuMode || _d3dPresenter == null || Volatile.Read(ref _disposed) != 0) return;
                double currentDpi = Volatile.Read(ref _bitmapDpi);
                double scale = currentDpi > 0 ? currentDpi / 96.0 : 1.0;
                _host.Width = SurfaceWidth / scale;
                _host.Height = SurfaceHeight / scale;
            }
            if (_dispatcher.CheckAccess()) SyncHostDimensions();
            else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)SyncHostDimensions);
        }
        return true;
    }

    private void RefreshDisplayCadence()
    {
        RefreshBitmapDpi(updateHostDimensions: true);
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
        ForceRefresh();
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
        SignalWakeEventSafe();
    }

    private void SignalWakeEventSafe()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _idleWakeDisposed) != 0) return;
        try { _idleWakeEvent.Set(); }
        catch (ObjectDisposedException) { /* disposed concurrently */ }
    }

    private void WakeWorkerForStop()
    {
        if (Volatile.Read(ref _idleWakeDisposed) != 0) return;
        try { _idleWakeEvent.Set(); }
        catch (ObjectDisposedException) { /* disposed concurrently */ }
    }

    private void DisposeTerminalKernelHandles()
    {
        ReleaseRenderTimerPeriod();
        DisposeWaitableTimer();
        if (Interlocked.Exchange(ref _idleWakeDisposed, 1) == 0)
        {
            try { _idleWakeEvent.Dispose(); }
            catch { /* best-effort handle cleanup */ }
        }
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
        RefreshBitmapDpi(updateHostDimensions: true);
        if (_gpuMode && _d3dPresenter == null && !TryEnableGpuPresenter(out var error))
            OnD3DPresenterFailed(error ?? new InvalidOperationException("GPU presenter restart failed."));
        if (_gpuMode) _d3dPresenter?.BeginSession(Volatile.Read(ref _renderGeneration));
        _presentationPaused = false;
        _presentInFlight = false;
        _hasUploadedGpuFrame = false;
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

        StartWorkerIfNeeded();
    }

    private void StartWorkerIfNeeded()
    {
        if (!_isActive || Volatile.Read(ref _disposed) != 0 || _worker != null) return;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ReleaseGpuLens();
        Stop(retainGpuResources: false);
        if (Volatile.Read(ref _worker) == null)
        {
            DisposeTerminalKernelHandles();
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
        WakeWorkerForStop();
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

    private void WorkerLoop(int generation)
    {
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

    private bool ExecuteWorkerFrame(int generation, Stopwatch clock, ref double nextFrameAtMs, ref double lastCaptureAtMs)
    {
        double frameStart = clock.Elapsed.TotalMilliseconds;
        RefreshDisplayCadence();
        double activeIntervalMs = ChooseLockedFrameIntervalMs(Volatile.Read(ref _activeIntervalMs));
        double frameIntervalMs = ChooseFrameIntervalMs(activeIntervalMs, _idle, _animating,
            _hasUploadedGpuFrame || _hasPresentedCpuFrame, Environment.TickCount64 - _lastPresentTicks, ForceRefreshNeeded);
        // GPU work includes completion waits; they must not lower the requested cadence.
        frameIntervalMs = ChooseProcessingIntervalMs(frameIntervalMs, _frameWorkMs, _gpuMode);
        if (_gpuMode)
        {
            if (!_presentationPaused && (frameIntervalMs < 100 || _d3dPresenter?.HasPendingFrame == true))
                RequestRenderTimerPeriod();
            else
                ReleaseRenderTimerPeriod();
        }

        // Interaction wakes resume active cadence without bypassing its cap.
        double remainingMs = lastCaptureAtMs + (_gpuMode ? frameIntervalMs : activeIntervalMs) - frameStart;
        if (remainingMs > 0)
        {
            SleepUntilRenderDeadline(remainingMs);
            return ShouldContinueWorker(generation);
        }

        if (ApplyCapturePolicy(frameIntervalMs))
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

        lastCaptureAtMs = frameStart;
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
        if ((_gpuMode && _d3dPresenter?.HasPendingFrame == true) || (!_gpuMode && _presentInFlight))
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
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            if (Volatile.Read(ref _disposed) != 0)
                DisposeTerminalKernelHandles();
            Volatile.Write(ref _worker, null);
            return;
        }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
            {
                Volatile.Write(ref _worker, null);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    DisposeTerminalKernelHandles();
                    return;
                }
                StartWorkerIfNeeded();
            }));
        }
        catch
        {
            // Ignored if dispatcher is shutting down
            Volatile.Write(ref _worker, null);
            if (Volatile.Read(ref _disposed) != 0)
                DisposeTerminalKernelHandles();
        }
    }

    private bool ApplyCapturePolicy(double frameIntervalMs)
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
                $"transfer={stats.Bytes / 1048576.0 / (sinceLastLog / 1000.0):F2} MiB/s " +
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
        double BlurSigma, int[] BlurPassRadii, int DisplayH, long RefreshVersion);

    private bool ProcessFrame(CaptureRegion region, int generation)
    {
        long refreshVersion = Volatile.Read(ref _refreshVersion);
        GlassParams p;
        double blurSigma;
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
        if (gpuMode && WindowAnimationPolicy.InvisibleMode)
        {
            var gpuDims = ComputeFrameDimensions(region, p, true, false, displayW, displayH);
            _outScale = gpuDims.OutScale;
            return ProcessDesktopGpuFrame(p, region, gpuDims, generation, blurSigma, refreshVersion);
        }
        // Keep the plain material (or Ghost-mode BitBlt) until Magnifier has a usable crop.
        // IsReady only means initialization succeeded; promoting it blindly every
        // three seconds used to freeze presentation for another 60 failed captures.
        long now = Environment.TickCount64;
        if (!_exactBitBltCapture && !_magReady && _mag?.IsReady == true &&
            (now >= _nextMagnifierRetryTicks || now < _magnifierProbeUntilTicks))
        {
            if (now >= _nextMagnifierRetryTicks)
            {
                _nextMagnifierRetryTicks = now + 3000;
                // The first probe requests a fresh asynchronous callback. Check
                // subsequent frames too, before that new frame becomes stale.
                _magnifierProbeUntilTicks = now + 100;
            }
            var probe = ComputeFrameDimensions(region, p, gpuMode, true, displayW, displayH);
            if (TryRestoreMagnifier(probe.SrcX, probe.SrcY, probe.PhysSrcW, probe.PhysSrcH))
                _magnifierProbeUntilTicks = 0;
        }
        bool useMag = !_exactBitBltCapture && _magReady && _mag != null;
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

        var ctx = new FrameRenderContext(gpuMode, useMag, mapsChanged, blurSigma, blurPassRadii, displayH, refreshVersion);
        return ExecuteFrameRender(p, region, dims, generation, ctx);
    }

    internal bool TryRestoreMagnifier(int x, int y, int width, int height)
    {
        if (_mag?.HasUsableFrame(x, y, width, height) != true) return false;
        _magFailStreak = 0;
        _magReady = true;
        if (!WindowAnimationPolicy.InvisibleMode && _exactBitBltCapture)
        {
            _exactBitBltCapture = false;
            SetWindowDisplayAffinitySafe(WDA_NONE);
        }
        return true;
    }

    private FrameDimensions ComputeFrameDimensions(
#pragma warning disable S1172
        CaptureRegion region, GlassParams p, bool gpuMode, bool useMag, int displayW, int displayH)
#pragma warning restore S1172
    {
        _ = useMag;
        RefreshBitmapDpi(updateHostDimensions: true);
        double bitmapDpi = Volatile.Read(ref _bitmapDpi);
        if (bitmapDpi <= 0) bitmapDpi = 96.0;
        // A GPU-native lens renders the active crop; pooled capacity handles growth.
        // Full-surface capture remains for the legacy presentation path.
        bool fullSurface = gpuMode && CaptureFullSurface && !UsesNativeRefraction;
        double scale = 1.0;
        int outW = Math.Max(8, (int)Math.Round(displayW * scale));
        int outH = Math.Max(8, (int)Math.Round(displayH * scale));
        double outScale = displayW > 0 ? (double)outW / displayW * (bitmapDpi / 96.0) : 1.0;

        int overscan = gpuMode
            ? 0
            : Math.Clamp((int)Math.Round(80 * (bitmapDpi / 96.0)), 64, 150);
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
            margin = fullSurface
                ? GpuSamplingMarginLimit
                : Math.Clamp(requiredMargin + 32, 32, GpuSamplingMarginLimit);
        }

        int srcW = (fullSurface) ? SurfaceWidth : bufW + margin * 2;
        int srcH = (fullSurface) ? SurfaceHeight : bufH + margin * 2;

        double inv = 1.0 / scale;
        int physMargin = (int)Math.Round(margin * inv);
        int physNotchOffX = (int)Math.Round(notchOffX * inv);
        int physSrcW = (int)Math.Round(srcW * inv);
        int physSrcH = (int)Math.Round(srcH * inv);

        int requestedSrcX = region.X - physNotchOffX - physMargin;
        if (fullSurface)
            requestedSrcX -= (_maxRegionW - displayW) / 2;
        int requestedSrcY = region.Y - physMargin;
        int srcX = ClampCaptureOriginToVirtualDesktop(requestedSrcX, physSrcW, horizontal: true);
        int srcY = ClampCaptureOriginToVirtualDesktop(requestedSrcY, physSrcH, horizontal: false);
        if (fullSurface)
        {
            int samplingPad = Math.Min(requiredMargin, margin);
            int neededLeft = ClampCaptureOriginToVirtualDesktop(
                region.X - samplingPad, displayW + samplingPad * 2, horizontal: true);
            int neededTop = ClampCaptureOriginToVirtualDesktop(
                region.Y - samplingPad, displayH + samplingPad * 2, horizontal: false);
            if (_fullSurfaceCaptureBounds is { } previous &&
                previous.Width == physSrcW && previous.Height == physSrcH &&
                ClampCaptureOriginToVirtualDesktop(previous.X, physSrcW, horizontal: true) == previous.X &&
                ClampCaptureOriginToVirtualDesktop(previous.Y, physSrcH, horizontal: false) == previous.Y &&
                neededLeft >= previous.X && neededTop >= previous.Y &&
                neededLeft + displayW + samplingPad * 2 <= previous.X + physSrcW &&
                neededTop + displayH + samplingPad * 2 <= previous.Y + physSrcH)
            {
                srcX = previous.X;
                srcY = previous.Y;
            }
            _fullSurfaceCaptureBounds = (srcX, srcY, physSrcW, physSrcH);
        }
        else
        {
            _fullSurfaceCaptureBounds = null;
        }
        int captureShiftX = srcX - requestedSrcX;
        int captureShiftY = srcY - requestedSrcY;

        int mapCaptureShiftY = captureShiftY;

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
        IntPtr screenDc = needsGdi ? GetScreenDc() : IntPtr.Zero;
        try
        {
            if (needsGdi && !EnsureGdiResources(dims.SrcW, dims.SrcH, screenDc)) return false;

            var captureParams = new BackdropCaptureParams(
                dims.SrcX, dims.SrcY, dims.SrcW, dims.SrcH,
                dims.PhysSrcW, dims.PhysSrcH, region.Y, ctx.DisplayH, ctx.UseMag, screenDc);
            if (!CaptureBackdrop(captureParams, out bool isSyntheticBackdrop))
                return false;

            if (!isSyntheticBackdrop && !ctx.GpuMode && (_dbgFrameCount & 3) == 0)
            {
                UpdateBackdropOptics(
                    dims.SrcW, dims.SrcH,
                    region.X - dims.SrcX,
                    region.Y - dims.SrcY,
                    dims.OutW, dims.OutH);
            }

            if (_presentationPaused || !_isActive || ctx.GpuMode != _gpuMode || generation != Volatile.Read(ref _renderGeneration))
                return false;

            if (ctx.GpuMode)
                return ProcessDesktopGpuFrame(p, region, dims, generation, ctx.BlurSigma, ctx.RefreshVersion, _dibBits);
            return ProcessCpuFrame(p, dims.SrcW, dims.SrcH, ctx.MapsChanged, ctx.BlurSigma, ctx.BlurPassRadii, ctx.RefreshVersion);
        }
        finally
        {
            if (screenDc != IntPtr.Zero)
                ReleaseScreenDc(screenDc);
        }
    }

    private bool CaptureBackdrop(BackdropCaptureParams cp, out bool isSyntheticBackdrop)
    {
        isSyntheticBackdrop = false;
        // Retain last presented frame on missed Magnifier frames to avoid geometry
        // jumps from fallback mixing; persistent failures cleanly disable Magnifier.
        if (cp.UseMag)
            return TryMagnifierCapture(cp);

        return BitBltCapture(cp, out isSyntheticBackdrop);
    }

    private bool TryMagnifierCapture(BackdropCaptureParams cp)
    {
        var mag = _mag;
        if (mag == null) return false;

        if (mag.CaptureInto(cp.SrcX, cp.SrcY, cp.SrcW, cp.SrcH, _dibBits, out _, out _))
        {
            _magFailStreak = 0;
            return true;
        }

        if (++_magFailStreak >= 60)
        {
            _magReady = false;
            _nextMagnifierRetryTicks = Environment.TickCount64 + 3000;
            if (!_exactBitBltCapture && WindowAnimationPolicy.InvisibleMode)
            {
                _exactBitBltCapture = SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
            }
            RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier failing repeatedly ({mag.GetDebugStatus(cp.SrcX, cp.SrcY, cp.SrcW, cp.SrcH)}); falling back to BitBlt (exactBitBlt={_exactBitBltCapture}).");
            return BitBltCapture(cp, out _);
        }
        return false;
    }

    private bool BitBltCapture(BackdropCaptureParams cp, out bool isSyntheticBackdrop)
    {
        isSyntheticBackdrop = false;
        if (!WindowAnimationPolicy.InvisibleMode)
            _exactBitBltCapture = false;
        if (!_exactBitBltCapture && !_magReady && WindowAnimationPolicy.InvisibleMode)
        {
            _exactBitBltCapture = SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
        }

        // Without capture exclusion, BitBlt would capture the notch itself.
        // Sampling below it avoids feedback but displays unrelated desktop pixels
        // at the notch's coordinates. Keep a plain material until Magnifier recovers.
        if (!_exactBitBltCapture)
        {
            isSyntheticBackdrop = true;
            return FillUnavailableBackdrop(cp.SrcW, cp.SrcH);
        }

        IntPtr screenDc = cp.ScreenDc;
        bool release = false;
        if (screenDc == IntPtr.Zero)
        {
            screenDc = GetScreenDc();
            release = true;
        }
        try
        {
            return CaptureScreenRect(_memDc, cp.SrcW, cp.SrcH, screenDc, cp.SrcX, cp.SrcY);
        }
        finally
        {
            if (release && screenDc != IntPtr.Zero)
                ReleaseScreenDc(screenDc);
        }
    }

    private unsafe bool FillUnavailableBackdrop(int width, int height)
    {
        if (_dibBits == IntPtr.Zero || width <= 0 || height <= 0) return false;
        uint color = 0xFF000000u | (uint)Volatile.Read(ref _averageBackgroundColorRgb);
        new Span<uint>((void*)_dibBits, checked(width * height)).Fill(color);
        return true;
    }

    private GpuGeometry CreateGpuGeometry(GlassParams p, CaptureRegion region, FrameDimensions dims)
    {
        double topR = Math.Clamp(p.TopCornerRadius * _outScale, 0.0, dims.MinHalf);
        double bottomR = Math.Clamp(p.BottomCornerRadius * _outScale, 0.0, dims.MinHalf);

        // The origin can be clamped to a monitor edge or retained across a resize.
        // A padding value alone is not the offset into that captured desktop frame.
        double offX = region.X - dims.SrcX + region.SubX;
        double offY = region.Y - dims.SrcY + region.SubY;

        return new GpuGeometry(
            dims.SrcW, dims.SrcH, dims.OutW, dims.OutH, offX, offY,
            topR, bottomR,
            p.PowerFactor, p.RefractionA,
            p.RefractionB * (p.Refraction > 1e-4 ? (0.15 + 1.15 * Math.Clamp(p.Refraction, 0.0, 3.0)) : 0.0),
            p.RefractionC,
            Math.Clamp(p.RefractionD * (0.25 / Math.Clamp(double.IsFinite(p.ZRadius) ? p.ZRadius : 0.25, 0.04, 1.0)), 1.2, 24.0),
            p.FPower, p.Noise,
            Math.Clamp(p.Specular * 1.15 + p.EdgeHighlight * 0.65 + p.GlowWeight * 0.12, 0.0, 2.5),
            Math.Clamp(p.Fresnel * 1.15 + p.EdgeHighlight * 0.40 + Math.Max(0.0, p.GlowBias), 0.0, 2.5),
            Math.Clamp(p.Distortion, 0.0, 2.0),
            p.GlowEdge1,
            Math.Clamp(p.ChromaticAberration, 0.0, 2.0),
            (double.IsFinite(p.EdgeBend) ? Math.Max(0.0, p.EdgeBend) : 0.0) * (p.Refraction > 1e-4 ? (0.25 + 0.95 * Math.Clamp(p.Refraction, 0.0, 3.0)) : 0.0),
            1.0 + p.Saturation, p.Brightness,
            p.BevelMode >= 1 ? 1.0 : 0.0,
            dims.SrcX, dims.SrcY);
    }

    private bool ProcessCpuFrame(
        GlassParams p, int srcW, int srcH, bool mapsChanged, double blurSigma, int[] blurPassRadii, long refreshVersion)
    {
        bool sourceUnchanged = _frameHistory.IsUnchanged(_dibBits, srcW, srcH);
        long cpuNowTicks = Environment.TickCount64;
        bool cpuUnchanged = _hasPresentedCpuFrame && !ForceRefreshNeeded && !mapsChanged &&
            sourceUnchanged &&
            Math.Abs(_presentSubX - _lastPresentedSubX) < 0.001 &&
            Math.Abs(_presentSubY - _lastPresentedSubY) < 0.001 &&
            Math.Abs(p.Saturation - _lastPresentedSaturation) < 0.001 &&
            Math.Abs(p.Brightness - _lastPresentedBrightness) < 0.001;
        if (cpuUnchanged)
            return false;

        byte[]? blurredSource = blurSigma > 0 && blurPassRadii.Length > 0 && blurPassRadii[^1] > 0
            ? BlurCapturedSource(srcW, srcH, blurPassRadii)
            : null;
        Refract(p, blurredSource);

        _hasPresentedCpuFrame = true;
        CompleteRefresh(refreshVersion);
        _frameHistory.CommitCheckedFrame();
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
                    Interlocked.Increment(ref _totalPresentedFrames);
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
        long requiredPixels = (long)w * h;
        long currentPixels = _bitmap != null ? (long)_bitmap.PixelWidth * _bitmap.PixelHeight : 0;
        bool shrinkBitmap = _bitmap != null && requiredPixels > 0 && currentPixels > requiredPixels * 4;
        bool needsBitmap = _bitmap == null || dpiChanged ||
            _bitmap.PixelWidth < w || _bitmap.PixelHeight < h || shrinkBitmap;

        if (needsBitmap)
        {
            int previousW = (dpiChanged || shrinkBitmap) ? 0 : _bitmap?.PixelWidth ?? 0;
            int previousH = (dpiChanged || shrinkBitmap) ? 0 : _bitmap?.PixelHeight ?? 0;
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
        if (current > checked(required * 4)) current = 0;
        if (current >= required) return current;

        int target = current > 0
            ? Math.Max(required, checked(current + slack))
            : checked(required + slack);
        const int quantum = 64;
        return checked(((target + quantum - 1) / quantum) * quantum);
    }

    internal static ulong ComputeSourceHash(IntPtr dibBits, int srcW, int srcH) =>
        GlassFrameFingerprint.Compute(dibBits, srcW, srcH);

    private unsafe void UpdateBackdropOptics(
        int srcW, int srcH, int sampleX, int sampleY, int sampleW, int sampleH)
    {
        if (_dibBits == IntPtr.Zero || srcW <= 0 || srcH <= 0 || sampleW <= 0 || sampleH <= 0)
            return;

        if (NativeSampleBackdropOptics(
            (byte*)_dibBits, srcW, srcH, sampleX, sampleY, sampleW, sampleH, out var optics) != 0)
            return;

        Volatile.Write(ref _averageBackgroundColorRgb,
            (optics.Red << 16) | (optics.Green << 8) | optics.Blue);
        Volatile.Write(ref _backgroundLightX1000, (int)Math.Round(optics.LightX * 1000.0));
        Volatile.Write(ref _backgroundLightY1000, (int)Math.Round(optics.LightY * 1000.0));
        Volatile.Write(ref _backgroundContrast1000, (int)Math.Round(optics.Contrast * 1000.0));
        AverageBackgroundBrightnessInt = (int)Math.Round(
            0.299 * optics.Red + 0.587 * optics.Green + 0.114 * optics.Blue);
        Volatile.Write(ref _outsideBackdropColorRgb, optics.OutsideRgb);
    }

    internal static unsafe BackdropOptics AnalyzeBackdropSamples(
        ReadOnlySpan<int> rgbSamples, int columns, int rows)
    {
        int count = columns * rows;
        if (columns <= 0 || rows <= 0 || rgbSamples.Length < count)
            return new BackdropOptics(128, 128, 128, 0, 0, 0);

        fixed (int* samplePtr = rgbSamples)
        {
            if (NativeAnalyzeBackdropSamples(samplePtr, rgbSamples.Length, columns, rows, out var optics) != 0)
                return new BackdropOptics(128, 128, 128, 0, 0, 0);
            return new BackdropOptics(
                optics.Red, optics.Green, optics.Blue, optics.LightX, optics.LightY, optics.Contrast);
        }
    }

    private bool EnsureGdiResources(int srcW, int srcH, IntPtr screenDc)
    {
        if (_memDc != IntPtr.Zero && _dibBits != IntPtr.Zero && _dibW == srcW && _dibH == srcH)
            return true;

        ReleaseGdiResources();

        if (!CreateDibSurface(screenDc, srcW, srcH, out var surface)) return false;

        _memDc = (IntPtr)surface.MemoryDc;
        _dibBmp = (IntPtr)surface.Bitmap;
        _oldBmp = (IntPtr)surface.PreviousBitmap;
        _dibBits = (IntPtr)surface.Bits;
        _dibW = srcW; _dibH = srcH;
        return true;
    }

    private void ReleaseGdiResources()
    {
        if (_memDc != IntPtr.Zero)
        {
            var surface = new VnDibSurface
            {
                MemoryDc = _memDc,
                Bitmap = _dibBmp,
                PreviousBitmap = _oldBmp,
                Bits = _dibBits,
                Width = _dibW,
                Height = _dibH,
            };
            DestroyDibSurface(ref surface);
        }
        _memDc = _dibBmp = _dibBits = _oldBmp = IntPtr.Zero;
        _dibW = _dibH = 0;
    }

    private const int ParallelPixelThreshold = 40_000;

    private static readonly ParallelOptions ParallelOpts = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8))
    };

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
        byte adjust = (Math.Abs(sat) > 1e-3 || Math.Abs(bright) > 1e-3) ? (byte)1 : (byte)0;
        float satFactor = (float)(1.0 + sat);
        float brightAdd = (float)(bright * 255.0);

        fixed (byte* dst = _outBuffer)
        fixed (int* mapPtr = _refractMap)
        {
            if (count >= ParallelPixelThreshold)
            {
                int chunks = Math.Max(1, ParallelOpts.MaxDegreeOfParallelism);
                int chunkPixels = (count + chunks - 1) / chunks;
                IntPtr srcP = (IntPtr)src, dstP = (IntPtr)dst, mapP = (IntPtr)mapPtr;
                int st = stride;

                Parallel.For(0, chunks, ParallelOpts, chunk =>
                {
                    int start = chunk * chunkPixels;
                    int end = Math.Min(count, start + chunkPixels);
                    if (start < end)
                    {
                        NativeRefractGather(
                            (byte*)srcP, (byte*)dstP, (int*)mapP, st, start, end,
                            adjust, satFactor, brightAdd);
                    }
                });
            }
            else
            {
                NativeRefractGather(
                    src, dst, mapPtr, stride, 0, count,
                    adjust, satFactor, brightAdd);
            }
        }
    }

    internal static unsafe int[] GaussianBoxRadii(double sigma, int passes = 3)
    {
        int count = Math.Max(1, passes);
        var radii = new int[count];
        fixed (int* radiiPtr = radii)
        {
            NativeGaussianBoxRadii(sigma, count, radiiPtr, count, out _);
        }
        return radii;
    }

    private unsafe byte[] BlurCapturedSource(int w, int h, int[] passRadii)
    {
        int pixels = checked(w * h);
        int bytes = checked(pixels * 4);
        if (_sourceBlurBuffer.Length < bytes)
            _sourceBlurBuffer = new byte[checked(GrowPixelBufferCapacity(_sourceBlurBuffer.Length / 4, pixels) * 4)];
        if (_sourceBlurTmp.Length < bytes)
            _sourceBlurTmp = new byte[_sourceBlurBuffer.Length];

        byte[] a = _sourceBlurBuffer, b = _sourceBlurTmp;
        int maximumRadius = Math.Max(1, Math.Min(w, h) / 2);
        const int rowStripe = 16;
        const int colStripe = 64;
        int rowStripes = (h + rowStripe - 1) / rowStripe;
        int colStripes = (w + colStripe - 1) / colStripe;
        bool blurred = false;

        for (int pass = 0; pass < passRadii.Length; pass++)
        {
            int r = Math.Clamp(passRadii[pass], 0, maximumRadius);
            if (r < 1) continue;

            if (!blurred)
            {
                IntPtr srcBits = _dibBits;
                if (h >= 64)
                    Parallel.For(0, rowStripes, ParallelOpts, stripe => GlassBoxBlur.Horizontal(
                        srcBits, b, w, r, stripe * rowStripe, Math.Min(h, (stripe + 1) * rowStripe)));
                else
                    GlassBoxBlur.Horizontal(srcBits, b, w, r, 0, h);
                blurred = true;
            }
            else
            {
                if (h >= 64)
                    Parallel.For(0, rowStripes, ParallelOpts, stripe => GlassBoxBlur.Horizontal(
                        a, b, w, r, stripe * rowStripe, Math.Min(h, (stripe + 1) * rowStripe)));
                else
                    GlassBoxBlur.Horizontal(a, b, w, r, 0, h);
            }

            if (w >= colStripe)
                Parallel.For(0, colStripes, ParallelOpts, stripe => GlassBoxBlur.Vertical(
                    b, a, w, h, r, stripe * colStripe, Math.Min(w, (stripe + 1) * colStripe)));
            else
                GlassBoxBlur.Vertical(b, a, w, h, r, 0, w);
        }

        if (!blurred)
        {
            fixed (byte* dst = a)
            {
                Buffer.MemoryCopy((void*)_dibBits, dst, a.Length, bytes);
            }
        }

        return a;
    }

    internal readonly record struct MapDimensions(
        int OutW, int OutH,
        int SrcW, int SrcH,
        int Margin,
        int NotchW, int NotchH,
        int NotchOffX, int NotchOffY,
        int CaptureShiftX, int CaptureShiftY);

    private const int RefractionLutSize = 512;
    private readonly double[] _refractLut = new double[RefractionLutSize];

    private unsafe bool EnsureMaps(GlassParams p, MapDimensions d)
    {
        p.TopCornerRadius = Math.Round(p.TopCornerRadius * 2.0) / 2.0;
        p.BottomCornerRadius = Math.Round(p.BottomCornerRadius * 2.0) / 2.0;

        if (IsMapCacheValid(p, d))
            return false;

        UpdateMapCacheState(p, d);
        EnsureMapBuffers(d.OutW, d.OutH);

        var nativeParams = new VnGlassMapParams
        {
            OutW = d.OutW,
            OutH = d.OutH,
            SrcW = d.SrcW,
            SrcH = d.SrcH,
            Margin = d.Margin,
            NotchW = d.NotchW,
            NotchH = d.NotchH,
            NotchOffX = d.NotchOffX,
            NotchOffY = d.NotchOffY,
            CaptureShiftX = d.CaptureShiftX,
            CaptureShiftY = d.CaptureShiftY,
            OutScale = _outScale,
            TopCornerRadius = p.TopCornerRadius,
            BottomCornerRadius = p.BottomCornerRadius,
            EdgeBend = p.EdgeBend,
            ChromaticAberration = p.ChromaticAberration,
            Distortion = p.Distortion,
            FPower = p.FPower,
            RefractionA = p.RefractionA,
            RefractionB = p.RefractionB,
            RefractionC = p.RefractionC,
            RefractionD = p.RefractionD,
            Refraction = p.Refraction,
            ZRadius = p.ZRadius,
            BevelMode = p.BevelMode
        };

        fixed (double* lutPtr = _refractLut)
        fixed (int* mapPtr = _refractMap)
        {
            NativeBuildRefractionLut(
                p.FPower, p.RefractionA, p.RefractionB, p.RefractionC, p.RefractionD,
                lutPtr, RefractionLutSize);

            if (d.OutH >= 64)
            {
                const int stripeRows = 16;
                int stripes = (d.OutH + stripeRows - 1) / stripeRows;
                IntPtr lutAddr = (IntPtr)lutPtr;
                IntPtr mapAddr = (IntPtr)mapPtr;
                int outH = d.OutH;
                Parallel.For(0, stripes, ParallelOpts, stripe =>
                {
                    int y0 = stripe * stripeRows;
                    int y1 = Math.Min(outH, y0 + stripeRows);
                    NativeBuildRefractionRows(
                        in nativeParams, (double*)lutAddr, RefractionLutSize, y0, y1, (int*)mapAddr);
                });
            }
            else
            {
                NativeBuildRefractionRows(
                    in nativeParams, lutPtr, RefractionLutSize, 0, d.OutH, mapPtr);
            }
        }

        return true;
    }

    private bool IsMapCacheValid(GlassParams p, MapDimensions d)
    {
        return !_mapsDirty
            && _outW == d.OutW && _outH == d.OutH
            && _srcW == d.SrcW && _srcH == d.SrcH
            && _margin == d.Margin
            && _refractMap.Length >= d.OutW * d.OutH * 6
            && _mapNotchW == d.NotchW && _mapNotchH == d.NotchH
            && _mapNotchOffX == d.NotchOffX && _mapNotchOffY == d.NotchOffY
            && _mapCaptureShiftX == d.CaptureShiftX && _mapCaptureShiftY == d.CaptureShiftY
            && Math.Abs(_mapTopCornerRadius - p.TopCornerRadius) < 1e-3
            && Math.Abs(_mapBottomCornerRadius - p.BottomCornerRadius) < 1e-3;
    }

    private void UpdateMapCacheState(GlassParams p, MapDimensions d)
    {
        _mapsDirty = false;
        _outW = d.OutW; _outH = d.OutH; _srcW = d.SrcW; _srcH = d.SrcH; _margin = d.Margin;
        _mapNotchW = d.NotchW; _mapNotchH = d.NotchH; _mapNotchOffX = d.NotchOffX; _mapNotchOffY = d.NotchOffY;
        _mapCaptureShiftX = d.CaptureShiftX; _mapCaptureShiftY = d.CaptureShiftY;
        _mapTopCornerRadius = p.TopCornerRadius;
        _mapBottomCornerRadius = p.BottomCornerRadius;
    }

    private void EnsureMapBuffers(int outW, int outH)
    {
        int n = checked(outW * outH);
        int needed = checked(n * 4);
        if (_outBuffer.Length < needed)
        {
            _outBuffer = new byte[checked(GrowPixelBufferCapacity(_outBuffer.Length / 4, n) * 4)];
        }
        int mapInts = checked(n * 6);
        if (_refractMap.Length < mapInts)
        {
            int capacity = checked(GrowPixelBufferCapacity(_refractMap.Length / 6, n) * 6);
            _refractMap = new int[capacity];
        }
    }

    internal static int GrowPixelBufferCapacity(int current, int required)
    {
        // Retain capacity across collapse/expand cycles to prevent LOH thrashing.
        if (current >= required) return current;
        // Amortize LOH allocations through expansion. Bound unused capacity to
        // 64K pixels per buffer instead of retaining another full-sized frame.
        int slack = Math.Clamp(current / 4, 4096, 65536);
        return Math.Max(required, checked(current + slack));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Smoother01(double x)
    {
        double t = Math.Clamp(x, 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double ExponentialRefract(double x, double a, double b, double c, double d)
    {
        double exponent = -d * x - a;
        double baseVal = Math.Max(c * 2.718281828459045, 0.0001);
        return 1.0 - b * Math.Pow(baseVal, exponent);
    }
}
