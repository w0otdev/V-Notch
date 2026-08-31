using System;
using System.Windows;
using System.Windows.Media;
using VNotch.Controllers;

namespace VNotch;

public partial class MainWindow
{
    private LiquidGlassController? _liquidGlass;

    private const string LiquidGlassStyleId = "liquidglass";

    private LiquidGlassRefractionEffect? _glassRefractionEffect;
    private LiquidGlassInteractionController? _glassInteractionController;
    private bool _gpuRefractionConfigured;

    private bool UseGpuRefraction =>
        (_settings.LiquidGlass?.UseGpuRefraction ?? true) &&
        LiquidGlassRefractionEffect.IsAvailable;

    private bool IsLiquidGlassEnabled =>
        _settings.EnableDynamicIslandMode &&
        string.Equals(_settings.NotchStyle, LiquidGlassStyleId, StringComparison.OrdinalIgnoreCase);

    private void ApplyLiquidGlassSkin()
    {
        if (GlassBackdropHost == null) return;

        if (IsLiquidGlassEnabled)
        {
            NotchBackground.Opacity = 0;
            ExpandedContent.Background = System.Windows.Media.Brushes.Transparent;

            // Opaque dark base behind the live glass image. The refraction image is
            GlassBackdropHost.Background = _glassBaseFill;

            bool sysTrans = IsSystemTransparencyEnabled();
            if (!sysTrans)
            {
                NotchBackground.Opacity = 1;
                ExpandedContent.Background = (System.Windows.Media.Brush)FindResource("NotchGradient");

                GlassBackdropHost.Visibility = Visibility.Collapsed;
                GlassTintOverlay.Visibility = Visibility.Collapsed;
                SetOpticalRimVisibility(Visibility.Collapsed);
                if (GlassDarkOverlay != null) GlassDarkOverlay.Visibility = Visibility.Collapsed;

                _liquidGlass?.Stop();
                DetachGpuRefraction();
                return;
            }

            GlassBackdropHost.Visibility = Visibility.Visible;
            GlassTintOverlay.Visibility = Visibility.Visible;
            SetOpticalRimVisibility(Visibility.Visible);
            if (GlassDarkOverlay != null) GlassDarkOverlay.Visibility = Visibility.Visible;

            CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
            CompositionTarget.Rendering += OnLiquidGlassFrameUpdate;

            int targetFps = _settings.LiquidGlass?.TargetFps ?? 0;
            if (targetFps <= 0 || targetFps == 60) targetFps = VNotch.Services.AnimationConfig.TargetFps;

            _liquidGlass ??= new LiquidGlassController(
                GlassBackdropImage,
                () => _hwnd,
                GetGlassCaptureRegion,
                // This is a hard render cadence: unchanged desktop frames are still
                activeFps: Math.Clamp(targetFps, 30, LiquidGlassController.MaxTargetFps),
                logTag: "ISLAND");

            // Magnifier capture excludes the notch internally while the user-facing
            _liquidGlass.HideFromScreenCapture = false;

            // Match the controller to the notch's current motion state so it starts
            _liquidGlass.SetAnimating(_isAnimating);

            ConfigureGpuRefraction();

            ApplyLiquidGlassConfig();
            _liquidGlass.Start();
            SyncGlassCornerRadius(NotchBorder.CornerRadius);
            ApplyGlassContentShadow(true);
            ApplyGlassToTimerBar(true);
            ApplyGlassToTimerFinishedView(true);
            ApplyGlassPanelMaterial(true);
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
            HideLyricsBlurBackground();
        }
        else
        {
            _liquidGlass?.Stop();
            DetachGpuRefraction();

            CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;

            ApplyGlassContentShadow(false);
            ApplyGlassToTimerBar(false);
            ApplyGlassToTimerFinishedView(false);
            ApplyGlassPanelMaterial(false);

            GlassBackdropHost.Visibility = Visibility.Collapsed;
            GlassBackdropHost.Background = null;
            GlassTintOverlay.Visibility = Visibility.Collapsed;
            SetOpticalRimVisibility(Visibility.Collapsed);
            if (GlassDarkOverlay != null)
            {
                GlassDarkOverlay.Visibility = Visibility.Collapsed;
                GlassDarkOverlay.Opacity = 0;
            }

            NotchBackground.Opacity = 1;
            ExpandedContent.Background = (System.Windows.Media.Brush)FindResource("NotchGradient");
            RestoreNotchShadowDefaults();
        }
    }

