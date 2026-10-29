using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private LiquidGlassController? _liquidGlass;
    private const string LiquidGlassStyleId = "liquidglass";
    private LiquidGlassRefractionEffect? _glassRefractionEffect;
    private LiquidGlassInteractionController? _glassInteractionController;
    private BlurEffect? _glassHostBlur;
    private bool _gpuRefractionConfigured;
    private bool _gpuRefractionFailed;
    private NotchSettings _settings = new();
    private double _lastAppliedDpiScale = 1.0;
    private LiquidGlassController.GpuGeometry? _lastGpuGeometry;
    private LiquidGlassController.GpuGeometry? _lastAppliedGpuOptics;
    private LiquidGlassController.CaptureRegion? _lastPushedCaptureRegion;
    private double _lastAppliedTouchLight = -1;
    private Size _lastClipSize;
    private CornerRadius _lastClipCornerRadius;
    private double _lastClipPowerFactor = double.NaN;
    private bool? _cachedSystemTransparency;
    private long _lastTransparencyCheckTicks;
    private UiScheduledTimer? _glassResourceExpiry;

    internal void PrepareGlassForOpening()
    {
        if (!IsLiquidGlassEnabled || IsSpotlightOpen) return;
        ScheduleGlassResourceExpiry();
    }

    private void RetainGlassForReopen()
    {
        _liquidGlass?.Stop(retainGpuResources: true);
        ScheduleGlassResourceExpiry();
    }

    private void ScheduleGlassResourceExpiry()
    {
        if (_glassResourceExpiry == null)
        {
            _glassResourceExpiry = new UiScheduledTimer(
                System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _glassResourceExpiry.Tick += (_, _) =>
            {
                _glassResourceExpiry.Stop();
                if (IsSpotlightOpen) return;
                _liquidGlass?.Stop(retainGpuResources: false);
            };
        }
        _glassResourceExpiry.Stop();
        _glassResourceExpiry.Start();
    }

    private static readonly SolidColorBrush _glassBaseFill = CreateFrozenBrush(0x18, 0x0B, 0x0E, 0x12);

    internal bool IsLiquidGlassEnabled =>
        string.Equals(_settings.NotchStyle, LiquidGlassStyleId, StringComparison.OrdinalIgnoreCase);

    private bool UseGpuRefraction =>
        !_gpuRefractionFailed && (_settings.LiquidGlass?.UseGpuRefraction ?? true) &&
        LiquidGlassRefractionEffect.IsAvailable;

    internal void ApplySettings(NotchSettings settings)
    {
        bool languageChanged = !string.Equals(_settings.Language, settings.Language, StringComparison.Ordinal);
        bool restoreSavedChats = !_settings.SaveAiChatHistory && settings.SaveAiChatHistory;
        if (settings.EnableLocalOnlyMode || !settings.AllowOnlineAi)
            CancelAiRequest();
        if (_settings.SaveAiChatHistory != settings.SaveAiChatHistory)
        {
            // Start a fresh in-memory conversation; never persist an incognito chat on re-enable.
            _settings.SaveAiChatHistory = settings.SaveAiChatHistory;
            CancelAiRequest();
            _savedChats.Clear();
            _aiHistory.Clear();
            _chatId = Guid.NewGuid().ToString("N");
            _aiDraftToRestore = null;
            if (_aiMode) SearchBox.Clear();
            CloseAiHistory();
            RenderAiHistory();
        }
        bool aiSelectionChanged = !string.Equals(_settings.SpotlightAiProvider, settings.SpotlightAiProvider, StringComparison.Ordinal)
            || VNotch.Services.Spotlight.SpotlightAiService.Configuration(_settings, _settings.SpotlightAiProvider).Model
                != VNotch.Services.Spotlight.SpotlightAiService.Configuration(settings, settings.SpotlightAiProvider).Model;
        if (aiSelectionChanged)
        {
            CancelAiRequest();
            SaveAiHistory();
            _chatId = Guid.NewGuid().ToString("N");
            _aiHistory.Clear();
            _aiConversationProvider = settings.SpotlightAiProvider;
            _aiConversationModel = VNotch.Services.Spotlight.SpotlightAiService.Configuration(settings, settings.SpotlightAiProvider).Model;
            _aiDraftToRestore = null;
            CloseAiHistory();
            RenderAiHistory();
        }
        _settings = settings.Clone();
        if (restoreSavedChats) LoadAiHistory();
        _viewModel.HistoryEnabled = _settings.EnableSpotlightHistory;
        _gpuRefractionFailed = false;
        if (languageChanged)
            RefreshLocalization();
        ApplyLiquidGlassSkin();
    }

    internal void ApplyLiquidGlassSkin()
    {
        if (GlassBackdropHost == null) return;

        if (IsLiquidGlassEnabled)
        {
            bool sysTrans = IsSystemTransparencyEnabled();
            if (!sysTrans)
            {
                Shell.Background = (Brush)FindResource("ShellBrush");
                CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
                GlassMaterialClipHost.Visibility = Visibility.Collapsed;
                GlassBackdropHost.Visibility = Visibility.Collapsed;
                GlassBackdropHost.Background = null;
                GlassTintOverlay.Visibility = Visibility.Collapsed;
                SetOpticalRimVisibility(Visibility.Collapsed);
                if (GlassDarkOverlay != null) GlassDarkOverlay.Visibility = Visibility.Collapsed;

                _liquidGlass?.Stop();
                DetachGpuRefraction();
                CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
                return;
            }

            Shell.Background = Brushes.Transparent;
            Shell.BorderThickness = new Thickness(0);
            GlassBackdropHost.Background = _glassBaseFill;
            GlassMaterialClipHost.Visibility = Visibility.Visible;
            GlassBackdropHost.Visibility = Visibility.Visible;
            GlassTintOverlay.Visibility = Visibility.Visible;
            SetOpticalRimVisibility(Visibility.Visible);
            if (GlassDarkOverlay != null)
            {
                GlassDarkOverlay.Visibility = Visibility.Visible;
                if (IsSpotlightOpen && !_entranceActive && !_isClosing && !_preparingGlassEntrance)
                    AnimateGlassReadability(true);
            }
            if (GlassGrainOverlay != null) GlassGrainOverlay.Background = GlassGrainBrush.Instance;

            CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
            if (IsSpotlightOpen)
                CompositionTarget.Rendering += OnLiquidGlassFrameUpdate;

            int targetFps = _settings.LiquidGlass?.TargetFps ?? 0;
            if (targetFps <= 0) targetFps = AnimationConfig.TargetFps;

            IntPtr hwnd = EnsureHwnd();

            _liquidGlass ??= new LiquidGlassController(
                GlassBackdropImage,
                () => _hwnd,
                GetGlassCaptureRegion,
                activeFps: Math.Clamp(targetFps, 30, LiquidGlassController.MaxTargetFps),
                logTag: "SPOTLIGHT",
                maxRegionWidth: (int)Math.Ceiling(1000 * GetGlassDpiScale()),
                maxRegionHeight: (int)Math.Ceiling(700 * GetGlassDpiScale()));

            UpdateGlassCaptureExtent();

            _liquidGlass.HideFromScreenCapture = _settings.LiquidGlass?.HideFromScreenCapture ?? false;
            bool isAnimating = _entranceActive || _isClosing || _preparingGlassEntrance;
            _liquidGlass.SetAnimating(isAnimating);
            _liquidGlass.SetIdle(!isAnimating);

            if (IsSpotlightOpen)
                ConfigureGpuRefraction();
            ApplyLiquidGlassConfig();

            if (hwnd != IntPtr.Zero && IsSpotlightOpen && !_isClosing && !_preparingGlassEntrance)
            {
                _liquidGlass.Start();
            }
        }
        else
        {
            _liquidGlass?.Stop();
            DetachGpuRefraction();
            CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;

            Shell.Background = (Brush)FindResource("ShellBrush");
            CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
            GlassMaterialClipHost.Visibility = Visibility.Collapsed;
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
            RestoreShadow();
        }
    }

    private void ApplyLiquidGlassConfig()
    {
        if (GlassBackdropHost == null) return;
        var cfg = _settings.LiquidGlass ?? new LiquidGlassConfig();
        _lastGpuGeometry = null;
        _lastAppliedGpuOptics = null;

        double dipRadius = Math.Clamp(cfg.BlurAmount, 0, 1) * 28.0;
        double dpiScale = GetGlassDpiScale();
        int gaussianSigma = (int)Math.Round(dipRadius * dpiScale);
        if (_liquidGlass != null)
        {
            _liquidGlass.SetBlur(gaussianSigma);
            _liquidGlass.HideFromScreenCapture = cfg.HideFromScreenCapture;

            int targetFps = cfg.TargetFps;
            if (targetFps <= 0) targetFps = AnimationConfig.TargetFps;

            _liquidGlass.UpdateFps(Math.Clamp(targetFps, 30, LiquidGlassController.MaxTargetFps));
            if (UseGpuRefraction)
            {
                GlassBackdropImage.HorizontalAlignment = HorizontalAlignment.Left;
                GlassBackdropImage.VerticalAlignment = VerticalAlignment.Top;
                GlassBackdropImage.Width = _liquidGlass.SurfaceWidth / dpiScale;
                GlassBackdropImage.Height = _liquidGlass.SurfaceHeight / dpiScale;
            }
        }

        ApplyGpuBlur(cfg.BlurAmount);
        GlassBackdropHost.Opacity = Math.Clamp(cfg.Opacity, 0, 1);

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

        SyncGlassCornerRadius(Shell.CornerRadius);

        if (Shell.Effect is DropShadowEffect dse)
        {
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
            TopCornerRadius = Shell.CornerRadius.TopLeft,
            BottomCornerRadius = Shell.CornerRadius.BottomLeft
        });

        UpdateShaderGeometryPerFrame();
    }

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

        if (_gpuRefractionConfigured &&
            ReferenceEquals(GlassBackdropImage.Effect, _glassRefractionEffect))
            return;

        try
        {
            _glassRefractionEffect ??= new LiquidGlassRefractionEffect();
            GlassBackdropImage.Effect = _glassRefractionEffect;

            _glassInteractionController ??= new LiquidGlassInteractionController(Shell, Shell, _glassRefractionEffect);

            if (!_liquidGlass.SetGpuMode(true, ApplyGpuGeometry, OnGpuRefractionFailure))
            {
                _gpuRefractionFailed = true;
                DetachGpuRefraction();
                _liquidGlass.SetGpuMode(false, null);
                ApplyGpuBlur(0.0);
                return;
            }

            RuntimeLog.Log("LIQUIDGLASS",
                $"Spotlight GPU refraction enabled; target={Math.Clamp(_settings.LiquidGlass?.TargetFps ?? 60, 30, LiquidGlassController.MaxTargetFps)} FPS");
            _gpuRefractionConfigured = true;
        }
        catch (Exception ex)
        {
            _gpuRefractionFailed = true;
            RuntimeLog.Log("LIQUIDGLASS", $"Spotlight GPU effect attach failed; using CPU fallback: {ex.Message}");
            DetachGpuRefraction();
            _liquidGlass.SetGpuMode(false, null);
        }
    }

    private void OnGpuRefractionFailure(Exception ex)
    {
        _gpuRefractionFailed = true;
        RuntimeLog.Log("LIQUIDGLASS", $"Spotlight GPU render failed; switched to CPU fallback: {ex.Message}");
        DetachGpuRefraction();
        _liquidGlass?.SetGpuMode(false, null);
        if (IsSpotlightOpen)
        {
            CompositionTarget.Rendering -= OnLiquidGlassFrameUpdate;
            CompositionTarget.Rendering += OnLiquidGlassFrameUpdate;
        }
        ApplyLiquidGlassConfig();
    }

    private void DetachGpuRefraction()
    {
        _glassResourceExpiry?.Stop();
        _gpuRefractionConfigured = false;
        _lastGpuGeometry = null;
        _lastAppliedGpuOptics = null;
        _lastPushedCaptureRegion = null;
        _lastAppliedTouchLight = -1;
        _glassInteractionController?.Dispose();
        _glassInteractionController = null;

        if (GlassBackdropImage != null)
        {
            GlassBackdropImage.Effect = null;
            GlassBackdropImage.Width = double.NaN;
            GlassBackdropImage.Height = double.NaN;
        }
        if (GlassBackdropHost != null)
            GlassBackdropHost.Effect = null;
        _glassHostBlur = null;
    }

    private void ApplyGpuGeometry(LiquidGlassController.GpuGeometry g)
    {
        _lastGpuGeometry = g;
        if (_liquidGlass != null)
        {
            if (_liquidGlass.HasPresentedFrame && Shell.Background != Brushes.Transparent)
                Shell.Background = Brushes.Transparent;
            UpdateDynamicFresnel(_liquidGlass.CurrentBackdropOptics);
        }
        UpdateShaderGeometryPerFrame();
    }

    private void ApplyGpuBlur(double blurAmount)
    {
        if (UseGpuRefraction)
            _glassRefractionEffect ??= new LiquidGlassRefractionEffect();
        LiquidGlassSurfaceBinder.ApplyGpuBlur(GlassBackdropHost, ref _glassHostBlur, blurAmount, UseGpuRefraction, _glassRefractionEffect);
    }

    private LiquidGlassSurfaceBinder? _glassSurface;

    // Rim and Fresnel presentation shared with MainWindow. Created lazily because
    // the XAML elements only exist after InitializeComponent.
    private LiquidGlassSurfaceBinder GlassSurface => _glassSurface ??= new LiquidGlassSurfaceBinder(
        GlassDepthRimBorder, GlassCoolRimBorder, GlassWarmRimBorder, GlassFresnelBloomBorder,
        GlassFresnelBorder, GlassInnerFresnelBorder, GlassSpecularBorder, GlassRimBorder);

    private void SetOpticalRimVisibility(Visibility visibility) =>
        GlassSurface.SetOpticalRimVisibility(visibility, UseGpuRefraction);

    private void ApplyOpticalRimLevels(double edgeHighlight, double specular, double fresnel, double chroma)
    {
        var surface = GlassSurface;
        // Unlike MainWindow, Spotlight always re-asserts rim visibility here and also
        // re-shows GlassRimBorder whenever the backdrop host is visible.
        bool showRims = IsLiquidGlassEnabled && GlassBackdropHost.Visibility == Visibility.Visible;
        if (UseGpuRefraction)
        {
            surface.ApplyGpuRimOpacities(edgeHighlight);
            surface.SetCpuRimVisibility(Visibility.Collapsed);
            if (showRims)
                GlassRimBorder.Visibility = Visibility.Visible;
            return;
        }

        if (showRims)
        {
            surface.SetCpuRimVisibility(Visibility.Visible);
            GlassRimBorder.Visibility = Visibility.Visible;
        }

        surface.ApplyCpuRimOpacities(edgeHighlight, specular, fresnel, chroma);
    }

    private void UpdateDynamicFresnel(LiquidGlassController.BackdropOptics optics) =>
        GlassSurface.UpdateDynamicFresnel(optics, UseGpuRefraction);

    private void UpdateShaderGeometryPerFrame()
    {
        var fx = _glassRefractionEffect;
        var lg = _liquidGlass;
        if (!_gpuRefractionConfigured || fx == null || lg == null || GlassBackdropHost == null) return;

        double dpiScale = GetGlassDpiScale();
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1.0;

        double shellW = GlassBackdropHost.ActualWidth;
        double shellH = GlassBackdropHost.ActualHeight;
        if (!double.IsFinite(shellW) || shellW <= 0) shellW = Shell.Width;
        if (!double.IsFinite(shellH) || shellH <= 0) shellH = Shell.Height;
        if (!double.IsFinite(shellW) || shellW <= 0) shellW = 720;
        if (!double.IsFinite(shellH) || shellH <= 0) shellH = 64;

        double exactW = shellW * dpiScale;
        double exactH = shellH * dpiScale;
        var (screenLeft, screenTop) = GetShellScreenPosition(exactW, dpiScale);
        UpdateShaderGeometryPerFrame(dpiScale, exactW, exactH, screenLeft, screenTop);
    }

    private void UpdateShaderGeometryPerFrame(double dpiScale, double exactW, double exactH, double screenLeft, double screenTop)
    {
        var fx = _glassRefractionEffect;
        var lg = _liquidGlass;
        if (!_gpuRefractionConfigured || fx == null || lg == null || GlassBackdropHost == null) return;

        double sourceW = lg.SurfaceWidth;
        double sourceH = lg.SurfaceHeight;
        if (Math.Abs(GlassBackdropImage.Width - sourceW / dpiScale) > 0.01)
            GlassBackdropImage.Width = sourceW / dpiScale;
        if (Math.Abs(GlassBackdropImage.Height - sourceH / dpiScale) > 0.01)
            GlassBackdropImage.Height = sourceH / dpiScale;

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
        double topR = Shell.CornerRadius.TopLeft * dpiScale;
        double bottomR = Shell.CornerRadius.BottomLeft * dpiScale;
        if (Math.Abs(fx.TopCornerR - topR) > 0.01) fx.TopCornerR = topR;
        if (Math.Abs(fx.BottomCornerR - bottomR) > 0.01) fx.BottomCornerR = bottomR;

        var cfg = _settings.LiquidGlass ?? new LiquidGlassConfig();
        if (_lastGpuGeometry is { } g)
        {
            if (_lastAppliedGpuOptics == null || !_lastAppliedGpuOptics.Value.Equals(g))
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

    private (double ScreenLeft, double ScreenTop) GetShellScreenPosition(double exactW, double dpiScale)
    {
        try
        {
            if (GlassBackdropHost != null &&
                (_glassScreenGeometry ??= new(this)).TryGetPosition(GlassBackdropHost, out var pt))
            {
                return (pt.X, pt.Y);
            }
        }
        catch
        {
            // Visual tree might be detached or mid-transition; fallback to window coordinates
        }

        double shellDipW = exactW / dpiScale;
        double winLeft = double.IsFinite(Left) ? Left : 0;
        double winTop = double.IsFinite(Top) ? Top : 0;
        double winW = ActualWidth > 0 ? ActualWidth : Width;
        double fallbackLeft = (winLeft + Math.Max(0, (winW - shellDipW) / 2.0)) * dpiScale;
        double fallbackTop = winTop * dpiScale;
        return (fallbackLeft, fallbackTop);
    }

    private LiquidGlassController.CaptureRegion? GetGlassCaptureRegion() =>
        GetGlassCaptureRegion(out _, out _, out _, out _, out _);

    private LiquidGlassController.CaptureRegion? GetGlassCaptureRegion(
        out double dpiScale,
        out double exactW,
        out double exactH,
        out double screenLeft,
        out double screenTop)
    {
        dpiScale = 1.0;
        exactW = 0;
        exactH = 0;
        screenLeft = 0;
        screenTop = 0;

        IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : EnsureHwnd();
        if (hwnd == IntPtr.Zero || !IsSpotlightOpen || Shell == null || Shell.Visibility != Visibility.Visible)
            return null;

        dpiScale = GetGlassDpiScale();
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

        double shellW = GlassBackdropHost.ActualWidth;
        double shellH = GlassBackdropHost.ActualHeight;
        if (!double.IsFinite(shellW) || shellW <= 0) shellW = Shell.Width;
        if (!double.IsFinite(shellH) || shellH <= 0) shellH = Shell.Height;
        if (!double.IsFinite(shellW) || shellW <= 0) shellW = 720;
        if (!double.IsFinite(shellH) || shellH <= 0) shellH = 64;

        exactW = shellW * dpiScale;
        exactH = shellH * dpiScale;
        int physW = Math.Max(1, (int)Math.Round(exactW));
        int physH = Math.Max(1, (int)Math.Round(exactH));

        (screenLeft, screenTop) = GetShellScreenPosition(exactW, dpiScale);

        int physLeft = (int)Math.Round(screenLeft, MidpointRounding.AwayFromZero);
        int physTop = (int)Math.Round(screenTop, MidpointRounding.AwayFromZero);

        double subX = screenLeft - physLeft;
        double subY = screenTop - physTop;

        if (physW <= 1 || physH <= 1) return null;

        return new LiquidGlassController.CaptureRegion(
            physLeft, physTop, physW, physH,
            Shell.CornerRadius.TopLeft,
            Shell.CornerRadius.BottomLeft,
            subX, subY);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_liquidGlass == null) return;
        _lastAppliedDpiScale = -1;
        _lastPushedCaptureRegion = null;
        if (_liquidGlass.MaxRegionWidth < Math.Ceiling(1000 * newDpi.DpiScaleX) ||
            _liquidGlass.MaxRegionHeight < Math.Ceiling(700 * newDpi.DpiScaleY))
        {
            _liquidGlass.Stop();
            DetachGpuRefraction();
            _liquidGlass.Dispose();
            _liquidGlass = null;
            ApplyLiquidGlassSkin();
            return;
        }
        ApplyLiquidGlassConfig();
    }

    private static bool CaptureRegionEquals(LiquidGlassController.CaptureRegion? a, LiquidGlassController.CaptureRegion? b)
    {
        if (a.HasValue != b.HasValue) return false;
        if (!a.HasValue) return true;
        var r1 = a.Value;
        var r2 = b!.Value;
        return r1.X == r2.X
            && r1.Y == r2.Y
            && r1.Width == r2.Width
            && r1.Height == r2.Height
            && Math.Abs(r1.TopCornerRadiusDip - r2.TopCornerRadiusDip) <= 0.01
            && Math.Abs(r1.BottomCornerRadiusDip - r2.BottomCornerRadiusDip) <= 0.01
            && Math.Abs(r1.SubX - r2.SubX) <= 1e-4
            && Math.Abs(r1.SubY - r2.SubY) <= 1e-4;
    }

    private void SyncLiveGlassFrame(bool updateDynamicFresnel)
    {
        if (_liquidGlass == null || !IsLiquidGlassEnabled || !IsSpotlightOpen) return;

        if (_liquidGlass.HasPresentedFrame && Shell.Background != Brushes.Transparent)
            Shell.Background = Brushes.Transparent;

        UpdateGlassCaptureExtent();
        var region = GetGlassCaptureRegion(out double dpiScale, out double exactW, out double exactH, out double screenLeft, out double screenTop);
        bool isAnimating = _entranceActive || _isClosing || _preparingGlassEntrance;
        bool regionChanged = !CaptureRegionEquals(_lastPushedCaptureRegion, region);

        if (isAnimating || regionChanged)
        {
            _lastPushedCaptureRegion = region;
            _liquidGlass.SetLiveRegion(region);
            UpdateShaderGeometryPerFrame(dpiScale, exactW, exactH, screenLeft, screenTop);
        }

        if (updateDynamicFresnel && !UseGpuRefraction)
        {
            UpdateDynamicFresnel(_liquidGlass.CurrentBackdropOptics);
        }
    }

    private void OnLiquidGlassFrameUpdate(object? sender, EventArgs e)
    {
        SyncLiveGlassFrame(updateDynamicFresnel: true);
    }

    private void AnimateGlassReadability(bool opened, bool animate = true)
    {
        if (GlassDarkOverlay == null) return;

        double from = GlassDarkOverlay.Opacity;
        double target = opened && IsLiquidGlassEnabled ? 0.64 : 0;
        GlassDarkOverlay.BeginAnimation(OpacityProperty, null);
        GlassDarkOverlay.Opacity = target;
        if (!animate || AnimationConfig.ReduceMotion || Math.Abs(from - target) < 0.001) return;

        GlassDarkOverlay.BeginAnimation(OpacityProperty, CreateAnimation(
            from, target, TimeSpan.FromMilliseconds(opened ? 360 : 280),
            new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            }));
    }

    internal void UpdateGlassClip()
    {
        if (GlassMaterialClipHost == null || Shell == null) return;
        double w = GlassMaterialClipHost.ActualWidth;
        double h = GlassMaterialClipHost.ActualHeight;
        if (!double.IsFinite(w) || w <= 0) w = Shell.ActualWidth;
        if (!double.IsFinite(h) || h <= 0) h = Shell.ActualHeight;
        if (!double.IsFinite(w) || w <= 0) w = 720;
        if (!double.IsFinite(h) || h <= 0) h = 64;

        var size = new Size(w, h);
        var cr = Shell.CornerRadius;
        double powerFactor = _settings.LiquidGlass?.PowerFactor ?? 3;

        if (GlassMaterialClipHost.Clip != null
            && Math.Abs(_lastClipSize.Width - size.Width) <= 0.01
            && Math.Abs(_lastClipSize.Height - size.Height) <= 0.01
            && _lastClipCornerRadius == cr
            && Math.Abs(_lastClipPowerFactor - powerFactor) <= 1e-4)
        {
            return;
        }

        _lastClipSize = size;
        _lastClipCornerRadius = cr;
        _lastClipPowerFactor = powerFactor;

        var geometry = GlassClipBuilder.CreateClip(size, cr, powerFactor);
        GlassMaterialClipHost.Clip = geometry;
    }

    private void GlassMaterialClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGlassClip();
        UpdateShaderGeometryPerFrame();
    }

    private void UpdateGlassCaptureExtent()
    {
        if (_liquidGlass == null) return;

        // Spotlight binds the full D3D surface without cropping and can morph to
        // wide expanded notch states on exit; keep the full surface envelope ready.
        bool isAnimating = _entranceActive || _isClosing || _preparingGlassEntrance;
        _liquidGlass.CaptureFullSurface = isAnimating;
        _liquidGlass.SetIdle(!isAnimating);
    }

    private void GlassMaterialClipHost_LayoutUpdated(object? sender, EventArgs e)
    {
        // Publish arranged geometry so shader lens and crop match what WPF renders
        // across animated layout updates and auto-size handoffs.
        SyncLiveGlassFrame(updateDynamicFresnel: false);
    }

    private void SyncGlassCornerRadius(CornerRadius cr)
    {
        if (GlassBackdropHost != null) GlassBackdropHost.CornerRadius = cr;
        if (GlassGrainOverlay != null) GlassGrainOverlay.CornerRadius = cr;
        if (GlassTintOverlay != null) GlassTintOverlay.CornerRadius = cr;
        if (GlassDepthRimBorder != null) GlassDepthRimBorder.CornerRadius = cr;
        if (GlassCoolRimBorder != null) GlassCoolRimBorder.CornerRadius = cr;
        if (GlassWarmRimBorder != null) GlassWarmRimBorder.CornerRadius = cr;
        if (GlassFresnelBloomBorder != null) GlassFresnelBloomBorder.CornerRadius = cr;
        if (GlassFresnelBorder != null) GlassFresnelBorder.CornerRadius = cr;
        if (GlassInnerFresnelBorder != null)
        {
            double inset = 1.0;
            GlassInnerFresnelBorder.CornerRadius = new CornerRadius(
                Math.Max(0, cr.TopLeft - inset),
                Math.Max(0, cr.TopRight - inset),
                Math.Max(0, cr.BottomRight - inset),
                Math.Max(0, cr.BottomLeft - inset));
        }
        if (GlassDarkOverlay != null) GlassDarkOverlay.CornerRadius = cr;
        if (GlassRimBorder != null) GlassRimBorder.CornerRadius = cr;
        if (GlassSpecularBorder != null) GlassSpecularBorder.CornerRadius = cr;
        UpdateGlassClip();
    }

    private WindowScreenGeometry? _glassScreenGeometry;

    private double GetGlassDpiScale()
    {
        try
        {
            double scale = (_glassScreenGeometry ??= new(this)).DpiScaleX;
            return scale > 0 ? scale : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private bool IsSystemTransparencyEnabled()
    {
        long now = Environment.TickCount64;
        if (_cachedSystemTransparency.HasValue && now - _lastTransparencyCheckTicks < 5000)
            return _cachedSystemTransparency.Value;

        _lastTransparencyCheckTicks = now;
        bool enabled = LiquidGlassSurfaceBinder.ReadSystemTransparencySetting();
        _cachedSystemTransparency = enabled;
        return enabled;
    }

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
