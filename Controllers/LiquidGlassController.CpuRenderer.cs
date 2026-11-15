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
    internal static ulong ComputeSourceHash(IntPtr dibBits, int srcW, int srcH) =>
        GlassFrameFingerprint.Compute(dibBits, srcW, srcH);

    private unsafe void UpdateBackdropOptics(
        int srcW, int srcH, int sampleX, int sampleY, int sampleW, int sampleH)
    {
        if (_dibBits == IntPtr.Zero || srcW <= 0 || srcH <= 0 || sampleW <= 0 || sampleH <= 0)
            return;

        if (NativeSampleBackdropOptics(
            (byte*)_dibBits, srcW, srcH, sampleX, sampleY, sampleW, sampleH, out var optics) != 0)
            return;

        Volatile.Write(ref _averageBackgroundColorRgb,
            (optics.Red << 16) | (optics.Green << 8) | optics.Blue);
        Volatile.Write(ref _backgroundLightX1000, (int)Math.Round(optics.LightX * 1000.0));
        Volatile.Write(ref _backgroundLightY1000, (int)Math.Round(optics.LightY * 1000.0));
        Volatile.Write(ref _backgroundContrast1000, (int)Math.Round(optics.Contrast * 1000.0));
        AverageBackgroundBrightnessInt = (int)Math.Round(
            0.299 * optics.Red + 0.587 * optics.Green + 0.114 * optics.Blue);
        Volatile.Write(ref _outsideBackdropColorRgb, optics.OutsideRgb);
    }

    internal static unsafe BackdropOptics AnalyzeBackdropSamples(
        ReadOnlySpan<int> rgbSamples, int columns, int rows)
    {
        int count = columns * rows;
        if (columns <= 0 || rows <= 0 || rgbSamples.Length < count)
            return new BackdropOptics(128, 128, 128, 0, 0, 0);

        fixed (int* samplePtr = rgbSamples)
        {
            if (NativeAnalyzeBackdropSamples(samplePtr, rgbSamples.Length, columns, rows, out var optics) != 0)
                return new BackdropOptics(128, 128, 128, 0, 0, 0);
            return new BackdropOptics(
                optics.Red, optics.Green, optics.Blue, optics.LightX, optics.LightY, optics.Contrast);
        }
    }

    private bool EnsureGdiResources(int srcW, int srcH, IntPtr screenDc)
    {
        if (_memDc != IntPtr.Zero && _dibBits != IntPtr.Zero && _dibW == srcW && _dibH == srcH)
            return true;

        ReleaseGdiResources();

        if (!CreateDibSurface(screenDc, srcW, srcH, out var surface)) return false;

        _memDc = (IntPtr)surface.MemoryDc;
        _dibBmp = (IntPtr)surface.Bitmap;
        _oldBmp = (IntPtr)surface.PreviousBitmap;
        _dibBits = (IntPtr)surface.Bits;
        _dibW = srcW; _dibH = srcH;
        return true;
    }

    private void ReleaseGdiResources()
    {
        if (_memDc != IntPtr.Zero)
        {
            var surface = new VnDibSurface
            {
                MemoryDc = _memDc,
                Bitmap = _dibBmp,
                PreviousBitmap = _oldBmp,
                Bits = _dibBits,
                Width = _dibW,
                Height = _dibH,
            };
            DestroyDibSurface(ref surface);
        }
        _memDc = _dibBmp = _dibBits = _oldBmp = IntPtr.Zero;
        _dibW = _dibH = 0;
    }

    private const int ParallelPixelThreshold = 40_000;

    private static readonly ParallelOptions ParallelOpts = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8))
    };

    private unsafe void Refract(GlassParams p, byte[]? sourceOverride)
    {
        if (sourceOverride == null)
        {
            RefractFromSource((byte*)_dibBits, p);
            return;
        }

        fixed (byte* source = sourceOverride)
            RefractFromSource(source, p);
    }

    private unsafe void RefractFromSource(byte* src, GlassParams p)
    {
        int count = _outW * _outH;
        int stride = _srcW * 4;

        double sat = p.Saturation;
        double bright = p.Brightness;
        byte adjust = (Math.Abs(sat) > 1e-3 || Math.Abs(bright) > 1e-3) ? (byte)1 : (byte)0;
        float satFactor = (float)(1.0 + sat);
        float brightAdd = (float)(bright * 255.0);

        fixed (byte* dst = _outBuffer)
        fixed (int* mapPtr = _refractMap)
        {
            if (count >= ParallelPixelThreshold)
            {
                int chunks = Math.Max(1, ParallelOpts.MaxDegreeOfParallelism);
                int chunkPixels = (count + chunks - 1) / chunks;
                IntPtr srcP = (IntPtr)src, dstP = (IntPtr)dst, mapP = (IntPtr)mapPtr;
                int st = stride;

                Parallel.For(0, chunks, ParallelOpts, chunk =>
                {
                    int start = chunk * chunkPixels;
                    int end = Math.Min(count, start + chunkPixels);
                    if (start < end)
                    {
                        NativeRefractGather(
                            (byte*)srcP, (byte*)dstP, (int*)mapP, st, start, end,
                            adjust, satFactor, brightAdd);
                    }
                });
            }
            else
            {
                NativeRefractGather(
                    src, dst, mapPtr, stride, 0, count,
                    adjust, satFactor, brightAdd);
            }
        }
    }

    internal static unsafe int[] GaussianBoxRadii(double sigma, int passes = 3)
    {
        int count = Math.Max(1, passes);
        var radii = new int[count];
        fixed (int* radiiPtr = radii)
        {
            NativeGaussianBoxRadii(sigma, count, radiiPtr, count, out _);
        }
        return radii;
    }

    private unsafe byte[] BlurCapturedSource(int w, int h, int[] passRadii)
    {
        int pixels = checked(w * h);
        int bytes = checked(pixels * 4);
        if (_sourceBlurBuffer.Length < bytes)
            _sourceBlurBuffer = new byte[checked(GrowPixelBufferCapacity(_sourceBlurBuffer.Length / 4, pixels) * 4)];
        if (_sourceBlurTmp.Length < bytes)
            _sourceBlurTmp = new byte[_sourceBlurBuffer.Length];

        byte[] a = _sourceBlurBuffer, b = _sourceBlurTmp;
        int maximumRadius = Math.Max(1, Math.Min(w, h) / 2);
        const int rowStripe = 16;
        const int colStripe = 64;
        int rowStripes = (h + rowStripe - 1) / rowStripe;
        int colStripes = (w + colStripe - 1) / colStripe;
        bool blurred = false;

        for (int pass = 0; pass < passRadii.Length; pass++)
        {
            int r = Math.Clamp(passRadii[pass], 0, maximumRadius);
            if (r < 1) continue;

            if (!blurred)
            {
                IntPtr srcBits = _dibBits;
                if (h >= 64)
                    Parallel.For(0, rowStripes, ParallelOpts, stripe => GlassBoxBlur.Horizontal(
                        srcBits, b, w, r, stripe * rowStripe, Math.Min(h, (stripe + 1) * rowStripe)));
                else
                    GlassBoxBlur.Horizontal(srcBits, b, w, r, 0, h);
                blurred = true;
            }
            else
            {
                if (h >= 64)
                    Parallel.For(0, rowStripes, ParallelOpts, stripe => GlassBoxBlur.Horizontal(
                        a, b, w, r, stripe * rowStripe, Math.Min(h, (stripe + 1) * rowStripe)));
                else
                    GlassBoxBlur.Horizontal(a, b, w, r, 0, h);
            }

            if (w >= colStripe)
                Parallel.For(0, colStripes, ParallelOpts, stripe => GlassBoxBlur.Vertical(
                    b, a, w, h, r, stripe * colStripe, Math.Min(w, (stripe + 1) * colStripe)));
            else
                GlassBoxBlur.Vertical(b, a, w, h, r, 0, w);
        }

        if (!blurred)
        {
            fixed (byte* dst = a)
            {
                Buffer.MemoryCopy((void*)_dibBits, dst, a.Length, bytes);
            }
        }

        return a;
    }

    internal readonly record struct MapDimensions(
        int OutW, int OutH,
        int SrcW, int SrcH,
        int Margin,
        int NotchW, int NotchH,
        int NotchOffX, int NotchOffY,
        int CaptureShiftX, int CaptureShiftY);

    private const int RefractionLutSize = 512;
    private readonly double[] _refractLut = new double[RefractionLutSize];

    private unsafe bool EnsureMaps(GlassParams p, MapDimensions d)
    {
        p.TopCornerRadius = Math.Round(p.TopCornerRadius * 2.0) / 2.0;
        p.BottomCornerRadius = Math.Round(p.BottomCornerRadius * 2.0) / 2.0;

        if (IsMapCacheValid(p, d))
            return false;

        UpdateMapCacheState(p, d);
        EnsureMapBuffers(d.OutW, d.OutH);

        var nativeParams = new VnGlassMapParams
        {
            OutW = d.OutW,
            OutH = d.OutH,
            SrcW = d.SrcW,
            SrcH = d.SrcH,
            Margin = d.Margin,
            NotchW = d.NotchW,
            NotchH = d.NotchH,
            NotchOffX = d.NotchOffX,
            NotchOffY = d.NotchOffY,
            CaptureShiftX = d.CaptureShiftX,
            CaptureShiftY = d.CaptureShiftY,
            OutScale = _outScale,
            TopCornerRadius = p.TopCornerRadius,
            BottomCornerRadius = p.BottomCornerRadius,
            EdgeBend = p.EdgeBend,
            ChromaticAberration = p.ChromaticAberration,
            Distortion = p.Distortion,
            FPower = p.FPower,
            RefractionA = p.RefractionA,
            RefractionB = p.RefractionB,
            RefractionC = p.RefractionC,
            RefractionD = p.RefractionD,
            Refraction = p.Refraction,
            ZRadius = p.ZRadius,
            BevelMode = p.BevelMode
        };

        fixed (double* lutPtr = _refractLut)
        fixed (int* mapPtr = _refractMap)
        {
            NativeBuildRefractionLut(
                p.FPower, p.RefractionA, p.RefractionB, p.RefractionC, p.RefractionD,
                lutPtr, RefractionLutSize);

            if (d.OutH >= 64)
            {
                const int stripeRows = 16;
                int stripes = (d.OutH + stripeRows - 1) / stripeRows;
                IntPtr lutAddr = (IntPtr)lutPtr;
                IntPtr mapAddr = (IntPtr)mapPtr;
                int outH = d.OutH;
                Parallel.For(0, stripes, ParallelOpts, stripe =>
                {
                    int y0 = stripe * stripeRows;
                    int y1 = Math.Min(outH, y0 + stripeRows);
                    NativeBuildRefractionRows(
                        in nativeParams, (double*)lutAddr, RefractionLutSize, y0, y1, (int*)mapAddr);
                });
            }
            else
            {
                NativeBuildRefractionRows(
                    in nativeParams, lutPtr, RefractionLutSize, 0, d.OutH, mapPtr);
            }
        }

        return true;
    }

    private bool IsMapCacheValid(GlassParams p, MapDimensions d)
    {
        return !_mapsDirty
            && _outW == d.OutW && _outH == d.OutH
            && _srcW == d.SrcW && _srcH == d.SrcH
            && _margin == d.Margin
            && _refractMap.Length >= d.OutW * d.OutH * 6
            && _mapNotchW == d.NotchW && _mapNotchH == d.NotchH
            && _mapNotchOffX == d.NotchOffX && _mapNotchOffY == d.NotchOffY
            && _mapCaptureShiftX == d.CaptureShiftX && _mapCaptureShiftY == d.CaptureShiftY
            && Math.Abs(_mapTopCornerRadius - p.TopCornerRadius) < 1e-3
            && Math.Abs(_mapBottomCornerRadius - p.BottomCornerRadius) < 1e-3;
    }

    private void UpdateMapCacheState(GlassParams p, MapDimensions d)
    {
        _mapsDirty = false;
        _outW = d.OutW; _outH = d.OutH; _srcW = d.SrcW; _srcH = d.SrcH; _margin = d.Margin;
        _mapNotchW = d.NotchW; _mapNotchH = d.NotchH; _mapNotchOffX = d.NotchOffX; _mapNotchOffY = d.NotchOffY;
        _mapCaptureShiftX = d.CaptureShiftX; _mapCaptureShiftY = d.CaptureShiftY;
        _mapTopCornerRadius = p.TopCornerRadius;
        _mapBottomCornerRadius = p.BottomCornerRadius;
    }

    private void EnsureMapBuffers(int outW, int outH)
    {
        int n = checked(outW * outH);
        int needed = checked(n * 4);
        if (_outBuffer.Length < needed)
        {
            _outBuffer = new byte[checked(GrowPixelBufferCapacity(_outBuffer.Length / 4, n) * 4)];
        }
        int mapInts = checked(n * 6);
        if (_refractMap.Length < mapInts)
        {
            int capacity = checked(GrowPixelBufferCapacity(_refractMap.Length / 6, n) * 6);
            _refractMap = new int[capacity];
        }
    }

    internal static int GrowPixelBufferCapacity(int current, int required)
    {
        // Retain capacity across collapse/expand cycles to prevent LOH thrashing.
        if (current >= required) return current;
        // Amortize LOH allocations through expansion. Bound unused capacity to
        // 64K pixels per buffer instead of retaining another full-sized frame.
        int slack = Math.Clamp(current / 4, 4096, 65536);
        return Math.Max(required, checked(current + slack));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Smoother01(double x)
    {
        double t = Math.Clamp(x, 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double ExponentialRefract(double x, double a, double b, double c, double d)
    {
        double exponent = -d * x - a;
        double baseVal = Math.Max(c * 2.718281828459045, 0.0001);
        return 1.0 - b * Math.Pow(baseVal, exponent);
    }
}
