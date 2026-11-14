using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controllers;

internal sealed partial class LiquidGlassSurfaceBinder
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnCpuRimOpacities
    {
        public double ActiveFresnel;
        public double Rim;
        public double DepthRim;
        public double FresnelBloom;
        public double Fresnel;
        public double InnerFresnel;
        public double Specular;
        public double CoolRim;
        public double WarmRim;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnDynamicFresnelStep
    {
        public double FresnelX;
        public double FresnelY;
        public double FresnelContrast;
        public byte TintR;
        public byte TintG;
        public byte TintB;
        public byte BrightR;
        public byte BrightG;
        public byte BrightB;
        public byte MidR;
        public byte MidG;
        public byte MidB;
        public byte InnerShadowA;
        public double OriginX;
        public double OriginY;
        public double RadiusX;
        public double RadiusY;
        public double BloomOpacity;
        public double FresnelOpacity;
        public double InnerFresnelOpacity;
    }

    [LibraryImport("VNotch.Native")]
    private static partial int vn_glass_binder_compute_cpu_rim_opacities(
        double edge_highlight, double specular, double fresnel, double chroma, out VnCpuRimOpacities out_opacities);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_glass_binder_step_dynamic_fresnel(
        long now_ticks, long last_ticks, double active_fresnel_level,
        double light_x, double light_y, double contrast,
        byte optic_r, byte optic_g, byte optic_b,
        double cur_x, double cur_y, double cur_contrast,
        byte cur_tint_r, byte cur_tint_g, byte cur_tint_b,
        out VnDynamicFresnelStep out_step);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_glass_binder_read_system_transparency(out byte out_enabled);

    private readonly Border _depthRim;
    private readonly Border _coolRim;
    private readonly Border _warmRim;
    private readonly Border _fresnelBloom;
    private readonly Border _fresnel;
    private readonly Border _innerFresnel;
    private readonly Border _specular;
    private readonly Border _rim;

    private RadialGradientBrush? _dynamicFresnelBrush;
    private LinearGradientBrush? _dynamicInnerFresnelBrush;
    private double _activeFresnelLevel;
    private double _dynamicFresnelX = 0.42;
    private double _dynamicFresnelY = 0.34;
    private double _dynamicFresnelContrast;
    private Color _dynamicFresnelTint = Color.FromRgb(126, 154, 180);
    private long _lastDynamicFresnelTicks;

    public LiquidGlassSurfaceBinder(
        Border depthRim, Border coolRim, Border warmRim, Border fresnelBloom,
        Border fresnel, Border innerFresnel, Border specular, Border rim)
    {
        _depthRim = depthRim;
        _coolRim = coolRim;
        _warmRim = warmRim;
        _fresnelBloom = fresnelBloom;
        _fresnel = fresnel;
        _innerFresnel = innerFresnel;
        _specular = specular;
        _rim = rim;
    }

    /// <summary>The seven CPU-only rim layers (everything except the outer rim) are visible.</summary>
    public bool CpuRimsVisible => _depthRim.Visibility == Visibility.Visible;

    /// <summary>The seven CPU-only rim layers are collapsed.</summary>
    public bool CpuRimsCollapsed => _depthRim.Visibility == Visibility.Collapsed;

    public void SetOpticalRimVisibility(Visibility visibility, bool useGpuRefraction)
    {
        Visibility cpuRimVis = visibility == Visibility.Visible && useGpuRefraction
            ? Visibility.Collapsed
            : visibility;
        SetCpuRimVisibility(cpuRimVis);
        _rim.Visibility = visibility;
    }

    /// <summary>Sets visibility on the seven CPU-only rim layers; the outer rim is untouched.</summary>
    public void SetCpuRimVisibility(Visibility visibility)
    {
        _depthRim.Visibility = visibility;
        _coolRim.Visibility = visibility;
        _warmRim.Visibility = visibility;
        _fresnelBloom.Visibility = visibility;
        _fresnel.Visibility = visibility;
        _innerFresnel.Visibility = visibility;
        _specular.Visibility = visibility;
    }

    /// <summary>GPU mode: the shader draws the optics, so only the outer rim keeps an opacity.</summary>
    public void ApplyGpuRimOpacities(double edgeHighlight)
    {
        GlassVisualUpdates.SetDouble(_depthRim, UIElement.OpacityProperty, 0);
        GlassVisualUpdates.SetDouble(_coolRim, UIElement.OpacityProperty, 0);
        GlassVisualUpdates.SetDouble(_warmRim, UIElement.OpacityProperty, 0);
        GlassVisualUpdates.SetDouble(_fresnelBloom, UIElement.OpacityProperty, 0);
        GlassVisualUpdates.SetDouble(_fresnel, UIElement.OpacityProperty, 0);
        GlassVisualUpdates.SetDouble(_innerFresnel, UIElement.OpacityProperty, 0);
        GlassVisualUpdates.SetDouble(_specular, UIElement.OpacityProperty, 0);
        GlassVisualUpdates.SetDouble(_rim, UIElement.OpacityProperty, Math.Clamp(edgeHighlight * 0.50, 0.0, 0.65));
    }

    /// <summary>CPU mode: derives every rim layer opacity from the configured optics levels.</summary>
    public void ApplyCpuRimOpacities(double edgeHighlight, double specular, double fresnel, double chroma)
    {
        EnsureDynamicFresnelBrush();

        vn_glass_binder_compute_cpu_rim_opacities(edgeHighlight, specular, fresnel, chroma, out var opacities);
        _activeFresnelLevel = opacities.ActiveFresnel;

        // A square-root response preserves a delicate rim at low slider values
        GlassVisualUpdates.SetDouble(_rim, UIElement.OpacityProperty, opacities.Rim);
        GlassVisualUpdates.SetDouble(_depthRim, UIElement.OpacityProperty, opacities.DepthRim);
        GlassVisualUpdates.SetDouble(_fresnelBloom, UIElement.OpacityProperty, opacities.FresnelBloom);
        GlassVisualUpdates.SetDouble(_fresnel, UIElement.OpacityProperty, opacities.Fresnel);
        GlassVisualUpdates.SetDouble(_innerFresnel, UIElement.OpacityProperty, opacities.InnerFresnel);
        GlassVisualUpdates.SetDouble(_specular, UIElement.OpacityProperty, opacities.Specular);
        GlassVisualUpdates.SetDouble(_coolRim, UIElement.OpacityProperty, opacities.CoolRim);
        GlassVisualUpdates.SetDouble(_warmRim, UIElement.OpacityProperty, opacities.WarmRim);
    }

    public void EnsureDynamicFresnelBrush()
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

        _fresnelBloom.BorderBrush = _dynamicFresnelBrush;
        _fresnel.BorderBrush = _dynamicFresnelBrush;
        _innerFresnel.BorderBrush = _dynamicInnerFresnelBrush;
    }

    public void UpdateDynamicFresnel(LiquidGlassController.BackdropOptics optics, bool useGpuRefraction)
    {
        if (useGpuRefraction || _activeFresnelLevel <= 0.001) return;

        EnsureDynamicFresnelBrush();
        var brush = _dynamicFresnelBrush!;
        var innerBrush = _dynamicInnerFresnelBrush!;

        long now = Environment.TickCount64;
        vn_glass_binder_step_dynamic_fresnel(
            now,
            _lastDynamicFresnelTicks,
            _activeFresnelLevel,
            optics.LightX,
            optics.LightY,
            optics.Contrast,
            optics.Red,
            optics.Green,
            optics.Blue,
            _dynamicFresnelX,
            _dynamicFresnelY,
            _dynamicFresnelContrast,
            _dynamicFresnelTint.R,
            _dynamicFresnelTint.G,
            _dynamicFresnelTint.B,
            out var step);
        _lastDynamicFresnelTicks = now;

        _dynamicFresnelX = step.FresnelX;
        _dynamicFresnelY = step.FresnelY;
        _dynamicFresnelContrast = step.FresnelContrast;
        _dynamicFresnelTint = Color.FromRgb(step.TintR, step.TintG, step.TintB);

        GlassVisualUpdates.SetPoint(brush, RadialGradientBrush.CenterProperty, new Point(step.FresnelX, step.FresnelY));
        GlassVisualUpdates.SetPoint(brush, RadialGradientBrush.GradientOriginProperty, new Point(step.OriginX, step.OriginY));
        GlassVisualUpdates.SetDouble(brush, RadialGradientBrush.RadiusXProperty, step.RadiusX);
        GlassVisualUpdates.SetDouble(brush, RadialGradientBrush.RadiusYProperty, step.RadiusY);

        // Keep the inner reflection axis fixed. Normalizing the tiny per-frame

        GlassVisualUpdates.SetColor(brush.GradientStops[0], Color.FromArgb(255, step.BrightR, step.BrightG, step.BrightB));
        GlassVisualUpdates.SetColor(brush.GradientStops[1], Color.FromArgb(200, step.MidR, step.MidG, step.MidB));
        GlassVisualUpdates.SetColor(brush.GradientStops[2], Color.FromArgb(112, step.TintR, step.TintG, step.TintB));
        GlassVisualUpdates.SetColor(brush.GradientStops[3], Color.FromArgb(18, step.TintR, step.TintG, step.TintB));
        GlassVisualUpdates.SetColor(brush.GradientStops[4], Color.FromArgb(0, step.TintR, step.TintG, step.TintB));

        GlassVisualUpdates.SetColor(innerBrush.GradientStops[0], Color.FromArgb(255, step.BrightR, step.BrightG, step.BrightB));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[1], Color.FromArgb(150, step.TintR, step.TintG, step.TintB));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[2], Color.FromArgb(18, step.TintR, step.TintG, step.TintB));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[3], Color.FromArgb(step.InnerShadowA, 0, 0, 0));
        GlassVisualUpdates.SetColor(innerBrush.GradientStops[4], Color.FromArgb(184, step.MidR, step.MidG, step.MidB));

        GlassVisualUpdates.SetDouble(_fresnelBloom, UIElement.OpacityProperty, step.BloomOpacity);
        GlassVisualUpdates.SetDouble(_fresnel, UIElement.OpacityProperty, step.FresnelOpacity);
        GlassVisualUpdates.SetDouble(_innerFresnel, UIElement.OpacityProperty, step.InnerFresnelOpacity);
    }

    /// <summary>
    /// Applies the GPU-mode backdrop blur inside <paramref name="refractionEffect"/>
    /// and clears any legacy <see cref="BlurEffect"/> on <paramref name="host"/> so
    /// WPF renders the glass in a single shader pass without an extra render target.
    /// </summary>
    public static void ApplyGpuBlur(
        UIElement? host,
        ref BlurEffect? blur,
        double blurAmount,
        bool useGpuRefraction,
        LiquidGlassLensState? refractionEffect = null)
    {
        if (host?.Effect != null)
            host.Effect = null;
        blur = null;

        if (!useGpuRefraction || refractionEffect == null) return;

        double radius = Math.Clamp(blurAmount, 0, 1) * 14.0;
        if (radius < 0.5) radius = 0.0;
        refractionEffect.BlurRadius = radius;
    }

    /// <summary>Reads the Windows "Transparency effects" personalization toggle (defaults to enabled).</summary>
    public static bool ReadSystemTransparencySetting()
    {
        return vn_glass_binder_read_system_transparency(out byte enabled) == 0 ? enabled != 0 : true;
    }
}
