using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controllers;


internal sealed class LiquidGlassSurfaceBinder
{
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

        double edge = Math.Clamp(edgeHighlight, 0, 1);
        double spec = Math.Clamp(specular, 0, 1);
        double fres = Math.Clamp(fresnel, 0, 1);
        double spectral = Math.Clamp(chroma, 0, 2);
        _activeFresnelLevel = fres;

        // A square-root response preserves a delicate rim at low slider values
        GlassVisualUpdates.SetDouble(_rim, UIElement.OpacityProperty, Math.Sqrt(edge) * 0.82);
        GlassVisualUpdates.SetDouble(_depthRim, UIElement.OpacityProperty, Math.Clamp(edge * 0.34 + fres * 0.24, 0, 0.46));
        double fresnelEnergy = Math.Sqrt(fres);
        GlassVisualUpdates.SetDouble(_fresnelBloom, UIElement.OpacityProperty, fresnelEnergy * 0.30);
        GlassVisualUpdates.SetDouble(_fresnel, UIElement.OpacityProperty, fresnelEnergy * 0.94);
        GlassVisualUpdates.SetDouble(_innerFresnel, UIElement.OpacityProperty, fresnelEnergy * 0.64);
        GlassVisualUpdates.SetDouble(_specular, UIElement.OpacityProperty, spec * 0.92);

        double spectralOpacity = Math.Clamp(spectral * 0.30 + edge * 0.10, 0, 0.52);
        GlassVisualUpdates.SetDouble(_coolRim, UIElement.OpacityProperty, spectralOpacity);
        GlassVisualUpdates.SetDouble(_warmRim, UIElement.OpacityProperty, spectralOpacity * 0.76);
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
        GlassVisualUpdates.SetDouble(_fresnelBloom, UIElement.OpacityProperty, Math.Clamp(
            fresnelEnergy * (0.24 + _dynamicFresnelContrast * 0.08), 0.0, 0.42));
        GlassVisualUpdates.SetDouble(_fresnel, UIElement.OpacityProperty, Math.Clamp(
            fresnelEnergy * 0.98 * contrastResponse, 0.0, 1.0));
        GlassVisualUpdates.SetDouble(_innerFresnel, UIElement.OpacityProperty, Math.Clamp(
            fresnelEnergy * (0.58 + _dynamicFresnelContrast * 0.10), 0.0, 0.76));
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
        LiquidGlassRefractionEffect? refractionEffect = null)
    {
        if (host?.Effect != null)
            host.Effect = null;
        blur = null;

        if (!useGpuRefraction || refractionEffect == null) return;

        double radius = Math.Clamp(blurAmount, 0, 1) * 14.0;
        if (radius < 0.5) radius = 0.0;
        GlassVisualUpdates.SetDouble(refractionEffect, LiquidGlassRefractionEffect.BlurRadiusProperty, radius);
    }

    public static Color BuildContentFresnelTint(byte red, byte green, byte blue)
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

    public static Color InterpolateColor(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    /// <summary>Reads the Windows "Transparency effects" personalization toggle (defaults to enabled).</summary>
    public static bool ReadSystemTransparencySetting()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var val = key.GetValue("EnableTransparency");
                if (val is int i) return i == 1;
            }
        }
        catch { /* ignored */ }
        return true;
    }
}
