using System;

namespace VNotch.Controllers;

public sealed class LiquidGlassLensState
{
    private double _srcW;
    private double _srcH;
    private double _notchW;
    private double _notchH;
    private double _offX;
    private double _offY;
    private double _bottomCornerR = 20.0;
    private double _topCornerR;

    private double _powerFactor = 3.0;
    private double _a = 0.7;
    private double _b = 2.3;
    private double _c = 5.2;
    private double _d = 6.9;
    private double _fPower = 1.0;
    private double _noise = 0.10;
    private double _glowWeight = 0.30;
    private double _glowBias;
    private double _glowEdge0 = 0.06;
    private double _glowEdge1;
    private double _blurRadius;

    private double _chroma = 0.35;
    private double _satFactor = 1.0;
    private double _brightAdd;

    private double _pointerX = 0.5;
    private double _pointerY = 0.5;
    private double _pointerActive;
    private double _pressAmount;
    private double _highlightStrength = 0.90;
    private double _flexStrength = 1.10;
    private double _lightX = 0.15;
    private double _lightY = -0.15;
    private double _edgeBend = 1.0;
    private double _bevelMode;

    public event EventHandler? Changed;

    private void Set(ref double field, double value)
    {
        double previous = field;
        if (previous == value) return;
        field = value;
        if (Math.Abs(previous - value) > 1e-6 || value is 0.0 or 1.0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public double SrcW { get => _srcW; set => Set(ref _srcW, value); }
    public double SrcH { get => _srcH; set => Set(ref _srcH, value); }
    public double NotchW { get => _notchW; set => Set(ref _notchW, value); }
    public double NotchH { get => _notchH; set => Set(ref _notchH, value); }
    public double OffX { get => _offX; set => Set(ref _offX, value); }
    public double OffY { get => _offY; set => Set(ref _offY, value); }
    public double BottomCornerR { get => _bottomCornerR; set => Set(ref _bottomCornerR, value); }
    public double TopCornerR { get => _topCornerR; set => Set(ref _topCornerR, value); }

    public double PowerFactor { get => _powerFactor; set => Set(ref _powerFactor, value); }
    public double A { get => _a; set => Set(ref _a, value); }
    public double B { get => _b; set => Set(ref _b, value); }
    public double C { get => _c; set => Set(ref _c, value); }
    public double D { get => _d; set => Set(ref _d, value); }
    public double FPower { get => _fPower; set => Set(ref _fPower, value); }
    public double Noise { get => _noise; set => Set(ref _noise, value); }
    public double GlowWeight { get => _glowWeight; set => Set(ref _glowWeight, value); }
    public double GlowBias { get => _glowBias; set => Set(ref _glowBias, value); }
    public double GlowEdge0 { get => _glowEdge0; set => Set(ref _glowEdge0, value); }
    public double GlowEdge1 { get => _glowEdge1; set => Set(ref _glowEdge1, value); }
    public double BlurRadius { get => _blurRadius; set => Set(ref _blurRadius, value); }

    public double Chroma { get => _chroma; set => Set(ref _chroma, value); }
    public double SatFactor { get => _satFactor; set => Set(ref _satFactor, value); }
    public double BrightAdd { get => _brightAdd; set => Set(ref _brightAdd, value); }

    public double PointerX { get => _pointerX; set => Set(ref _pointerX, value); }
    public double PointerY { get => _pointerY; set => Set(ref _pointerY, value); }
    public double PointerActive { get => _pointerActive; set => Set(ref _pointerActive, value); }
    public double PressAmount { get => _pressAmount; set => Set(ref _pressAmount, value); }
    public double HighlightStrength { get => _highlightStrength; set => Set(ref _highlightStrength, value); }
    public double FlexStrength { get => _flexStrength; set => Set(ref _flexStrength, value); }
    public double LightX { get => _lightX; set => Set(ref _lightX, value); }
    public double LightY { get => _lightY; set => Set(ref _lightY, value); }
    public double EdgeBend { get => _edgeBend; set => Set(ref _edgeBend, value); }
    public double BevelMode { get => _bevelMode; set => Set(ref _bevelMode, value); }
}