    private void ApplyLiquidGlassConfig()
    {
        if (GlassBackdropHost == null) return;
        var cfg = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();

        double dipRadius = Math.Clamp(cfg.BlurAmount, 0, 1) * 28.0;
        double dpiScale = GetGlassDpiScale();
        int gaussianSigma = (int)Math.Round(dipRadius * dpiScale);
        if (_liquidGlass != null)
        {
            _liquidGlass.SetBlur(gaussianSigma);

            int targetFps = cfg.TargetFps;
            if (targetFps <= 0 || targetFps == 60) targetFps = VNotch.Services.AnimationConfig.TargetFps;

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

        GlassBackdropHost.Opacity = Math.Clamp(cfg.Opacity, 0, 1);

        ApplyOpticalRimLevels(cfg.EdgeHighlight, cfg.Specular, cfg.Fresnel, cfg.ChromaticAberration);

        if (_glassRefractionEffect != null)
        {
            _glassRefractionEffect.HighlightStrength = cfg.TouchLight;
        }

        SyncGlassCornerRadius(NotchBorder.CornerRadius);

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
            Distortion = cfg.Distortion,
            ZRadius = cfg.ZRadius,
            Saturation = cfg.Saturation,
            Brightness = cfg.Brightness,
            BevelMode = cfg.BevelMode,
            TopCornerRadius = NotchBorder.CornerRadius.TopLeft,
            BottomCornerRadius = NotchBorder.CornerRadius.BottomLeft
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
                DetachGpuRefraction();
                _liquidGlass.SetGpuMode(false, null);
                ApplyGpuBlur(0.0);
                return;
            }

            VNotch.Services.RuntimeLog.Log("LIQUIDGLASS",
                $"GPU refraction enabled; target={Math.Clamp(_settings.LiquidGlass?.TargetFps ?? 60, 30, LiquidGlassController.MaxTargetFps)} FPS");
            _gpuRefractionConfigured = true;
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Log("LIQUIDGLASS", $"GPU effect attach failed; using CPU fallback: {ex.Message}");
            DetachGpuRefraction();
            _liquidGlass.SetGpuMode(false, null);
        }
    }

    private void OnGpuRefractionFailure(Exception ex)
    {
        VNotch.Services.RuntimeLog.Log("LIQUIDGLASS", $"GPU render failed; switched to CPU fallback: {ex.Message}");
        DetachGpuRefraction();
        _liquidGlass?.SetGpuMode(false, null);
        ApplyGpuBlur(0.0);
    }

    private void DetachGpuRefraction()
    {
        _gpuRefractionConfigured = false;

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

    /// <summary>Pushes the per-frame shader geometry from the controller into the
    /// effect. Invoked on the UI thread by the controller's present.</summary>
    private void ApplyGpuGeometry(LiquidGlassController.GpuGeometry g)
    {
        _lastGpuGeometry = g;
        UpdateShaderGeometryPerFrame();
    }

    private (double Width, double Height) GetCurrentInstantaneousNotchSize()
    {
        double w = double.NaN;
        double h = double.NaN;

        if (NotchBorder != null)
        {
            object valW = NotchBorder.GetValue(FrameworkElement.WidthProperty);
            if (valW is double dW && !double.IsNaN(dW) && dW > 0)
                w = dW;
            else if (NotchBorder.ActualWidth > 0)
                w = NotchBorder.ActualWidth;

            object valH = NotchBorder.GetValue(FrameworkElement.HeightProperty);
            if (valH is double dH && !double.IsNaN(dH) && dH > 0)
                h = dH;
            else if (NotchBorder.ActualHeight > 0)
                h = NotchBorder.ActualHeight;
        }

        if (double.IsNaN(w) || w <= 0) w = _collapsedWidth;
        if (double.IsNaN(h) || h <= 0) h = _collapsedHeight;

        return (w, h);
    }

    private void UpdateShaderGeometryPerFrame()
    {
        var fx = _glassRefractionEffect;
        var lg = _liquidGlass;
        if (fx == null || lg == null || GlassBackdropHost == null) return;

        double dpiScale = GetGlassDpiScale();
        var (notchW, notchH) = GetCurrentInstantaneousNotchSize();
        double exactW = notchW * dpiScale;
        double exactH = notchH * dpiScale;

        double screenLeft = _fixedX + (_windowWidth - exactW) / 2.0;
        double screenTop = _fixedY + (NotchContainerTranslate?.Y ?? 0) * dpiScale;

        int captureOriginX = lg.LastPresentedCaptureOriginX;
        int captureOriginY = lg.LastPresentedCaptureOriginY;
        double offX, offY;
        if (captureOriginX != int.MinValue && captureOriginY != int.MinValue)
        {
            offX = screenLeft - captureOriginX;
            offY = screenTop - captureOriginY;
        }
        else if (_lastGpuGeometry is { } lastGeom && lastGeom.CaptureOriginX != 0)
        {
            offX = screenLeft - lastGeom.CaptureOriginX;
            offY = screenTop - lastGeom.CaptureOriginY;
        }
        else
        {
            offX = _lastGpuGeometry?.OffX ?? 0;
            offY = _lastGpuGeometry?.OffY ?? 0;
        }

        fx.SrcW = lg.SurfaceWidth;
        fx.SrcH = lg.SurfaceHeight;
        fx.NotchW = exactW;
        fx.NotchH = exactH;
        fx.OffX = offX;
        fx.OffY = offY;
        fx.TopCornerR = NotchBorder.CornerRadius.TopLeft * dpiScale;
        fx.BottomCornerR = NotchBorder.CornerRadius.BottomLeft * dpiScale;

        var cfg = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();
        fx.PowerFactor = cfg.PowerFactor;
        fx.A = cfg.RefractionA;
        fx.B = cfg.RefractionB;
        fx.C = cfg.RefractionC;
        fx.D = cfg.RefractionD;
        fx.FPower = cfg.FPower;
        fx.Noise = cfg.Noise;
        fx.GlowWeight = cfg.GlowWeight;
        fx.GlowBias = cfg.GlowBias;
        fx.GlowEdge0 = cfg.GlowEdge0;
        fx.GlowEdge1 = cfg.GlowEdge1;
        fx.Chroma = cfg.ChromaticAberration;
        fx.SatFactor = 1.0 + cfg.Saturation;
        fx.BrightAdd = cfg.Brightness;
        fx.EdgeBend = cfg.EdgeBend;
        fx.BevelMode = cfg.BevelMode;
        fx.HighlightStrength = cfg.TouchLight;

        if (_lastGpuGeometry is { } g)
        {
            fx.PowerFactor = g.PowerFactor;
            fx.A = g.A;
            fx.B = g.B;
            fx.C = g.C;
            fx.D = g.D;
            fx.FPower = g.FPower;
            fx.Noise = g.Noise;
            fx.GlowWeight = g.GlowWeight;
            fx.GlowBias = g.GlowBias;
            fx.GlowEdge0 = g.GlowEdge0;
            fx.GlowEdge1 = g.GlowEdge1;
            fx.Chroma = g.Chroma;
            fx.EdgeBend = g.EdgeBend;
            fx.BevelMode = g.BevelMode;
            fx.SatFactor = g.SatFactor;
            fx.BrightAdd = g.BrightAdd;
        }
    }

    /// <summary>Applies the legacy GPU-mode host blur. CPU Liquid Glass blurs the
    /// captured source before refraction for a cleaner material result.</summary>
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
    private static readonly SolidColorBrush _glassBaseFill = Frozen(0xFF, 0x0B, 0x0E, 0x12);

    private void SetOpticalRimVisibility(Visibility visibility)
    {
        GlassDepthRimBorder.Visibility = visibility;
        GlassCoolRimBorder.Visibility = visibility;
        GlassWarmRimBorder.Visibility = visibility;
        GlassFresnelBloomBorder.Visibility = visibility;
        GlassFresnelBorder.Visibility = visibility;
        GlassInnerFresnelBorder.Visibility = visibility;
        GlassRimBorder.Visibility = visibility;
        GlassSpecularBorder.Visibility = visibility;
    }

    private void ApplyOpticalRimLevels(double edgeHighlight, double specular, double fresnel, double chroma)
    {
        if (UseGpuRefraction)
        {
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

        brush.Center = new Point(_dynamicFresnelX, _dynamicFresnelY);
        brush.GradientOrigin = new Point(
            Math.Clamp(0.5 + (_dynamicFresnelX - 0.5) * 0.72, 0.30, 0.62),
            Math.Clamp(0.5 + (_dynamicFresnelY - 0.5) * 0.72, 0.26, 0.58));
        brush.RadiusX = 0.84 - _dynamicFresnelContrast * 0.08;
        brush.RadiusY = 0.90 - _dynamicFresnelContrast * 0.06;

        // Keep the inner reflection axis fixed. Normalizing the tiny per-frame

        Color bright = InterpolateColor(_dynamicFresnelTint, Colors.White, 0.68);
        Color mid = InterpolateColor(_dynamicFresnelTint, Colors.White, 0.42);
        brush.GradientStops[0].Color = Color.FromArgb(255, bright.R, bright.G, bright.B);
        brush.GradientStops[1].Color = Color.FromArgb(200, mid.R, mid.G, mid.B);
        brush.GradientStops[2].Color = Color.FromArgb(
            112, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B);
        brush.GradientStops[3].Color = Color.FromArgb(
            18, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B);
        brush.GradientStops[4].Color = Color.FromArgb(
            0, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B);

        innerBrush.GradientStops[0].Color = Color.FromArgb(255, bright.R, bright.G, bright.B);
        innerBrush.GradientStops[1].Color = Color.FromArgb(
            150, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B);
        innerBrush.GradientStops[2].Color = Color.FromArgb(
            18, _dynamicFresnelTint.R, _dynamicFresnelTint.G, _dynamicFresnelTint.B);
        innerBrush.GradientStops[3].Color = Color.FromArgb(
            (byte)Math.Round(82 + _dynamicFresnelContrast * 48), 0, 0, 0);
        innerBrush.GradientStops[4].Color = Color.FromArgb(184, mid.R, mid.G, mid.B);

        double fresnelEnergy = Math.Sqrt(Math.Clamp(_activeFresnelLevel, 0.0, 1.0));
        double contrastResponse = 0.94 + _dynamicFresnelContrast * 0.12;
        GlassFresnelBloomBorder.Opacity = Math.Clamp(
            fresnelEnergy * (0.24 + _dynamicFresnelContrast * 0.08), 0.0, 0.42);
        GlassFresnelBorder.Opacity = Math.Clamp(
            fresnelEnergy * 0.98 * contrastResponse, 0.0, 1.0);
        GlassInnerFresnelBorder.Opacity = Math.Clamp(
            fresnelEnergy * (0.58 + _dynamicFresnelContrast * 0.10), 0.0, 0.76);
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
    private static readonly SolidColorBrush _glassDashStroke = Frozen(0x40, 255, 255, 255);
    private static readonly SolidColorBrush _defaultPanelBg = Frozen(0xFF, 0x1A, 0x1A, 0x1A);
    private static readonly SolidColorBrush _defaultDashStroke = Frozen(0xFF, 0x33, 0x33, 0x33);
    // The idle camera box should read the same as the file tray (both #1A1A1A);
    private static readonly SolidColorBrush _cameraOverlayDefault = Frozen(0x00, 0, 0, 0);
    private static readonly SolidColorBrush _defaultAnimThumbnailBorder = Frozen(0xFF, 0x33, 0x33, 0x33);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Gives the camera box and file-tray the same translucent glass material as
    /// the audio redirect frame (only while the Liquid Glass skin is active), so
    /// the refracted backdrop shows through them. Restores the solid look otherwise.
    /// </summary>
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

            if (FileShelf != null)
            {
                FileShelf.Background = _glassPanelBg;
                FileShelf.BorderBrush = _glassPanelBorder;
                FileShelf.BorderThickness = new Thickness(1);
            }

            if (FileShelfDashedBorder != null)
                FileShelfDashedBorder.Stroke = _glassDashStroke;
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

            if (FileShelf != null)
            {
                FileShelf.Background = _defaultPanelBg;
                FileShelf.BorderBrush = null;
                FileShelf.BorderThickness = new Thickness(0);
            }

            if (FileShelfDashedBorder != null)
                FileShelfDashedBorder.Stroke = _defaultDashStroke;
        }
    }

    // Frosted translucent track for the media progress bar while the Liquid Glass
    private static readonly SolidColorBrush _glassProgressTrack = Frozen(0x59, 255, 255, 255);
    private Brush? _progressTrackDefaultBg;
    private bool _progressTrackDefaultCaptured;

    private void ApplyGlassToProgressBar(bool glass)
    {
        if (ProgressBarBg == null) return;

        if (!_progressTrackDefaultCaptured)
        {
            _progressTrackDefaultBg = ProgressBarBg.Background;
            _progressTrackDefaultCaptured = true;
        }

        ProgressBarBg.Background = glass ? _glassProgressTrack : _progressTrackDefaultBg;
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

    private void SyncGlassCornerRadius(CornerRadius cr)
    {
        if (GlassBackdropHost == null) return;
        GlassBackdropHost.CornerRadius = cr;
        GlassTintOverlay.CornerRadius = cr;
        GlassDepthRimBorder.CornerRadius = cr;
        GlassCoolRimBorder.CornerRadius = cr;
        GlassWarmRimBorder.CornerRadius = cr;
        GlassFresnelBloomBorder.CornerRadius = cr;
        GlassFresnelBorder.CornerRadius = cr;
        GlassInnerFresnelBorder.CornerRadius = cr;
        GlassRimBorder.CornerRadius = cr;
        GlassSpecularBorder.CornerRadius = cr;
        if (GlassDarkOverlay != null) GlassDarkOverlay.CornerRadius = cr;
    }

    private double GetGlassDpiScale()
    {
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        return scale > 0 ? scale : 1.0;
    }

    private void InvalidateGlassDpiScale() { }

    // Hover applies a transient scale to the collapsed notch without flipping the
    private bool _glassHoverMotion;
    private int _glassHoverGen;
    private bool _glassGestureSnapBackMotion;
    private int _glassGestureSnapBackGen;

    /// <summary>
    /// Marks the glass as "in motion" for the lifetime of a hover scale animation,
    /// so it tracks the moving notch smoothly. A generation token guards against a
    /// superseded animation's Completed event clearing a newer motion state.
    /// </summary>
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
        var safetyTimer = new System.Windows.Threading.DispatcherTimer(
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

    /// <summary>
    /// Keeps backdrop capture locked to the translated notch until the gesture
    /// spring has actually returned to rest. Mouse capture ends before this visual
    /// animation does, so gesture state alone is not long-lived enough.
    /// </summary>
    private void BeginGlassGestureSnapBack(System.Windows.Media.Animation.AnimationTimeline completionAnim)
    {
        if (_liquidGlass == null || !IsLiquidGlassEnabled || completionAnim == null) return;

        int gen = ++_glassGestureSnapBackGen;
        _glassGestureSnapBackMotion = true;
        UpdateGlassMotionState();

        completionAnim.Completed += (_, _) =>
        {
            if (gen != _glassGestureSnapBackGen) return;
            _glassGestureSnapBackMotion = false;
            UpdateGlassMotionState();
        };
    }

    private bool _glassRegionPushActive;

    private void UpdateGlassMotionState()
    {
        // Hover applies a ScaleTransform (NotchScale) that renders the notch â€” and
        bool motion = _isAnimating || _isGestureActive ||
                      _glassGestureSnapBackMotion || _glassHoverMotion;

        _liquidGlass?.SetAnimating(motion);
        // We no longer pause presentation during hover, DXGI handles it smoothly.
        SetGlassRegionPush(motion && _liquidGlass != null && IsLiquidGlassEnabled);
    }

    /// <summary>While the notch moves, push the capture region from the UI thread each
    /// compositor frame so the worker need not pull it synchronously at Send priority.</summary>
    private void SetGlassRegionPush(bool enabled)
    {
        if (enabled == _glassRegionPushActive) return;
        _glassRegionPushActive = enabled;

        if (enabled)
        {
            CompositionTarget.Rendering += OnGlassRegionRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnGlassRegionRendering;
            _liquidGlass?.ClearLiveRegion();
        }
    }

    private void OnGlassRegionRendering(object? sender, EventArgs e)
    {
        if (_liquidGlass == null) { SetGlassRegionPush(false); return; }
        _liquidGlass.SetLiveRegion(GetGlassCaptureRegion());
        UpdateShaderGeometryPerFrame();
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
            NotchContent.Effect = enable ? GetOrCreateGlassContentShadow() : null;
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
        if (TimerControlBar == null) return;

        if (!_timerBarDefaultsCaptured)
        {
            _timerBarDefaultBg = TimerControlBar.Background;
            _timerBarDefaultsCaptured = true;
        }

        if (glass)
        {
            if (_glassPanelTint == null)
            {
                _glassPanelTint = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
                _glassPanelTint.Freeze();
            }
            TimerControlBar.Background = _glassPanelTint;
            if (TimerControlBarShadow != null) TimerControlBarShadow.Opacity = 0;
        }
        else
        {
            TimerControlBar.Background = _timerBarDefaultBg;
            if (TimerControlBarShadow != null) TimerControlBarShadow.Opacity = 0.45;
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
                CountdownCompleteText.Foreground = System.Windows.Media.Brushes.White;

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

        double dpiScale = GetGlassDpiScale();
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

        var (notchW, notchH) = GetCurrentInstantaneousNotchSize();
        if (notchW <= 0 || notchH <= 0) return null;

        double exactW = notchW * dpiScale;
        double exactH = notchH * dpiScale;
        int physW = Math.Max(1, (int)Math.Round(exactW));
        int physH = Math.Max(1, (int)Math.Round(exactH));

        double screenLeft = _fixedX + (_windowWidth - exactW) / 2.0;
        double screenTop = _fixedY + (NotchContainerTranslate?.Y ?? 0) * dpiScale;

        int physLeft = (int)Math.Round(screenLeft, MidpointRounding.AwayFromZero);
        int physTop = (int)Math.Round(screenTop, MidpointRounding.AwayFromZero);

        double subX = screenLeft - physLeft;
        double subY = screenTop - physTop;

        if (physTop < 0) { physH += physTop; physTop = 0; }
        if (physLeft < 0) { physW += physLeft; physLeft = 0; }
        if (physW <= 1 || physH <= 1) return null;

        return new LiquidGlassController.CaptureRegion(
            physLeft, physTop, physW, physH,
            NotchBorder.CornerRadius.TopLeft,
            NotchBorder.CornerRadius.BottomLeft,
            subX, subY);
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
    }

    private void ApplyDynamicGlassTint()
    {
        if (GlassTintOverlay != null)
        {
            GlassTintOverlay.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private double _lastActualHeight = -1;

    private void OnLiquidGlassFrameUpdate(object? sender, EventArgs e)
    {
        if (_liquidGlass == null || !IsLiquidGlassEnabled) return;

        double curHeight = GlassBackdropHost?.ActualHeight ?? 0;
        if (Math.Abs(curHeight - _lastActualHeight) > 0.1)
        {
            _lastActualHeight = curHeight;
            UpdateDynamicGlassParams();
        }

        UpdateDynamicFresnel(_liquidGlass.CurrentBackdropOptics);
        UpdateDynamicGlassTint(_liquidGlass.AverageBackgroundBrightness);
        UpdateShaderGeometryPerFrame();
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

        // Preserve optical density as the notch grows. The previous 65%/40% boosts
        double activeZRadius = cfg.ZRadius * (1.0 + factor * 0.12);
        double activeRefraction = cfg.Refraction * (1.0 + factor * 0.06);

        // 2. Dynamic Shadowing (larger elements float higher and cast wider, darker shadows)
        double activeShadowOpacity = cfg.ShadowOpacity + (1.0 - cfg.ShadowOpacity) * factor * 0.35;
        double activeShadowSpread = cfg.ShadowSpread * (1.0 + factor * 1.4);

        if (NotchShadowWrapper?.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
        {
            dse.Opacity = Math.Clamp(activeShadowOpacity, 0, 1);
            dse.BlurRadius = Math.Clamp(activeShadowSpread, 0, 150);
        }

        // 3. Dynamic Specular & Fresnel edge highlighting
        double activeSpecular = cfg.Specular + (1.0 - cfg.Specular) * factor * 0.15;
        double activeFresnel = cfg.Fresnel + (1.0 - cfg.Fresnel) * factor * 0.2;

        double activeEdge = Math.Clamp(cfg.EdgeHighlight * (1.0 + factor * 0.5), 0, 1);
        ApplyOpticalRimLevels(activeEdge, activeSpecular, activeFresnel, cfg.ChromaticAberration);

        // Accessibility: ReduceMotion sets refraction distortion to a flat minimum
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
            Refraction = activeRefraction,
            EdgeBend = cfg.EdgeBend,
            ChromaticAberration = cfg.ChromaticAberration,
            Distortion = activeDistortion,
            ZRadius = activeZRadius,
            Saturation = cfg.Saturation,
            Brightness = cfg.Brightness,
            BevelMode = cfg.BevelMode,
            TopCornerRadius = NotchBorder.CornerRadius.TopLeft,
            BottomCornerRadius = NotchBorder.CornerRadius.BottomLeft
        });
    }

    private void UpdateDynamicGlassTint(double bgBrightness)
    {
        if (GlassDarkOverlay == null || !IsLiquidGlassEnabled) return;

        // True Apple HIG Materials rely on the internal shader's Brightness/Saturation variables 
        if (GlassDarkOverlay.Opacity > 0)
        {
            GlassDarkOverlay.BeginAnimation(OpacityProperty, null);
            GlassDarkOverlay.Opacity = 0;
        }
    }
}
