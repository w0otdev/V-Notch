using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controllers;

/// <summary>
/// GPU pixel-shader (ps_3_0) Liquid Glass refraction. Samples the raw captured desktop
/// and applies the high-quality continuous lens profile used by the CPU fallback.
/// </summary>
public sealed class LiquidGlassRefractionEffect : ShaderEffect
{
    private static readonly PixelShader _shader = LoadShader();

    /// <summary>True if the compiled shader loaded successfully (GPU path usable).</summary>
    public static bool IsAvailable { get; private set; }

    private static PixelShader LoadShader()
    {
        try
        {
            var ps = new PixelShader
            {
                UriSource = new Uri(
                    "pack://application:,,,/V-Notch;component/Shaders/LiquidGlassRefraction.ps",
                    UriKind.Absolute)
            };
            IsAvailable = true;
            return ps;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("LIQUIDGLASS", $"GPU shader load failed: {ex.Message}");
            IsAvailable = false;
            return new PixelShader();
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
        UpdateShaderValue(ZRProperty);
        UpdateShaderValue(RefractionProperty);
        UpdateShaderValue(ChromaProperty);
        UpdateShaderValue(DistortProperty);
        UpdateShaderValue(BevelModeProperty);
        UpdateShaderValue(SatFactorProperty);
        UpdateShaderValue(BrightAddProperty);
        UpdateShaderValue(TopCornerRProperty);
        UpdateShaderValue(EdgeBendProperty);
        UpdateShaderValue(PointerXProperty);
        UpdateShaderValue(PointerYProperty);
        UpdateShaderValue(PointerActiveProperty);
        UpdateShaderValue(PressAmountProperty);
        UpdateShaderValue(LightXProperty);
        UpdateShaderValue(LightYProperty);
        UpdateShaderValue(InteractionRadiusProperty);
        UpdateShaderValue(HighlightStrengthProperty);
        UpdateShaderValue(FlexStrengthProperty);
        UpdateShaderValue(PointerVelXProperty);
        UpdateShaderValue(PointerVelYProperty);
        UpdateShaderValue(ReleasePulseProperty);
        UpdateShaderValue(HoverLiftProperty);
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
    public static readonly DependencyProperty BottomCornerRProperty = Reg("BottomCornerR", 6);
    public static readonly DependencyProperty ZRProperty = Reg("ZR", 7);
    public static readonly DependencyProperty RefractionProperty = Reg("Refraction", 8);
    public static readonly DependencyProperty ChromaProperty = Reg("Chroma", 9);
    public static readonly DependencyProperty DistortProperty = Reg("Distort", 10);
    public static readonly DependencyProperty BevelModeProperty = Reg("BevelMode", 11);
    public static readonly DependencyProperty SatFactorProperty = Reg("SatFactor", 12, 1.0);
    public static readonly DependencyProperty BrightAddProperty = Reg("BrightAdd", 13);
    public static readonly DependencyProperty TopCornerRProperty = Reg("TopCornerR", 14);
    public static readonly DependencyProperty EdgeBendProperty = Reg("EdgeBend", 15, 1.0);
    public static readonly DependencyProperty PointerXProperty = Reg("PointerX", 16, 0.5);
    public static readonly DependencyProperty PointerYProperty = Reg("PointerY", 17, 0.5);
    public static readonly DependencyProperty PointerActiveProperty = Reg("PointerActive", 18, 0.0);
    public static readonly DependencyProperty PressAmountProperty = Reg("PressAmount", 19, 0.0);
    public static readonly DependencyProperty LightXProperty = Reg("LightX", 20, 0.15);
    public static readonly DependencyProperty LightYProperty = Reg("LightY", 21, -0.15);
    public static readonly DependencyProperty InteractionRadiusProperty = Reg("InteractionRadius", 22, 0.70);
    public static readonly DependencyProperty HighlightStrengthProperty = Reg("HighlightStrength", 23, 0.90);
    public static readonly DependencyProperty FlexStrengthProperty = Reg("FlexStrength", 24, 1.10);

    public static readonly DependencyProperty PointerVelXProperty = Reg("PointerVelX", 25, 0.0);
    public static readonly DependencyProperty PointerVelYProperty = Reg("PointerVelY", 26, 0.0);
    public static readonly DependencyProperty ReleasePulseProperty = Reg("ReleasePulse", 27, 0.0);
    public static readonly DependencyProperty HoverLiftProperty = Reg("HoverLift", 28, 0.0);

    public double SrcW { get => (double)GetValue(SrcWProperty); set => SetValue(SrcWProperty, value); }
    public double SrcH { get => (double)GetValue(SrcHProperty); set => SetValue(SrcHProperty, value); }
    public double NotchW { get => (double)GetValue(NotchWProperty); set => SetValue(NotchWProperty, value); }
    public double NotchH { get => (double)GetValue(NotchHProperty); set => SetValue(NotchHProperty, value); }
    public double OffX { get => (double)GetValue(OffXProperty); set => SetValue(OffXProperty, value); }
    public double OffY { get => (double)GetValue(OffYProperty); set => SetValue(OffYProperty, value); }
    public double BottomCornerR { get => (double)GetValue(BottomCornerRProperty); set => SetValue(BottomCornerRProperty, value); }
    public double ZR { get => (double)GetValue(ZRProperty); set => SetValue(ZRProperty, value); }
    public double Refraction { get => (double)GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    public double Chroma { get => (double)GetValue(ChromaProperty); set => SetValue(ChromaProperty, value); }
    public double Distort { get => (double)GetValue(DistortProperty); set => SetValue(DistortProperty, value); }
    public double BevelMode { get => (double)GetValue(BevelModeProperty); set => SetValue(BevelModeProperty, value); }
    public double SatFactor { get => (double)GetValue(SatFactorProperty); set => SetValue(SatFactorProperty, value); }
    public double BrightAdd { get => (double)GetValue(BrightAddProperty); set => SetValue(BrightAddProperty, value); }
    public double TopCornerR { get => (double)GetValue(TopCornerRProperty); set => SetValue(TopCornerRProperty, value); }
    public double EdgeBend { get => (double)GetValue(EdgeBendProperty); set => SetValue(EdgeBendProperty, value); }
    public double PointerX { get => (double)GetValue(PointerXProperty); set => SetValue(PointerXProperty, value); }
    public double PointerY { get => (double)GetValue(PointerYProperty); set => SetValue(PointerYProperty, value); }
    public double PointerActive { get => (double)GetValue(PointerActiveProperty); set => SetValue(PointerActiveProperty, value); }
    public double PressAmount { get => (double)GetValue(PressAmountProperty); set => SetValue(PressAmountProperty, value); }
    public double LightX { get => (double)GetValue(LightXProperty); set => SetValue(LightXProperty, value); }
    public double LightY { get => (double)GetValue(LightYProperty); set => SetValue(LightYProperty, value); }
    public double InteractionRadius { get => (double)GetValue(InteractionRadiusProperty); set => SetValue(InteractionRadiusProperty, value); }
    public double HighlightStrength { get => (double)GetValue(HighlightStrengthProperty); set => SetValue(HighlightStrengthProperty, value); }
    public double FlexStrength { get => (double)GetValue(FlexStrengthProperty); set => SetValue(FlexStrengthProperty, value); }
    public double PointerVelX { get => (double)GetValue(PointerVelXProperty); set => SetValue(PointerVelXProperty, value); }
    public double PointerVelY { get => (double)GetValue(PointerVelYProperty); set => SetValue(PointerVelYProperty, value); }
    public double ReleasePulse { get => (double)GetValue(ReleasePulseProperty); set => SetValue(ReleasePulseProperty, value); }
    public double HoverLift { get => (double)GetValue(HoverLiftProperty); set => SetValue(HoverLiftProperty, value); }
}
