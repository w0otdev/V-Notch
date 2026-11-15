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

}
