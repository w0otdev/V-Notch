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
    private void Present(int generation)
    {
        if (!_isActive) return;
        int w = _outW, h = _outH;
        byte[] buffer = _outBuffer;
        double presentDpi = _bitmapDpi < 1.0 ? 96.0 : _bitmapDpi;
        double dpiScale = _bitmapDpi > 0 ? _bitmapDpi / 96.0 : 1.0;
        // Canvas does not honor child alignment. Position the lens origin
        // explicitly, removing CPU overscan as well as the subpixel offset.
        double txDip = -(_mapNotchOffX + _presentSubX) / dpiScale;
        double tyDip = -(_mapNotchOffY + _presentSubY) / dpiScale;

        _presentInFlight = true;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
            {
                try
                {
                    if (!_isActive || _gpuMode || generation != _renderGeneration) return;

                    UpdatePresentBitmap(w, h, buffer, presentDpi, txDip, tyDip);
                    _hasVisibleFrame = true;
                    Interlocked.Increment(ref _dbgPresentCount);
                    Interlocked.Increment(ref _totalPresentedFrames);
                }
                finally
                {
                    if (generation == _renderGeneration) _presentInFlight = false;
                }
            }));
        }
        catch (Exception)
        {
            _presentInFlight = false;
        }
    }

    private void UpdatePresentBitmap(int w, int h, byte[] buffer, double presentDpi, double txDip, double tyDip)
    {
        bool dpiChanged = _bitmap != null && Math.Abs(_bitmap.DpiX - presentDpi) > 0.5;
        long requiredPixels = (long)w * h;
        long currentPixels = _bitmap != null ? (long)_bitmap.PixelWidth * _bitmap.PixelHeight : 0;
        bool shrinkBitmap = _bitmap != null && requiredPixels > 0 && currentPixels > requiredPixels * 4;
        bool needsBitmap = _bitmap == null || dpiChanged ||
            _bitmap.PixelWidth < w || _bitmap.PixelHeight < h || shrinkBitmap;

        if (needsBitmap)
        {
            int previousW = (dpiChanged || shrinkBitmap) ? 0 : _bitmap?.PixelWidth ?? 0;
            int previousH = (dpiChanged || shrinkBitmap) ? 0 : _bitmap?.PixelHeight ?? 0;
            int capacityW = GrowPresentCapacity(previousW, w, 128);
            int capacityH = GrowPresentCapacity(previousH, h, 96);
            _bitmap = new WriteableBitmap(
                capacityW, capacityH, presentDpi, presentDpi, PixelFormats.Bgra32, null);
        }

        var bitmap = _bitmap!;
        int frameX = (bitmap.PixelWidth - w) / 2;
        bitmap.WritePixels(new Int32Rect(frameX, 0, w, h), buffer, w * 4, 0);

        // The frame is centered within pooled capacity, which can survive a
        // resize. Its storage offset must not shift the visible desktop crop.
        ConfigureHostForBitmap(bitmap, txDip - frameX * 96.0 / presentDpi, tyDip);
    }

    private void ConfigureHostForBitmap(WriteableBitmap bitmap, double txDip, double tyDip)
    {
        _host.Stretch = Stretch.None;
        _host.HorizontalAlignment = HorizontalAlignment.Left;
        _host.VerticalAlignment = VerticalAlignment.Top;
        // Restore pixel density even when a GPU attempt left explicit sizes
        // on the same Image without replacing the last CPU source.
        _host.Width = bitmap.Width;
        _host.Height = bitmap.Height;
        RenderOptions.SetBitmapScalingMode(_host, BitmapScalingMode.Linear);
        _hostTransform ??= new TranslateTransform();
        _hostTransform.X = txDip;
        _hostTransform.Y = tyDip;
        _host.RenderTransform = _hostTransform;
        if (!ReferenceEquals(_host.Source, bitmap)) _host.Source = bitmap;
    }

    internal static int GrowPresentCapacity(int current, int required, int slack)
    {
        if (required <= 0) return 0;
        if (current > checked(required * 4)) current = 0;
        if (current >= required) return current;

        int target = current > 0
            ? Math.Max(required, checked(current + slack))
            : checked(required + slack);
        const int quantum = 64;
        return checked(((target + quantum - 1) / quantum) * quantum);
    }

}
