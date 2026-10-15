using System;
using System.Windows;
using System.Windows.Media;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private LiquidGlassController? _liquidGlass;

    private const string LiquidGlassStyleId = "liquidglass";

    private LiquidGlassRefractionEffect? _glassRefractionEffect;
    private LiquidGlassInteractionController? _glassInteractionController;
    private bool _gpuRefractionConfigured;
    private bool _gpuRefractionFailed;
    private UiScheduledTimer? _glassGpuRetryTimer;
    private int _glassGpuRetrySeconds = 5;
    private bool _glassRendererRebuildQueued;
    private bool _glassInitialFramePending;
    private double _glassConfiguredOpacity = 1.0;

    private bool UseGpuRefraction =>
        !_gpuRefractionFailed && (_settings.LiquidGlass?.UseGpuRefraction ?? true) &&
        LiquidGlassRefractionEffect.IsAvailable;

    private bool IsLiquidGlassEnabled =>
        string.Equals(_settings.NotchStyle, LiquidGlassStyleId, StringComparison.OrdinalIgnoreCase);

    private void ApplyLiquidGlassSkin()
    {
        if (_cleanedUp) return;
        if (GlassBackdropHost == null) return;

        if (IsLiquidGlassEnabled)
        {
            // Keep material transparent until first complete frame presents, avoiding
            // transient black notch feedback while DWM applies exclusion.
            bool needsInitialFrame = _liquidGlass?.HasPresentedFrame != true;
            _glassInitialFramePending = needsInitialFrame;
            NotchBackground.Opacity = 0;
            ExpandedContent.Background = System.Windows.Media.Brushes.Transparent;

            // Opaque dark base behind the live glass image. The refraction image is
            GlassBackdropHost.Background = _glassBaseFill;

            bool sysTrans = IsSystemTransparencyEnabled();
            if (!sysTrans)
            {
                GlassMaterialClipHost.Visibility = Visibility.Collapsed;
                _glassInitialFramePending = false;
                StopLiquidGlass();

                ApplyGlassContentShadow(false);
                ApplyGlassToTimerBar(false);
                ApplyGlassToTimerFinishedView(false);
                ApplyGlassPanelMaterial(false);

                GlassBackdropHost.Visibility = Visibility.Collapsed;
                GlassBackdropHost.Background = null;
                GlassTintOverlay.Visibility = Visibility.Collapsed;
                if (GlassGrainOverlay != null)
                {
                    GlassGrainOverlay.Visibility = Visibility.Collapsed;
                    GlassGrainOverlay.Opacity = 0;
                }
                SetOpticalRimVisibility(Visibility.Collapsed);
                if (GlassDarkOverlay != null)
                {
                    GlassDarkOverlay.Visibility = Visibility.Collapsed;
                    GlassDarkOverlay.Opacity = 0;
                }

                NotchBackground.Opacity = 1;
                ExpandedContent.Background = (System.Windows.Media.Brush)FindResource("NotchGradient");
                RestoreNotchShadowDefaults();
                UpdateEarVisibility();
                return;
            }

            if (needsInitialFrame)
                GlassBackdropHost.Opacity = 0;
            GlassMaterialClipHost.Visibility = Visibility.Visible;
            GlassBackdropHost.Visibility = Visibility.Visible;
            GlassTintOverlay.Visibility = Visibility.Visible;
            SetOpticalRimVisibility(Visibility.Visible);
            if (GlassDarkOverlay != null)
            {
                GlassDarkOverlay.Visibility = Visibility.Collapsed;
                GlassDarkOverlay.Opacity = 0;
            }
            if (GlassGrainOverlay != null) GlassGrainOverlay.Background = GlassGrainBrush.Instance;

            CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
            CompositionTarget.Rendering += OnLiquidGlassFrameUpdate;

            int targetFps = _settings.LiquidGlass?.TargetFps ?? 0;
            if (targetFps <= 0) targetFps = VNotch.Services.AnimationConfig.TargetFps;

            _liquidGlass ??= new LiquidGlassController(
                GlassBackdropImage,
                () => _hwnd,
                GetGlassCaptureRegion,
                // This is a hard render cadence: unchanged desktop frames are still
                activeFps: Math.Clamp(targetFps, 30, LiquidGlassController.MaxTargetFps),
                logTag: "ISLAND",
                maxRegionWidth: (int)Math.Ceiling(1600 * GetGlassDpiScale()),
                maxRegionHeight: (int)Math.Ceiling(600 * GetGlassDpiScale()));

            // Magnifier capture excludes the notch internally while the user-facing
            _liquidGlass.HideFromScreenCapture = false;

            // Match the controller to the notch's current motion state so it starts
            UpdateGlassMotionState();

            bool hadFailedBefore = _gpuRefractionFailed;
            ConfigureGpuRefraction();

            // If ConfigureGpuRefraction just failed synchronously, OnGpuRefractionFailure
            // already called ApplyLiquidGlassConfig() and ScheduleGlassGpuRetry().
            if (!_gpuRefractionFailed || hadFailedBefore)
            {
                ApplyLiquidGlassConfig();
                if (_gpuRefractionFailed) ScheduleGlassGpuRetry();
            }
            _liquidGlass.Start();
            ApplyGlassContentShadow(true);
            ApplyGlassToTimerBar(true);
            ApplyGlassToTimerFinishedView(true);
            ApplyGlassPanelMaterial(true);
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
            HideLyricsBlurBackground();
            UpdateEarVisibility();
        }
        else
        {
            GlassMaterialClipHost.Visibility = Visibility.Collapsed;
            _glassInitialFramePending = false;
            StopLiquidGlass();

            ApplyGlassContentShadow(false);
            ApplyGlassToTimerBar(false);
            ApplyGlassToTimerFinishedView(false);
            ApplyGlassPanelMaterial(false);

            GlassBackdropHost.Visibility = Visibility.Collapsed;
            GlassBackdropHost.Background = null;
            GlassTintOverlay.Visibility = Visibility.Collapsed;
            if (GlassGrainOverlay != null)
            {
                GlassGrainOverlay.Visibility = Visibility.Collapsed;
                GlassGrainOverlay.Opacity = 0;
            }
            SetOpticalRimVisibility(Visibility.Collapsed);
            if (GlassDarkOverlay != null)
            {
                GlassDarkOverlay.Visibility = Visibility.Collapsed;
                GlassDarkOverlay.Opacity = 0;
            }

            NotchBackground.Opacity = 1;
            ExpandedContent.Background = (System.Windows.Media.Brush)FindResource("NotchGradient");
            RestoreNotchShadowDefaults();
            UpdateEarVisibility();
        }
    }

    private void ApplyLiquidGlassConfig()
    {
        if (GlassBackdropHost == null) return;
        _lastGpuGeometry = null;
        _lastAppliedGpuOptics = null;
        _lastDynamicTopCornerR = -1;
        _lastDynamicBottomCornerR = -1;

        var cfg = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();
        _glassMaterialPresenter.PowerFactor = cfg.PowerFactor;
        UpdateNotchClip();

        double dipRadius = Math.Clamp(cfg.BlurAmount, 0, 1) * 28.0;
        double dpiScale = GetGlassDpiScale();
        int gaussianSigma = (int)Math.Round(dipRadius * dpiScale);
        if (_liquidGlass != null)
        {
            _liquidGlass.SetBlur(gaussianSigma);

            int targetFps = cfg.TargetFps;
            if (targetFps <= 0) targetFps = VNotch.Services.AnimationConfig.TargetFps;

            _liquidGlass.UpdateFps(Math.Clamp(targetFps, 30, LiquidGlassController.MaxTargetFps));
            if (UseGpuRefraction)
            {
                GlassBackdropImage.HorizontalAlignment = HorizontalAlignment.Left;
                GlassBackdropImage.VerticalAlignment = VerticalAlignment.Top;
                GlassBackdropImage.Width = _liquidGlass.SurfaceWidth / dpiScale;
                GlassBackdropImage.Height = _liquidGlass.SurfaceHeight / dpiScale;
            }
        }

        // GPU mode blurs on the host element instead of the CPU box blur.
        ApplyGpuBlur(cfg.BlurAmount);

        _glassConfiguredOpacity = Math.Clamp(cfg.Opacity, 0, 1);
        if (!_glassInitialFramePending)
            GlassBackdropHost.Opacity = _glassConfiguredOpacity;

        if (GlassGrainOverlay != null)
        {
            double grainOpacity = Math.Clamp(cfg.Noise * 1.5, 0.0, 1.0);
            GlassGrainOverlay.Opacity = grainOpacity;
            GlassGrainOverlay.Visibility = grainOpacity > 0.005 ? Visibility.Visible : Visibility.Collapsed;
            GlassGrainOverlay.Background = GlassGrainBrush.Instance;
        }

        ApplyOpticalRimLevels(cfg.EdgeHighlight, cfg.Specular, cfg.Fresnel, cfg.ChromaticAberration);

        if (_glassRefractionEffect != null)
        {
            _glassRefractionEffect.HighlightStrength = cfg.TouchLight;
        }

        _glassMaterialPresenter.SyncCornerRadius(NotchBorder.CornerRadius);

        if (NotchShadowWrapper?.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
        {
            if (!_notchShadowDefaultsCaptured)
            {
                _notchShadowDefaultOpacity = dse.Opacity;
                _notchShadowDefaultBlur = dse.BlurRadius;
                _notchShadowDefaultsCaptured = true;
            }
            dse.Opacity = Math.Clamp(cfg.ShadowOpacity, 0, 1);
            dse.BlurRadius = Math.Clamp(cfg.ShadowSpread, 0, 60);
        }

        _lastDynamicTopCornerR = NotchBorder.CornerRadius.TopLeft;
        _lastDynamicBottomCornerR = NotchBorder.CornerRadius.BottomLeft;
        _liquidGlass?.SetParams(new LiquidGlassController.GlassParams
        {
            PowerFactor = cfg.PowerFactor,
            RefractionA = cfg.RefractionA,
            RefractionB = cfg.RefractionB,
            RefractionC = cfg.RefractionC,
            RefractionD = cfg.RefractionD,
            FPower = cfg.FPower,
            Noise = cfg.Noise,
            GlowWeight = cfg.GlowWeight,
            GlowBias = cfg.GlowBias,
            GlowEdge0 = cfg.GlowEdge0,
            GlowEdge1 = cfg.GlowEdge1,
            Refraction = cfg.Refraction,
            EdgeBend = cfg.EdgeBend,
            ChromaticAberration = cfg.ChromaticAberration,
            Distortion = VNotch.Services.AnimationConfig.ReduceMotion ? 0.0 : cfg.Distortion,
            ZRadius = cfg.ZRadius,
            Saturation = cfg.Saturation,
            Brightness = cfg.Brightness,
            BevelMode = cfg.BevelMode,
            TopCornerRadius = _lastDynamicTopCornerR,
            BottomCornerRadius = _lastDynamicBottomCornerR
        });
    }

    private System.Windows.Media.Effects.BlurEffect? _glassHostBlur;

    private void ConfigureGpuRefraction()
    {
        if (_liquidGlass == null) return;

        if (!UseGpuRefraction)
        {
            if (_gpuRefractionConfigured || GlassBackdropImage.Effect != null)
            {
                DetachGpuRefraction();
                _liquidGlass.SetGpuMode(false, null);
            }
            return;
        }

        // Settings live preview calls ApplyLiquidGlassSkin repeatedly. Reattaching
        if (_gpuRefractionConfigured &&
            ReferenceEquals(GlassBackdropImage.Effect, _glassRefractionEffect))
            return;

        try
        {
            _glassRefractionEffect ??= new LiquidGlassRefractionEffect();
            GlassBackdropImage.Effect = _glassRefractionEffect;

            _glassInteractionController ??= new LiquidGlassInteractionController(NotchContainer, NotchBorder, _glassRefractionEffect);

            if (!_liquidGlass.SetGpuMode(true, ApplyGpuGeometry, OnGpuRefractionFailure))
            {
                OnGpuRefractionFailure(new InvalidOperationException("GPU presenter unavailable."));
                return;
            }

            VNotch.Services.RuntimeLog.Log("LIQUIDGLASS",
                $"GPU refraction enabled; target={Math.Clamp(_settings.LiquidGlass?.TargetFps ?? 60, 30, LiquidGlassController.MaxTargetFps)} FPS");
            _gpuRefractionConfigured = true;
            _glassGpuRetryTimer?.Stop();
        }
        catch (Exception ex)
        {
            OnGpuRefractionFailure(ex);
        }
    }

    private void OnGpuRefractionFailure(Exception ex)
    {
        if (_cleanedUp) return;
        _gpuRefractionFailed = true;
        VNotch.Services.RuntimeLog.Log("LIQUIDGLASS", $"GPU render failed; switched to CPU fallback: {ex.Message}");
        DetachGpuRefraction();
        _liquidGlass?.SetGpuMode(false, null);
        ApplyLiquidGlassConfig();
        ScheduleGlassGpuRetry();
    }

    private void ScheduleGlassGpuRetry()
    {
        if (_cleanedUp || !IsLiquidGlassEnabled ||
            !(_settings.LiquidGlass?.UseGpuRefraction ?? true) || !LiquidGlassRefractionEffect.IsAvailable) return;
        if (_glassGpuRetryTimer == null)
        {
            _glassGpuRetryTimer = new UiScheduledTimer(
                System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
            _glassGpuRetryTimer.Tick += OnGlassGpuRetry;
        }
        if (_glassGpuRetryTimer.IsEnabled) return;
        _glassGpuRetryTimer.Interval = TimeSpan.FromSeconds(_glassGpuRetrySeconds);
        _glassGpuRetrySeconds = Math.Min(60, _glassGpuRetrySeconds * 2);
        _glassGpuRetryTimer.Start();
    }

    private void OnGlassGpuRetry(object? sender, EventArgs e)
    {
        _glassGpuRetryTimer?.Stop();
        if (_cleanedUp || _liquidGlass?.IsActive != true || !IsLiquidGlassEnabled ||
            !(_settings.LiquidGlass?.UseGpuRefraction ?? true) || !IsSystemTransparencyEnabled()) return;
        _gpuRefractionFailed = false;
        ConfigureGpuRefraction();
        if (!_gpuRefractionFailed)
        {
            ApplyLiquidGlassConfig();
            _liquidGlass.ForceRefresh();
        }
    }

    private void StopLiquidGlass()
    {
        _glassGpuRetryTimer?.Stop();
        CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
        SetGlassRegionPush(false);
        _liquidGlass?.Stop();
        DetachGpuRefraction();
        if (_cleanedUp)
        {
            if (_glassGpuRetryTimer != null)
            {
                _glassGpuRetryTimer.Tick -= OnGlassGpuRetry;
                _glassGpuRetryTimer = null;
            }
            _liquidGlass?.Dispose();
            _liquidGlass = null;
        }
    }

    private void DetachGpuRefraction()
    {
        _gpuRefractionConfigured = false;
        _lastGpuGeometry = null;
        _lastAppliedGpuOptics = null;

        _glassInteractionController?.Dispose();
        _glassInteractionController = null;

        if (GlassBackdropImage != null)
        {
            GlassBackdropImage.Effect = null;
            // Restore CPU-present layout defaults (GPU mode set explicit size).
            GlassBackdropImage.Width = double.NaN;
            GlassBackdropImage.Height = double.NaN;
        }
        if (GlassBackdropHost != null)
            GlassBackdropHost.Effect = null;
        _glassHostBlur = null;
    }

    private LiquidGlassController.GpuGeometry? _lastGpuGeometry;

    private void ApplyGpuGeometry(LiquidGlassController.GpuGeometry g)
    {
        _glassGpuRetrySeconds = 5;
        _lastGpuGeometry = g;
        UpdateShaderGeometryPerFrame();
    }

    private bool _hasCachedFrameMetrics;
    private double _cachedFrameNotchW;
    private double _cachedFrameNotchH;
    private double _cachedFrameDpiScale = 1.0;
    private double _cachedFrameScreenLeft;
    private double _cachedFrameScreenTop;

    private (double NotchW, double NotchH, double DpiScale, double ScreenLeft, double ScreenTop) GetOrComputeFrameMetrics()
    {
        if (_hasCachedFrameMetrics)
        {
            return (_cachedFrameNotchW, _cachedFrameNotchH, _cachedFrameDpiScale, _cachedFrameScreenLeft, _cachedFrameScreenTop);
        }

        double dpiScale = GetGlassDpiScale();
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1.0;
        var (notchW, notchH) = GetCurrentInstantaneousNotchSize();
        double exactW = notchW * dpiScale;
        var (screenLeft, screenTop) = GetNotchScreenPosition(exactW, dpiScale);

        _cachedFrameNotchW = notchW;
        _cachedFrameNotchH = notchH;
        _cachedFrameDpiScale = dpiScale;
        _cachedFrameScreenLeft = screenLeft;
        _cachedFrameScreenTop = screenTop;

        return (notchW, notchH, dpiScale, screenLeft, screenTop);
    }

    private (double Width, double Height) GetCurrentInstantaneousNotchSize()
    {
        double w = double.NaN;
        double h = double.NaN;

        if (NotchBorder != null)
        {
            double scaleX = NotchScale?.ScaleX ?? 1.0;
            double scaleY = NotchScale?.ScaleY ?? 1.0;
            if (!double.IsFinite(scaleX) || scaleX <= 0) scaleX = 1.0;
            if (!double.IsFinite(scaleY) || scaleY <= 0) scaleY = 1.0;

            object valW = NotchBorder.GetValue(FrameworkElement.WidthProperty);
            if (valW is double dW && !double.IsNaN(dW) && dW > 0)
                w = dW * scaleX;
            else if (NotchBorder.ActualWidth > 0)
                w = NotchBorder.ActualWidth * scaleX;

            object valH = NotchBorder.GetValue(FrameworkElement.HeightProperty);
            if (valH is double dH && !double.IsNaN(dH) && dH > 0)
                h = dH * scaleY;
            else if (NotchBorder.ActualHeight > 0)
                h = NotchBorder.ActualHeight * scaleY;
        }

        if (double.IsNaN(w) || w <= 0) w = _collapsedWidth;
        if (double.IsNaN(h) || h <= 0) h = _collapsedHeight;

        return (w, h);
    }

    private LiquidGlassController.GpuGeometry? _lastAppliedGpuOptics;
    private double _lastAppliedTouchLight = -1;

    private (double ScreenLeft, double ScreenTop) GetNotchScreenPosition(double exactW, double dpiScale)
    {
        try
        {
            if (NotchBorder != null && NotchBorder.IsVisible && PresentationSource.FromVisual(NotchBorder) != null)
            {
                if ((_glassScreenGeometry ??= new(this)).TryGetPosition(NotchBorder, out var pt))
                    return (pt.X, pt.Y);
            }
        }
        catch
        {
            // Visual tree might be detached during transition; fallback to geometric calculation
        }

        double fallbackLeft = _fixedX + (_windowWidth - exactW) / 2.0;
        double fallbackTop = _fixedY + (NotchContainerTranslate?.Y ?? 0) * dpiScale;
        return (fallbackLeft, fallbackTop);
    }

    private void UpdateShaderGeometryPerFrame()
    {
        if (!UseGpuRefraction) return;
        var fx = _glassRefractionEffect;
        var lg = _liquidGlass;
        if (fx == null || lg == null || GlassBackdropHost == null) return;

        var (notchW, notchH, dpiScale, screenLeft, screenTop) = GetOrComputeFrameMetrics();
        double exactW = notchW * dpiScale;
        double exactH = notchH * dpiScale;

        double sourceW = lg.SurfaceWidth;
        double sourceH = lg.SurfaceHeight;
        // The shader works in physical screen pixels. Compensate the ancestor's
        // hover scale so WPF does not magnify the captured desktop a second time.
        double scaleX = NotchScale?.ScaleX ?? 1.0;
        double scaleY = NotchScale?.ScaleY ?? 1.0;
        if (!double.IsFinite(scaleX) || scaleX <= 0) scaleX = 1.0;
        if (!double.IsFinite(scaleY) || scaleY <= 0) scaleY = 1.0;
        double imageWidth = sourceW / (dpiScale * scaleX);
        double imageHeight = sourceH / (dpiScale * scaleY);
        if (!double.IsFinite(GlassBackdropImage.Width) || Math.Abs(GlassBackdropImage.Width - imageWidth) > 0.01)
            GlassBackdropImage.Width = imageWidth;
        if (!double.IsFinite(GlassBackdropImage.Height) || Math.Abs(GlassBackdropImage.Height - imageHeight) > 0.01)
            GlassBackdropImage.Height = imageHeight;

        int captureOriginX = lg.LastPresentedCaptureOriginX;
        int captureOriginY = lg.LastPresentedCaptureOriginY;
        double offX, offY;
        if (captureOriginX != int.MinValue && captureOriginY != int.MinValue)
        {
            offX = screenLeft - captureOriginX;
            offY = screenTop - captureOriginY;
        }
        else if (_lastGpuGeometry is { } lastGeom)
        {
            offX = screenLeft - lastGeom.CaptureOriginX;
            offY = screenTop - lastGeom.CaptureOriginY;
        }
        else
        {
            offX = _lastGpuGeometry?.OffX ?? 0;
            offY = _lastGpuGeometry?.OffY ?? 0;
        }

        if (Math.Abs(fx.SrcW - sourceW) > 0.01) fx.SrcW = sourceW;
        if (Math.Abs(fx.SrcH - sourceH) > 0.01) fx.SrcH = sourceH;
        if (Math.Abs(fx.NotchW - exactW) > 0.01) fx.NotchW = exactW;
        if (Math.Abs(fx.NotchH - exactH) > 0.01) fx.NotchH = exactH;
        if (Math.Abs(fx.OffX - offX) > 1e-4) fx.OffX = offX;
        if (Math.Abs(fx.OffY - offY) > 1e-4) fx.OffY = offY;
        double topR = NotchBorder.CornerRadius.TopLeft * dpiScale;
        double bottomR = NotchBorder.CornerRadius.BottomLeft * dpiScale;
        if (Math.Abs(fx.TopCornerR - topR) > 0.01) fx.TopCornerR = topR;
        if (Math.Abs(fx.BottomCornerR - bottomR) > 0.01) fx.BottomCornerR = bottomR;

        var cfg = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();
        if (_lastGpuGeometry is { } g)
        {
            _lastAppliedGpuOptics = g;
            if (Math.Abs(fx.PowerFactor - g.PowerFactor) > 1e-4) fx.PowerFactor = g.PowerFactor;
            if (Math.Abs(fx.A - g.A) > 1e-4) fx.A = g.A;
            if (Math.Abs(fx.B - g.B) > 1e-4) fx.B = g.B;
            if (Math.Abs(fx.C - g.C) > 1e-4) fx.C = g.C;
            if (Math.Abs(fx.D - g.D) > 1e-4) fx.D = g.D;
            if (Math.Abs(fx.FPower - g.FPower) > 1e-4) fx.FPower = g.FPower;
            if (Math.Abs(fx.Noise - g.Noise) > 1e-4) fx.Noise = g.Noise;
            if (Math.Abs(fx.GlowWeight - g.GlowWeight) > 1e-4) fx.GlowWeight = g.GlowWeight;
            if (Math.Abs(fx.GlowBias - g.GlowBias) > 1e-4) fx.GlowBias = g.GlowBias;
            if (Math.Abs(fx.GlowEdge0 - g.GlowEdge0) > 1e-4) fx.GlowEdge0 = g.GlowEdge0;
            if (Math.Abs(fx.GlowEdge1 - g.GlowEdge1) > 1e-4) fx.GlowEdge1 = g.GlowEdge1;
            if (Math.Abs(fx.Chroma - g.Chroma) > 1e-4) fx.Chroma = g.Chroma;
            if (Math.Abs(fx.EdgeBend - g.EdgeBend) > 1e-4) fx.EdgeBend = g.EdgeBend;
            if (Math.Abs(fx.BevelMode - g.BevelMode) > 1e-4) fx.BevelMode = g.BevelMode;
            if (Math.Abs(fx.SatFactor - g.SatFactor) > 1e-4) fx.SatFactor = g.SatFactor;
            if (Math.Abs(fx.BrightAdd - g.BrightAdd) > 1e-4) fx.BrightAdd = g.BrightAdd;
        }
        else
        {
            if (Math.Abs(fx.PowerFactor - cfg.PowerFactor) > 1e-4) fx.PowerFactor = cfg.PowerFactor;
            if (Math.Abs(fx.A - cfg.RefractionA) > 1e-4) fx.A = cfg.RefractionA;
            if (Math.Abs(fx.B - cfg.RefractionB) > 1e-4) fx.B = cfg.RefractionB;
            if (Math.Abs(fx.C - cfg.RefractionC) > 1e-4) fx.C = cfg.RefractionC;
            if (Math.Abs(fx.D - cfg.RefractionD) > 1e-4) fx.D = cfg.RefractionD;
            if (Math.Abs(fx.FPower - cfg.FPower) > 1e-4) fx.FPower = cfg.FPower;
            if (Math.Abs(fx.Noise - cfg.Noise) > 1e-4) fx.Noise = cfg.Noise;
            if (Math.Abs(fx.GlowWeight - cfg.GlowWeight) > 1e-4) fx.GlowWeight = cfg.GlowWeight;
            if (Math.Abs(fx.GlowBias - cfg.GlowBias) > 1e-4) fx.GlowBias = cfg.GlowBias;
            if (Math.Abs(fx.GlowEdge0 - cfg.GlowEdge0) > 1e-4) fx.GlowEdge0 = cfg.GlowEdge0;
            if (Math.Abs(fx.GlowEdge1 - cfg.GlowEdge1) > 1e-4) fx.GlowEdge1 = cfg.GlowEdge1;
            if (Math.Abs(fx.Chroma - cfg.ChromaticAberration) > 1e-4) fx.Chroma = cfg.ChromaticAberration;
            if (Math.Abs(fx.SatFactor - (1.0 + cfg.Saturation)) > 1e-4) fx.SatFactor = 1.0 + cfg.Saturation;
            if (Math.Abs(fx.BrightAdd - cfg.Brightness) > 1e-4) fx.BrightAdd = cfg.Brightness;
            if (Math.Abs(fx.EdgeBend - cfg.EdgeBend) > 1e-4) fx.EdgeBend = cfg.EdgeBend;
            if (Math.Abs(fx.BevelMode - cfg.BevelMode) > 1e-4) fx.BevelMode = cfg.BevelMode;
        }
        if (Math.Abs(_lastAppliedTouchLight - cfg.TouchLight) > 1e-4)
        {
            _lastAppliedTouchLight = cfg.TouchLight;
            fx.HighlightStrength = cfg.TouchLight;
        }
    }

    private void ApplyGpuBlur(double blurAmount)
    {
        if (!UseGpuRefraction || GlassBackdropHost == null) return;

        double radius = Math.Clamp(blurAmount, 0, 1) * 14.0;
        if (radius < 0.5)
        {
            GlassBackdropHost.Effect = null;
            _glassHostBlur = null;
            return;
        }

        if (_glassHostBlur == null)
        {
            _glassHostBlur = new System.Windows.Media.Effects.BlurEffect
            {
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            GlassBackdropHost.Effect = _glassHostBlur;
        }
        _glassHostBlur.Radius = radius;
    }

    // Dark base shown behind the live glass image if a frame is unavailable
    private static readonly SolidColorBrush _glassBaseFill = Frozen(0x18, 0x0B, 0x0E, 0x12);

    private void SetOpticalRimVisibility(Visibility visibility)
    {
        Visibility cpuRimVis = visibility == Visibility.Visible && UseGpuRefraction
            ? Visibility.Collapsed
            : visibility;
        GlassDepthRimBorder.Visibility = cpuRimVis;
        GlassCoolRimBorder.Visibility = cpuRimVis;
        GlassWarmRimBorder.Visibility = cpuRimVis;
        GlassFresnelBloomBorder.Visibility = cpuRimVis;
        GlassFresnelBorder.Visibility = cpuRimVis;
        GlassInnerFresnelBorder.Visibility = cpuRimVis;
        GlassSpecularBorder.Visibility = cpuRimVis;
        GlassRimBorder.Visibility = visibility;
    }

    private void ApplyOpticalRimLevels(double edgeHighlight, double specular, double fresnel, double chroma)
    {
        if (UseGpuRefraction)
        {
            if (GlassDepthRimBorder.Visibility != Visibility.Collapsed)
            {
                GlassDepthRimBorder.Visibility = Visibility.Collapsed;
                GlassCoolRimBorder.Visibility = Visibility.Collapsed;
                GlassWarmRimBorder.Visibility = Visibility.Collapsed;
                GlassFresnelBloomBorder.Visibility = Visibility.Collapsed;
                GlassFresnelBorder.Visibility = Visibility.Collapsed;
                GlassInnerFresnelBorder.Visibility = Visibility.Collapsed;
                GlassSpecularBorder.Visibility = Visibility.Collapsed;
            }
            GlassDepthRimBorder.Opacity = 0;
            GlassCoolRimBorder.Opacity = 0;
            GlassWarmRimBorder.Opacity = 0;
            GlassFresnelBloomBorder.Opacity = 0;
            GlassFresnelBorder.Opacity = 0;
            GlassInnerFresnelBorder.Opacity = 0;
            GlassSpecularBorder.Opacity = 0;
            GlassRimBorder.Opacity = Math.Clamp(edgeHighlight * 0.50, 0.0, 0.65);
            return;
        }

        if (IsLiquidGlassEnabled && GlassDepthRimBorder.Visibility != Visibility.Visible)
        {
            GlassDepthRimBorder.Visibility = Visibility.Visible;
            GlassCoolRimBorder.Visibility = Visibility.Visible;
            GlassWarmRimBorder.Visibility = Visibility.Visible;
            GlassFresnelBloomBorder.Visibility = Visibility.Visible;
            GlassFresnelBorder.Visibility = Visibility.Visible;
            GlassInnerFresnelBorder.Visibility = Visibility.Visible;
            GlassSpecularBorder.Visibility = Visibility.Visible;
        }

        EnsureDynamicFresnelBrush();

        double edge = Math.Clamp(edgeHighlight, 0, 1);
        double spec = Math.Clamp(specular, 0, 1);
        double fres = Math.Clamp(fresnel, 0, 1);
        double spectral = Math.Clamp(chroma, 0, 2);
        _activeFresnelLevel = fres;

        // A square-root response preserves a delicate rim at low slider values
        GlassRimBorder.Opacity = Math.Sqrt(edge) * 0.82;
        GlassDepthRimBorder.Opacity = Math.Clamp(edge * 0.34 + fres * 0.24, 0, 0.46);
        double fresnelEnergy = Math.Sqrt(fres);
        GlassFresnelBloomBorder.Opacity = fresnelEnergy * 0.30;
        GlassFresnelBorder.Opacity = fresnelEnergy * 0.94;
        GlassInnerFresnelBorder.Opacity = fresnelEnergy * 0.64;
        GlassSpecularBorder.Opacity = spec * 0.92;

        double spectralOpacity = Math.Clamp(spectral * 0.30 + edge * 0.10, 0, 0.52);
        GlassCoolRimBorder.Opacity = spectralOpacity;
        GlassWarmRimBorder.Opacity = spectralOpacity * 0.76;
    }

    private RadialGradientBrush? _dynamicFresnelBrush;
    private LinearGradientBrush? _dynamicInnerFresnelBrush;
    private double _activeFresnelLevel;
    private double _dynamicFresnelX = 0.42;
    private double _dynamicFresnelY = 0.34;
    private double _dynamicFresnelContrast;
    private Color _dynamicFresnelTint = Color.FromRgb(126, 154, 180);
    private long _lastDynamicFresnelTicks;

    private void EnsureDynamicFresnelBrush()
    {
        if (_dynamicFresnelBrush != null) return;

        _dynamicFresnelBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.82,
            RadiusY = 0.88,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Pad
        };
        _dynamicFresnelBrush.GradientStops.Add(new GradientStop(Colors.White, 0.0));
        _dynamicFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(232, 210, 230, 244), 0.18));
        _dynamicFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(104, 126, 154, 180), 0.48));
        _dynamicFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(14, 126, 154, 180), 0.78));
        _dynamicFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 126, 154, 180), 1.0));

        _dynamicInnerFresnelBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0.14, 0.08),
            EndPoint = new Point(0.86, 0.92),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Pad
        };
        _dynamicInnerFresnelBrush.GradientStops.Add(new GradientStop(Colors.White, 0.0));
        _dynamicInnerFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(136, 160, 184, 204), 0.2));
        _dynamicInnerFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(16, 126, 154, 180), 0.48));
        _dynamicInnerFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(112, 0, 0, 0), 0.74));
        _dynamicInnerFresnelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(168, 210, 230, 244), 1.0));

        GlassFresnelBloomBorder.BorderBrush = _dynamicFresnelBrush;
        GlassFresnelBorder.BorderBrush = _dynamicFresnelBrush;
        GlassInnerFresnelBorder.BorderBrush = _dynamicInnerFresnelBrush;
    }

    private void UpdateDynamicFresnel(LiquidGlassController.BackdropOptics optics)
    {
        if (UseGpuRefraction || _activeFresnelLevel <= 0.001) return;

        EnsureDynamicFresnelBrush();
        var brush = _dynamicFresnelBrush!;
        var innerBrush = _dynamicInnerFresnelBrush!;

        long now = Environment.TickCount64;
        double elapsedSeconds = _lastDynamicFresnelTicks == 0
            ? 1.0
            : Math.Clamp((now - _lastDynamicFresnelTicks) / 1000.0, 0.0, 0.25);
        _lastDynamicFresnelTicks = now;
        // Fresnel should read as a stable material reflection, not a highlight
        double response = 1.0 - Math.Exp(-elapsedSeconds * 1.1);

        double targetX = Math.Clamp(0.42 + optics.LightX * 0.08, 0.34, 0.50);
        double targetY = Math.Clamp(0.34 + optics.LightY * 0.07, 0.27, 0.41);
        _dynamicFresnelX += (targetX - _dynamicFresnelX) * response;
        _dynamicFresnelY += (targetY - _dynamicFresnelY) * response;
        _dynamicFresnelContrast +=
            (Math.Clamp(optics.Contrast, 0.0, 1.0) - _dynamicFresnelContrast) * response;

        Color targetTint = BuildContentFresnelTint(optics.Red, optics.Green, optics.Blue);
        _dynamicFresnelTint = InterpolateColor(_dynamicFresnelTint, targetTint, response);

        GlassVisualUpdates.SetPoint(brush, RadialGradientBrush.CenterProperty, new Point(_dynamicFresnelX, _dynamicFresnelY));
        GlassVisualUpdates.SetPoint(brush, RadialGradientBrush.GradientOriginProperty, new Point(
            Math.Clamp(0.5 + (_dynamicFresnelX - 0.5) * 0.72, 0.30, 0.62),
            Math.Clamp(0.5 + (_dynamicFresnelY - 0.5) * 0.72, 0.26, 0.58)));
        GlassVisualUpdates.SetDouble(brush, RadialGradientBrush.RadiusXProperty, 0.84 - _dynamicFresnelContrast * 0.08);
        GlassVisualUpdates.SetDouble(brush, RadialGradientBrush.RadiusYProperty, 0.90 - _dynamicFresnelContrast * 0.06);

        // Keep the inner reflection axis fixed. Normalizing the tiny per-frame

        Color bright = InterpolateColor(_dynamicFresnelTint, Colors.White, 0.68);
        Color mid = InterpolateColor(_dynamicFresnelTint, Colors.White, 0.42);
        GlassVisualUpdates.SetColor(brush.GradientStops[0], Color.FromArgb(255, bright.R, bright.G, bright.B));
        GlassVisualUpdates.SetColor(brush.GradientStops[1], Color.FromArgb(200, mid.R, mid.G, mid.B));
        GlassVisualUpdates.SetColor(brush.GradientStops[2], Color.FromArgb(
            112, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B));
        GlassVisualUpdates.SetColor(brush.GradientStops[3], Color.FromArgb(
            18, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B));
        GlassVisualUpdates.SetColor(brush.GradientStops[4], Color.FromArgb(
            0, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B));

        GlassVisualUpdates.SetColor(innerBrush.GradientStops[0], Color.FromArgb(255, bright.R, bright.G, bright.B));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[1], Color.FromArgb(
            150, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[2], Color.FromArgb(
            18, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[3], Color.FromArgb(
            (byte)Math.Round(82 + _dynamicFresnelContrast * 48), 0, 0, 0));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[4], Color.FromArgb(184, mid.R, mid.G, mid.B));

        double fresnelEnergy = Math.Sqrt(Math.Clamp(_activeFresnelLevel, 0.0, 1.0));
        double contrastResponse = 0.94 + _dynamicFresnelContrast * 0.12;
        GlassVisualUpdates.SetDouble(GlassFresnelBloomBorder, UIElement.OpacityProperty, Math.Clamp(
            fresnelEnergy * (0.24 + _dynamicFresnelContrast * 0.08), 0.0, 0.42));
        GlassVisualUpdates.SetDouble(GlassFresnelBorder, UIElement.OpacityProperty, Math.Clamp(
            fresnelEnergy * 0.98 * contrastResponse, 0.0, 1.0));
        GlassVisualUpdates.SetDouble(GlassInnerFresnelBorder, UIElement.OpacityProperty, Math.Clamp(
            fresnelEnergy * (0.58 + _dynamicFresnelContrast * 0.10), 0.0, 0.76));
    }

    private static Color BuildContentFresnelTint(byte red, byte green, byte blue)
    {
        double luminance = 0.299 * red + 0.587 * green + 0.114 * blue;
        double r = luminance + (red - luminance) * 1.30;
        double g = luminance + (green - luminance) * 1.30;
        double b = luminance + (blue - luminance) * 1.30;
        double peak = Math.Max(r, Math.Max(g, b));

        if (peak < 12.0)
            return Color.FromRgb(104, 116, 130);

        if (peak < 96.0)
        {
            double lift = 96.0 / peak;
            r *= lift; g *= lift; b *= lift;
        }

        return Color.FromRgb(
            (byte)Math.Clamp((int)Math.Round(r), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b), 0, 255));
    }

    private static Color InterpolateColor(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    // Liquid-glass "material" matching the audio redirect frame.
    private static readonly SolidColorBrush _glassPanelBg = Frozen(0x33, 0, 0, 0);
    private static readonly SolidColorBrush _glassPanelBorder = Frozen(0x26, 255, 255, 255);
    private static readonly SolidColorBrush _defaultPanelBg = Frozen(0xFF, 0x1A, 0x1A, 0x1A);
    // The idle camera box should read the same as the file tray (both #1A1A1A)
    private static readonly SolidColorBrush _cameraOverlayDefault = Frozen(0x00, 0, 0, 0);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void ApplyGlassPanelMaterial(bool glass)
    {
        ApplyGlassToProgressBar(glass);

        if (CompactThumbnailBorder != null)
        {
            CompactThumbnailBorder.Background = glass ? _glassPanelBg : _defaultPanelBg;
            CompactThumbnailBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
            CompactThumbnailBorder.BorderThickness = new Thickness(0);
        }

        if (CompactThumbnailRim != null)
        {
            CompactThumbnailRim.BorderBrush = glass ? _glassPanelBorder : System.Windows.Media.Brushes.Transparent;
            CompactThumbnailRim.BorderThickness = glass ? new Thickness(0.5) : new Thickness(0);
        }

        if (AnimationThumbnailBorder != null)
        {
            AnimationThumbnailBorder.Background = System.Windows.Media.Brushes.Transparent;
            AnimationThumbnailBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
            AnimationThumbnailBorder.BorderThickness = new Thickness(0);
            AnimationThumbnailBorder.Effect = glass ? GetOrCreateGlassAnimThumbnailShadow() : null;
        }

        if (AnimationThumbnailRim != null)
        {
            AnimationThumbnailRim.BorderBrush = glass ? _glassPanelBorder : System.Windows.Media.Brushes.Transparent;
            AnimationThumbnailRim.BorderThickness = glass ? new Thickness(0.5) : new Thickness(0);
        }

        if (glass)
        {
            if (CameraSection != null)
            {
                CameraSection.Background = _glassPanelBg;
                CameraSection.BorderBrush = _glassPanelBorder;
                CameraSection.BorderThickness = new Thickness(1);
                // The camera icon overlay adds an extra dark wash on top of the glass
                if (CameraOverlay != null)
                    CameraOverlay.Background = System.Windows.Media.Brushes.Transparent;
            }
        }
        else
        {
            if (CameraSection != null)
            {
                CameraSection.Background = _defaultPanelBg;
                CameraSection.BorderBrush = null;
                CameraSection.BorderThickness = new Thickness(0);
                if (CameraOverlay != null)
                    CameraOverlay.Background = _cameraOverlayDefault;
            }
        }
    }

    // Frosted translucent track for the media progress bar while the Liquid Glass
    private static readonly SolidColorBrush _glassProgressTrack = Frozen(0x59, 255, 255, 255);
    private Brush? _progressTrackDefaultBg;
    private bool _progressTrackDefaultCaptured;

    private void ApplyGlassToProgressBar(bool glass)
    {
        if (MediaPlayback.ProgressBarBg == null) return;

        if (!_progressTrackDefaultCaptured)
        {
            _progressTrackDefaultBg = MediaPlayback.ProgressBarBg.Background;
            _progressTrackDefaultCaptured = true;
        }

        MediaPlayback.ProgressBarBg.Background = glass ? _glassProgressTrack : _progressTrackDefaultBg;
    }

    private bool _notchShadowDefaultsCaptured;
    private double _notchShadowDefaultOpacity = 0.6;
    private double _notchShadowDefaultBlur = 20;

    private void RestoreNotchShadowDefaults()
    {
        if (!_notchShadowDefaultsCaptured) return;
        if (NotchShadowWrapper?.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
        {
            dse.Opacity = _notchShadowDefaultOpacity;
            dse.BlurRadius = _notchShadowDefaultBlur;
        }
    }

    private WindowScreenGeometry? _glassScreenGeometry;

    private double GetGlassDpiScale()
    {
        double scale = (_glassScreenGeometry ??= new(this)).DpiScaleX;
        return scale > 0 ? scale : 1.0;
    }

    private void InvalidateGlassDpiScale()
    {
        _glassScreenGeometry?.Invalidate();
    }

    // Hover applies a transient scale to the collapsed notch without flipping the
    private bool _glassHoverMotion;
    private int _glassHoverGen;
    private bool _glassGestureSnapBackMotion;
    private int _glassGestureSnapBackGen;

    private void BeginGlassHoverMotion(System.Windows.Media.Animation.AnimationTimeline completionAnim)
    {
        if (_liquidGlass == null || !IsLiquidGlassEnabled || completionAnim == null) return;

        int gen = ++_glassHoverGen;
        _glassHoverMotion = true;
        UpdateGlassMotionState();

        // Replacing a WPF animation does not always raise Completed on the old
        TimeSpan motionDuration = completionAnim.Duration.HasTimeSpan
            ? completionAnim.Duration.TimeSpan
            : TimeSpan.FromMilliseconds(600);
        var safetyTimer = new UiScheduledTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = motionDuration + TimeSpan.FromMilliseconds(180)
        };

        void FinishHoverMotion()
        {
            safetyTimer.Stop();
            if (gen != _glassHoverGen) return;
            _glassHoverMotion = false;
            UpdateGlassMotionState();
            UpdateDynamicGlassParams();
        }

        completionAnim.Completed += (_, _) => FinishHoverMotion();
        safetyTimer.Tick += (_, _) => FinishHoverMotion();
        safetyTimer.Start();
    }

    private void BeginGlassGestureSnapBack(System.Windows.Media.Animation.AnimationTimeline completionAnim)
    {
        if (_liquidGlass == null || !IsLiquidGlassEnabled || completionAnim == null) return;

        int gen = ++_glassGestureSnapBackGen;
        _glassGestureSnapBackMotion = true;
        UpdateGlassMotionState();

        TimeSpan motionDuration = completionAnim.Duration.HasTimeSpan
            ? completionAnim.Duration.TimeSpan
            : TimeSpan.FromMilliseconds(350);
        var safetyTimer = new UiScheduledTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = motionDuration + TimeSpan.FromMilliseconds(180)
        };

        void FinishSnapBackMotion()
        {
            safetyTimer.Stop();
            if (gen != _glassGestureSnapBackGen) return;
            _glassGestureSnapBackMotion = false;
            UpdateGlassMotionState();
        }

        completionAnim.Completed += (_, _) => FinishSnapBackMotion();
        safetyTimer.Tick += (_, _) => FinishSnapBackMotion();
        safetyTimer.Start();
    }

    private bool _glassRegionPushActive;

    private void UpdateGlassMotionState()
    {
        // Hover applies a ScaleTransform (NotchScale) that renders the notch — and
        bool motion = _isAnimating || _isModeTransitioning || _isGestureActive ||
                      _glassGestureSnapBackMotion || _glassHoverMotion;

        _liquidGlass?.SetAnimating(motion);
        if (_liquidGlass != null) _liquidGlass.CaptureFullSurface = motion;
        // We no longer pause presentation during hover, DXGI handles it smoothly.
        SetGlassRegionPush(motion && _liquidGlass != null && IsLiquidGlassEnabled);
    }

    private void SetGlassRegionPush(bool enabled)
    {
        if (enabled == _glassRegionPushActive) return;
        _glassRegionPushActive = enabled;

        if (!enabled)
        {
            if (_liquidGlass != null && IsLiquidGlassEnabled)
            {
                _liquidGlass.SetLiveRegion(GetGlassCaptureRegion());
                _liquidGlass.ClearLiveRegion();
                UpdateShaderGeometryPerFrame();
                UpdateDynamicGlassParams();
            }
            else
            {
                _liquidGlass?.ClearLiveRegion();
            }
        }
    }

    private System.Windows.Media.Effects.DropShadowEffect? _glassContentShadow;
    private System.Windows.Media.Effects.DropShadowEffect? _glassAnimThumbnailShadow;

    private System.Windows.Media.Effects.DropShadowEffect GetOrCreateGlassContentShadow()
    {
        if (_glassContentShadow == null)
        {
            _glassContentShadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 1.2,
                Direction = 270,
                Opacity = 0.5,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            _glassContentShadow.Freeze();
        }
        return _glassContentShadow;
    }

    private System.Windows.Media.Effects.DropShadowEffect GetOrCreateGlassAnimThumbnailShadow()
    {
        if (_glassAnimThumbnailShadow == null)
        {
            _glassAnimThumbnailShadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 0.8,
                Direction = 270,
                Opacity = 0.35,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
        }
        return _glassAnimThumbnailShadow;
    }

    private void ApplyGlassContentShadow(bool enable)
    {
        if (NotchContent != null)
        {
            // The tray already supplies card contrast. A shadow on the entire
            // scrolling subtree forces a large intermediate render surface.
            NotchContent.Effect = enable && ClipboardTrayView?.IsVisible != true
                ? GetOrCreateGlassContentShadow() : null;
        }

        if (AnimationThumbnailBorder != null)
        {
            AnimationThumbnailBorder.Effect = enable ? GetOrCreateGlassAnimThumbnailShadow() : null;
        }
    }

    private Brush? _timerBarDefaultBg;
    private bool _timerBarDefaultsCaptured;
    private Brush? _glassPanelTint;
    private void ApplyGlassToTimerBar(bool glass)
    {
        if (CountdownWidget.TimerControlBar == null) return;

        if (!_timerBarDefaultsCaptured)
        {
            _timerBarDefaultBg = CountdownWidget.TimerControlBar.Background;
            _timerBarDefaultsCaptured = true;
        }

        if (glass)
        {
            if (_glassPanelTint == null)
            {
                _glassPanelTint = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
                _glassPanelTint.Freeze();
            }
            CountdownWidget.TimerControlBar.Background = _glassPanelTint;
            if (CountdownWidget.TimerControlBarShadow != null) CountdownWidget.TimerControlBarShadow.Opacity = 0;
        }
        else
        {
            CountdownWidget.TimerControlBar.Background = _timerBarDefaultBg;
            if (CountdownWidget.TimerControlBarShadow != null) CountdownWidget.TimerControlBarShadow.Opacity = 0.45;
        }
    }

    // Glass material for the countdown "time's up" view: the full surface plus the
    private bool _countdownGlassDefaultsCaptured;
    private Brush? _countdownSurfaceDefaultBg;
    private Brush? _countdownRestartDefaultBg;
    private Brush? _countdownDismissDefaultBg;
    private Brush? _countdownTextDefaultFg;

    private void ApplyGlassToTimerFinishedView(bool glass)
    {
        if (CountdownCompleteSurface == null) return;

        if (!_countdownGlassDefaultsCaptured)
        {
            _countdownSurfaceDefaultBg = CountdownCompleteSurface.Background;
            _countdownRestartDefaultBg = CountdownRestartBtn?.Background;
            _countdownDismissDefaultBg = CountdownDismissBtn?.Background;
            _countdownTextDefaultFg = CountdownCompleteText?.Foreground;
            _countdownGlassDefaultsCaptured = true;
        }

        if (glass)
        {
            if (_glassPanelTint == null)
            {
                _glassPanelTint = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
                _glassPanelTint.Freeze();
            }
            CountdownCompleteSurface.Background = _glassPanelTint;

            // The default alert orange (#FFFF9B3D) turns into a muddy, dim brown
            if (CountdownCompleteText != null)
                CountdownCompleteText.Foreground = VNotch.Services.UiPalette.PrimaryBrush;

            if (CountdownRestartBtn != null)
            {
                CountdownRestartBtn.Background = _glassPanelBg;
                CountdownRestartBtn.BorderBrush = _glassPanelBorder;
                CountdownRestartBtn.BorderThickness = new Thickness(1);
            }

            if (CountdownDismissBtn != null)
            {
                CountdownDismissBtn.Background = _glassPanelBg;
                CountdownDismissBtn.BorderBrush = _glassPanelBorder;
                CountdownDismissBtn.BorderThickness = new Thickness(1);
            }
        }
        else
        {
            CountdownCompleteSurface.Background = _countdownSurfaceDefaultBg;

            if (CountdownCompleteText != null && _countdownTextDefaultFg != null)
                CountdownCompleteText.Foreground = _countdownTextDefaultFg;

            if (CountdownRestartBtn != null)
            {
                CountdownRestartBtn.Background = _countdownRestartDefaultBg;
                CountdownRestartBtn.BorderBrush = null;
                CountdownRestartBtn.BorderThickness = new Thickness(0);
            }

            if (CountdownDismissBtn != null)
            {
                CountdownDismissBtn.Background = _countdownDismissDefaultBg;
                CountdownDismissBtn.BorderBrush = null;
                CountdownDismissBtn.BorderThickness = new Thickness(0);
            }
        }
    }

    private void HideLyricsBlurBackground()
    {
        if (LyricsBlurBackground == null) return;
        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = 0;
        LyricsBlurBackground.Visibility = Visibility.Collapsed;
    }

    private double _lastAppliedDpiScale = -1;

    private LiquidGlassController.CaptureRegion? GetGlassCaptureRegion()
    {
        if (_hwnd == IntPtr.Zero || !IsEffectivelyNotchVisible) return null;

        var (notchW, notchH, dpiScale, screenLeft, screenTop) = GetOrComputeFrameMetrics();
        if (Math.Abs(dpiScale - _lastAppliedDpiScale) > 0.01)
        {
            _lastAppliedDpiScale = dpiScale;
            if (_liquidGlass != null && IsLiquidGlassEnabled && UseGpuRefraction)
            {
                GlassBackdropImage.HorizontalAlignment = HorizontalAlignment.Left;
                GlassBackdropImage.VerticalAlignment = VerticalAlignment.Top;
                GlassBackdropImage.Width = _liquidGlass.SurfaceWidth / dpiScale;
                GlassBackdropImage.Height = _liquidGlass.SurfaceHeight / dpiScale;
            }
        }

        if (notchW <= 0 || notchH <= 0) return null;

        double exactW = notchW * dpiScale;
        double exactH = notchH * dpiScale;
        int physW = Math.Max(1, (int)Math.Round(exactW));
        int physH = Math.Max(1, (int)Math.Round(exactH));
        QueueGlassRendererRebuildIfTooSmall(physW, physH, dpiScale);

        int physLeft = (int)Math.Round(screenLeft, MidpointRounding.AwayFromZero);
        int physTop = (int)Math.Round(screenTop, MidpointRounding.AwayFromZero);

        double subX = screenLeft - physLeft;
        double subY = screenTop - physTop;

        if (physW <= 1 || physH <= 1) return null;

        return new LiquidGlassController.CaptureRegion(
            physLeft, physTop, physW, physH,
            NotchBorder.CornerRadius.TopLeft,
            NotchBorder.CornerRadius.BottomLeft,
            subX, subY);
    }

    internal static (int Width, int Height) GetGlassCaptureCapacity(int width, int height, double dpiScale) =>
        (Math.Clamp(Math.Max(width, (int)Math.Ceiling(1600 * dpiScale)), 64, 4096),
         Math.Clamp(Math.Max(height, (int)Math.Ceiling(600 * dpiScale)), 64, 4096));

    private void QueueGlassRendererRebuildIfTooSmall(int width, int height, double dpiScale)
    {
        if (_liquidGlass == null || _glassRendererRebuildQueued || _cleanedUp) return;
        var (requiredW, requiredH) = GetGlassCaptureCapacity(width, height, dpiScale);
        if (_liquidGlass.MaxRegionWidth >= requiredW && _liquidGlass.MaxRegionHeight >= requiredH) return;
        _glassRendererRebuildQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
        {
            _glassRendererRebuildQueued = false;
            if (_cleanedUp || !IsLiquidGlassEnabled) return;
            StopLiquidGlass();
            _liquidGlass?.Dispose();
            int targetFps = _settings.LiquidGlass?.TargetFps ?? 0;
            if (targetFps <= 0) targetFps = VNotch.Services.AnimationConfig.TargetFps;
            int activeFps = Math.Clamp(targetFps, 30, LiquidGlassController.MaxTargetFps);
            _liquidGlass = new LiquidGlassController(GlassBackdropImage, () => _hwnd, GetGlassCaptureRegion,
                activeFps: activeFps, logTag: "ISLAND", maxRegionWidth: requiredW, maxRegionHeight: requiredH);
            _lastAppliedDpiScale = -1;
            ApplyLiquidGlassSkin();
        }));
    }

    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (key != null)
                {
                    var val = key.GetValue("EnableTransparency");
                    if (val is int i) return i == 1;
                }
            }
        }
        catch { /* ignored */ }
        return true;
    }

    public void UpdateGlassMediaTint(Color dominantColor)
    {
        // Feature disabled per user request: Do not tint liquid glass based on media thumbnail.
    }

    public void ClearGlassMediaTint()
    {
        // Feature disabled: Do not tint liquid glass based on media thumbnail.
    }

    private double _lastActualHeight = -1;
    private double _lastDynamicTopCornerR = -1;
    private double _lastDynamicBottomCornerR = -1;

    private void OnLiquidGlassFrameUpdate(object? sender, EventArgs e)
    {
        if (_liquidGlass == null || !IsLiquidGlassEnabled) return;

        if (_liquidGlass.HasPresentedFrame)
        {
            _glassGpuRetrySeconds = 5;
            if (_glassInitialFramePending)
            {
                _glassInitialFramePending = false;
                GlassBackdropHost.BeginAnimation(OpacityProperty, null);
                GlassBackdropHost.Opacity = _glassConfiguredOpacity;
            }
        }

        // When idle and not animating, skip redundant per-frame recalculations and DependencyProperty updates
        if (!_isAnimating && !_glassRegionPushActive && !_glassInitialFramePending)
        {
            return;
        }

        _hasCachedFrameMetrics = false;
        GetOrComputeFrameMetrics();
        _hasCachedFrameMetrics = true;
        try
        {
            if (_isAnimating || _glassRegionPushActive)
            {
                _liquidGlass.SetLiveRegion(GetGlassCaptureRegion());
            }

            double curHeight = GlassBackdropHost?.ActualHeight ?? 0;
            if (Math.Abs(curHeight - _lastActualHeight) > 0.1)
            {
                _lastActualHeight = curHeight;
                UpdateDynamicGlassParams();
            }

            UpdateDynamicFresnel(_liquidGlass.CurrentBackdropOptics);
            UpdateShaderGeometryPerFrame();
        }
        finally
        {
            _hasCachedFrameMetrics = false;
        }
    }

    private void UpdateDynamicGlassParams()
    {
        if (GlassBackdropHost == null || !IsLiquidGlassEnabled) return;
        var cfg = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();

        double height = GlassBackdropHost.ActualHeight;
        if (height <= 0) return;

        double collapsedH = _collapsedHeight > 0 ? _collapsedHeight : 32.0;

        // Accessibility: ReduceMotion locks the progress factor to 0.0 to eliminate dynamic bending/shadow motion
        double factor = VNotch.Services.AnimationConfig.ReduceMotion ? 0.0 : Math.Clamp((height - collapsedH) / 160.0, 0.0, 1.0);

        // Dynamic Shadowing (modulate opacity only; never mutate BlurRadius per frame to avoid render target reallocation)
        double activeShadowOpacity = Math.Clamp(cfg.ShadowOpacity + (1.0 - cfg.ShadowOpacity) * factor * 0.35, 0, 1);

        if (NotchShadowWrapper?.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
        {
            if (Math.Abs(dse.Opacity - activeShadowOpacity) > 0.005)
                dse.Opacity = activeShadowOpacity;
        }

        // Dynamic Specular & Fresnel edge highlighting
        double activeSpecular = cfg.Specular + (1.0 - cfg.Specular) * factor * 0.15;
        double activeFresnel = cfg.Fresnel + (1.0 - cfg.Fresnel) * factor * 0.2;

        double activeEdge = Math.Clamp(cfg.EdgeHighlight * (1.0 + factor * 0.5), 0, 1);
        ApplyOpticalRimLevels(activeEdge, activeSpecular, activeFresnel, cfg.ChromaticAberration);

        double topR = NotchBorder.CornerRadius.TopLeft;
        double bottomR = NotchBorder.CornerRadius.BottomLeft;
        if (Math.Abs(topR - _lastDynamicTopCornerR) > 0.25 ||
            Math.Abs(bottomR - _lastDynamicBottomCornerR) > 0.25)
        {
            _lastDynamicTopCornerR = topR;
            _lastDynamicBottomCornerR = bottomR;
            double activeDistortion = VNotch.Services.AnimationConfig.ReduceMotion ? 0.0 : cfg.Distortion;

            _liquidGlass?.SetParams(new LiquidGlassController.GlassParams
            {
                PowerFactor = cfg.PowerFactor,
                RefractionA = cfg.RefractionA,
                RefractionB = cfg.RefractionB,
                RefractionC = cfg.RefractionC,
                RefractionD = cfg.RefractionD,
                FPower = cfg.FPower,
                Noise = cfg.Noise,
                GlowWeight = cfg.GlowWeight,
                GlowBias = cfg.GlowBias,
                GlowEdge0 = cfg.GlowEdge0,
                GlowEdge1 = cfg.GlowEdge1,
                Refraction = cfg.Refraction,
                EdgeBend = cfg.EdgeBend,
                ChromaticAberration = cfg.ChromaticAberration,
                Distortion = activeDistortion,
                ZRadius = cfg.ZRadius,
                Saturation = cfg.Saturation,
                Brightness = cfg.Brightness,
                BevelMode = cfg.BevelMode,
                TopCornerRadius = topR,
                BottomCornerRadius = bottomR
            });
        }
    }

    private void UpdateDynamicGlassTint()
    {
        if (GlassDarkOverlay == null || !IsLiquidGlassEnabled) return;
        if (GlassDarkOverlay.Opacity > 0)
        {
            GlassDarkOverlay.BeginAnimation(OpacityProperty, null);
            GlassDarkOverlay.Opacity = 0;
        }
    }
}
