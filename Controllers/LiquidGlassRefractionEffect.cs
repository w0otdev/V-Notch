using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controllers;

/// <summary>
/// GPU pixel-shader (ps_3_0) Liquid Glass refraction based on OverShifted/LiquidGlass.
/// Samples the desktop backdrop and applies squircle SDF, exponential refraction,
/// chromatic dispersion, film grain noise, and directional specular glow.
/// </summary>
public sealed class LiquidGlassRefractionEffect : ShaderEffect
{
    private static readonly PixelShader _shader = LoadShader();

    /// <summary>True if the compiled shader loaded successfully (GPU path usable).</summary>
    public static bool IsAvailable { get; private set; }

    private static PixelShader LoadShader()
    {
        var ps = new PixelShader();
        try
        {
            // 1. Try loading directly from file on disk next to executable or project root
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidatePaths =
            {
                Path.Combine(baseDir, "Shaders", "LiquidGlassRefraction.ps"),
                Path.Combine(baseDir, "LiquidGlassRefraction.ps"),
                Path.Combine(baseDir, "..", "..", "..", "Shaders", "LiquidGlassRefraction.ps")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    ps.SetStreamSource(stream);
                    IsAvailable = true;
                    RuntimeLog.Log("LIQUIDGLASS", $"GPU shader loaded from file: {path}");
                    return ps;
                }
            }

            // 2. Try loading from application resource stream
            var resourceUri = new Uri("pack://application:,,,/V-Notch;component/Shaders/LiquidGlassRefraction.ps", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(resourceUri);
            if (streamInfo != null)
            {
                using (streamInfo.Stream)
                {
                    ps.SetStreamSource(streamInfo.Stream);
                    IsAvailable = true;
                    RuntimeLog.Log("LIQUIDGLASS", "GPU shader loaded from Application Resource Stream.");
                    return ps;
                }
            }

            // 3. Fallback to UriSource
            ps.UriSource = resourceUri;
            IsAvailable = true;
            return ps;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("LIQUIDGLASS", $"GPU shader load failed: {ex.Message}");
            IsAvailable = false;
            return ps;
        }
    }

