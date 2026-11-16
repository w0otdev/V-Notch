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
        bool invisibleMode = WindowAnimationPolicy.InvisibleMode;
        if (gpuMode && invisibleMode)
        {
            var gpuDims = ComputeFrameDimensions(region, p, true, false, displayW, displayH);
            _outScale = gpuDims.OutScale;
            return ProcessDesktopGpuFrame(p, region, gpuDims, generation, blurSigma, refreshVersion);
        }
        if (gpuMode && !invisibleMode)
        {
            var gpuDims = ComputeFrameDimensions(region, p, true, false, displayW, displayH);
            _outScale = gpuDims.OutScale;
            if (ProcessDesktopGpuFrame(p, region, gpuDims, generation, blurSigma, refreshVersion, exactWindows: true))
                return false;
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
        // A GPU-native lens renders the active crop; pooled capacity handles growth.
        // Full-surface capture remains for the legacy presentation path.
        bool fullSurface = gpuMode && CaptureFullSurface && !UsesNativeRefraction;
        double scale = 1.0;
        int outW = Math.Max(8, (int)Math.Round(displayW * scale));
        int outH = Math.Max(8, (int)Math.Round(displayH * scale));
        double outScale = displayW > 0 ? (double)outW / displayW * (_bitmapDpi / 96.0) : 1.0;

        int overscan = gpuMode
            ? 0
            : Math.Clamp((int)Math.Round(80 * (_bitmapDpi / 96.0)), 64, 150);
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

    internal Func<int, int, int, int, IntPtr, bool> CompositeBackdropCapture { get; set; } =
        MagnifierCaptureSource.TryCompositeDesktop;
    private bool _usingCompositeBackdrop;

    private bool BitBltCapture(BackdropCaptureParams cp, out bool isSyntheticBackdrop)
    {
        isSyntheticBackdrop = false;
        if (!WindowAnimationPolicy.InvisibleMode)
            _exactBitBltCapture = false;
        if (!_exactBitBltCapture && !_magReady && WindowAnimationPolicy.InvisibleMode)
        {
            _exactBitBltCapture = SetWindowDisplayAffinitySafe(WDA_EXCLUDEFROMCAPTURE);
        }

        // Without capture exclusion, BitBlt would sample the notch itself. Rebuild
        // the same screen area from the wallpaper and the windows underneath it.
        if (!_exactBitBltCapture)
        {
            if (CompositeBackdropCapture(cp.SrcX, cp.SrcY, cp.SrcW, cp.SrcH, _dibBits))
            {
                if (!_usingCompositeBackdrop)
                {
                    _usingCompositeBackdrop = true;
                    RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier unavailable; using wallpaper/window backdrop capture.");
                }
                return true;
            }
            // A transient miss must not upload a synthetic grey/black frame over
            // an initialized presentation surface. Keep that surface until the
            // next complete capture; the plain material is only for cold start.
            if (Volatile.Read(ref _hasVisibleFrame)) return false;
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

}