    public LiquidGlassRefractionEffect()
    {
        PixelShader = _shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SrcWProperty);
        UpdateShaderValue(SrcHProperty);
        UpdateShaderValue(NotchWProperty);
        UpdateShaderValue(NotchHProperty);
        UpdateShaderValue(OffXProperty);
        UpdateShaderValue(OffYProperty);
        UpdateShaderValue(BottomCornerRProperty);
        UpdateShaderValue(TopCornerRProperty);
        UpdateShaderValue(PowerFactorProperty);
        UpdateShaderValue(AProperty);
        UpdateShaderValue(BProperty);
        UpdateShaderValue(CProperty);
        UpdateShaderValue(DProperty);
        UpdateShaderValue(FPowerProperty);
        UpdateShaderValue(NoiseProperty);
        UpdateShaderValue(GlowWeightProperty);
        UpdateShaderValue(GlowBiasProperty);
        UpdateShaderValue(GlowEdge0Property);
        UpdateShaderValue(GlowEdge1Property);
        UpdateShaderValue(ChromaProperty);
        UpdateShaderValue(SatFactorProperty);
        UpdateShaderValue(BrightAddProperty);
        UpdateShaderValue(PointerXProperty);
        UpdateShaderValue(PointerYProperty);
        UpdateShaderValue(PointerActiveProperty);
        UpdateShaderValue(PressAmountProperty);
        UpdateShaderValue(HighlightStrengthProperty);
        UpdateShaderValue(FlexStrengthProperty);
        UpdateShaderValue(LightXProperty);
        UpdateShaderValue(LightYProperty);
        UpdateShaderValue(EdgeBendProperty);
        UpdateShaderValue(BevelModeProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassRefractionEffect), 0);

    private static DependencyProperty Reg(string name, int register, double def = 0.0) =>
        DependencyProperty.Register(name, typeof(double), typeof(LiquidGlassRefractionEffect),
            new UIPropertyMetadata(def, PixelShaderConstantCallback(register)));

    public static readonly DependencyProperty SrcWProperty = Reg("SrcW", 0);
    public static readonly DependencyProperty SrcHProperty = Reg("SrcH", 1);
    public static readonly DependencyProperty NotchWProperty = Reg("NotchW", 2);
    public static readonly DependencyProperty NotchHProperty = Reg("NotchH", 3);
    public static readonly DependencyProperty OffXProperty = Reg("OffX", 4);
    public static readonly DependencyProperty OffYProperty = Reg("OffY", 5);
    public static readonly DependencyProperty BottomCornerRProperty = Reg("BottomCornerR", 6, 20.0);
    public static readonly DependencyProperty TopCornerRProperty = Reg("TopCornerR", 7, 0.0);

    // OverShifted LiquidGlass Core
    public static readonly DependencyProperty PowerFactorProperty = Reg("PowerFactor", 8, 3.0);
    public static readonly DependencyProperty AProperty = Reg("A", 9, 0.7);
    public static readonly DependencyProperty BProperty = Reg("B", 10, 2.3);
    public static readonly DependencyProperty CProperty = Reg("C", 11, 5.2);
    public static readonly DependencyProperty DProperty = Reg("D", 12, 6.9);
    public static readonly DependencyProperty FPowerProperty = Reg("FPower", 13, 1.0);
    public static readonly DependencyProperty NoiseProperty = Reg("Noise", 14, 0.10);
    public static readonly DependencyProperty GlowWeightProperty = Reg("GlowWeight", 15, 0.30);
    public static readonly DependencyProperty GlowBiasProperty = Reg("GlowBias", 16, 0.0);
    public static readonly DependencyProperty GlowEdge0Property = Reg("GlowEdge0", 17, 0.06);
    public static readonly DependencyProperty GlowEdge1Property = Reg("GlowEdge1", 18, 0.0);

    public static readonly DependencyProperty ChromaProperty = Reg("Chroma", 19, 0.35);
    public static readonly DependencyProperty SatFactorProperty = Reg("SatFactor", 20, 1.0);
    public static readonly DependencyProperty BrightAddProperty = Reg("BrightAdd", 21, 0.0);

    public static readonly DependencyProperty PointerXProperty = Reg("PointerX", 22, 0.5);
    public static readonly DependencyProperty PointerYProperty = Reg("PointerY", 23, 0.5);
    public static readonly DependencyProperty PointerActiveProperty = Reg("PointerActive", 24, 0.0);
    public static readonly DependencyProperty PressAmountProperty = Reg("PressAmount", 25, 0.0);
    public static readonly DependencyProperty HighlightStrengthProperty = Reg("HighlightStrength", 26, 0.90);
    public static readonly DependencyProperty FlexStrengthProperty = Reg("FlexStrength", 27, 1.10);
    public static readonly DependencyProperty LightXProperty = Reg("LightX", 28, 0.15);
    public static readonly DependencyProperty LightYProperty = Reg("LightY", 29, -0.15);
    public static readonly DependencyProperty EdgeBendProperty = Reg("EdgeBend", 30, 1.0);
    public static readonly DependencyProperty BevelModeProperty = Reg("BevelMode", 31, 0.0);

    public double SrcW { get => (double)GetValue(SrcWProperty); set => SetValue(SrcWProperty, value); }
    public double SrcH { get => (double)GetValue(SrcHProperty); set => SetValue(SrcHProperty, value); }
    public double NotchW { get => (double)GetValue(NotchWProperty); set => SetValue(NotchWProperty, value); }
    public double NotchH { get => (double)GetValue(NotchHProperty); set => SetValue(NotchHProperty, value); }
    public double OffX { get => (double)GetValue(OffXProperty); set => SetValue(OffXProperty, value); }
    public double OffY { get => (double)GetValue(OffYProperty); set => SetValue(OffYProperty, value); }
    public double BottomCornerR { get => (double)GetValue(BottomCornerRProperty); set => SetValue(BottomCornerRProperty, value); }
    public double TopCornerR { get => (double)GetValue(TopCornerRProperty); set => SetValue(TopCornerRProperty, value); }

    public double PowerFactor { get => (double)GetValue(PowerFactorProperty); set => SetValue(PowerFactorProperty, value); }
    public double A { get => (double)GetValue(AProperty); set => SetValue(AProperty, value); }
    public double B { get => (double)GetValue(BProperty); set => SetValue(BProperty, value); }
    public double C { get => (double)GetValue(CProperty); set => SetValue(CProperty, value); }
    public double D { get => (double)GetValue(DProperty); set => SetValue(DProperty, value); }
    public double FPower { get => (double)GetValue(FPowerProperty); set => SetValue(FPowerProperty, value); }
    public double Noise { get => (double)GetValue(NoiseProperty); set => SetValue(NoiseProperty, value); }
    public double GlowWeight { get => (double)GetValue(GlowWeightProperty); set => SetValue(GlowWeightProperty, value); }
    public double GlowBias { get => (double)GetValue(GlowBiasProperty); set => SetValue(GlowBiasProperty, value); }
    public double GlowEdge0 { get => (double)GetValue(GlowEdge0Property); set => SetValue(GlowEdge0Property, value); }
    public double GlowEdge1 { get => (double)GetValue(GlowEdge1Property); set => SetValue(GlowEdge1Property, value); }

    public double Chroma { get => (double)GetValue(ChromaProperty); set => SetValue(ChromaProperty, value); }
    public double SatFactor { get => (double)GetValue(SatFactorProperty); set => SetValue(SatFactorProperty, value); }
    public double BrightAdd { get => (double)GetValue(BrightAddProperty); set => SetValue(BrightAddProperty, value); }

    public double PointerX { get => (double)GetValue(PointerXProperty); set => SetValue(PointerXProperty, value); }
    public double PointerY { get => (double)GetValue(PointerYProperty); set => SetValue(PointerYProperty, value); }
    public double PointerActive { get => (double)GetValue(PointerActiveProperty); set => SetValue(PointerActiveProperty, value); }
    public double PressAmount { get => (double)GetValue(PressAmountProperty); set => SetValue(PressAmountProperty, value); }
    public double HighlightStrength { get => (double)GetValue(HighlightStrengthProperty); set => SetValue(HighlightStrengthProperty, value); }
    public double FlexStrength { get => (double)GetValue(FlexStrengthProperty); set => SetValue(FlexStrengthProperty, value); }
    public double LightX { get => (double)GetValue(LightXProperty); set => SetValue(LightXProperty, value); }
    public double LightY { get => (double)GetValue(LightYProperty); set => SetValue(LightYProperty, value); }
    public double EdgeBend { get => (double)GetValue(EdgeBendProperty); set => SetValue(EdgeBendProperty, value); }
    public double BevelMode { get => (double)GetValue(BevelModeProperty); set => SetValue(BevelModeProperty, value); }
}
