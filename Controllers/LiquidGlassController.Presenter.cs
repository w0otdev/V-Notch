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
    public bool SetGpuMode(bool enabled, Action<GpuGeometry>? onGeometry, Action<Exception>? onFailure = null, bool ultimateGpu = false)
    {
        _onGpuGeometry = onGeometry;
        _onGpuFailure = onFailure;
        bool adapterModeChanged = _ultimateGpuMode != ultimateGpu;
        _ultimateGpuMode = ultimateGpu;
        double nextInterval = ComputeCaptureIntervalMs(Volatile.Read(ref _configuredFps), Volatile.Read(ref _displayRefreshHz), ultimateGpu);
        Volatile.Write(ref _activeIntervalMs, nextInterval);
        if (!enabled)
        {
            UsesNativeRefraction = false;
            ReleaseGpuLens();
            _gpuMode = false;
            Interlocked.Exchange(ref _gpuFailureSignaled, 0);
            ResetGpuGeometryTracking();
            DisposeGpuPresenter();
            _mapsDirty = true;
            return true;
        }

        if (adapterModeChanged && _d3dPresenter != null)
            DisposeGpuPresenter();

        Interlocked.Exchange(ref _gpuFailureSignaled, 0);
        ResetGpuGeometryTracking();
        if (!TryEnableGpuPresenter(out Exception? error))
        {
            _gpuMode = false;
            _mapsDirty = true;
            RuntimeLog.Log(LogCategory, $"D3DImage presenter unavailable; using CPU fallback: {error?.Message}");
            return false;
        }

        _d3dPresenter?.SetFrameIntervalMs(nextInterval);
        _gpuMode = true;
        _mapsDirty = true;
        return true;
    }

    private bool TryEnableGpuPresenter(out Exception? error)
    {
        try
        {
            if (_dispatcher.CheckAccess())
                EnsureGpuPresenterOnDispatcher();
            else
                _dispatcher.Invoke(EnsureGpuPresenterOnDispatcher, DispatcherPriority.Send, CancellationToken.None);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            DisposeGpuPresenter();
            return false;
        }
    }

    private void EnsureGpuPresenterOnDispatcher()
    {
        if (_d3dPresenter == null)
        {
            IntPtr hwnd = _getHwnd();
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("A window handle is required for the D3DImage presenter.");

            // Use one fixed presentation surface for the full supported region
            _d3dPresenter = new D3DImageFramePresenter(
                _dispatcher,
                hwnd,
                _maxRegionW + GpuSamplingMarginLimit * 2,
                _maxRegionH + GpuSamplingMarginLimit * 2,
                ultimateGpuMode: _ultimateGpuMode,
                useGpuCapture: true);
            _d3dPresenter.SetFrameIntervalMs(Volatile.Read(ref _activeIntervalMs));
            _d3dPresenter.FramePresented += OnD3DFramePresented;
            _d3dPresenter.RefreshRequested += ForceRefresh;
            _d3dPresenter.Failed += OnD3DPresenterFailed;
            if (_isActive) _d3dPresenter.BeginSession(Volatile.Read(ref _renderGeneration));
        }

        _host.Stretch = Stretch.Fill;
        _host.HorizontalAlignment = HorizontalAlignment.Left;
        _host.VerticalAlignment = VerticalAlignment.Top;
        double dpi = _bitmapDpi > 0 ? _bitmapDpi / 96.0 : 1.0;
        _host.Width = SurfaceWidth / dpi;
        _host.Height = SurfaceHeight / dpi;
        _host.RenderTransform = null;
        RenderOptions.SetBitmapScalingMode(_host, BitmapScalingMode.HighQuality);
        // Do not expose the D3DImage until its first captured frame has been
    }

    private void DisposeGpuPresenter()
    {
        if (_d3dPresenter == null)
            return;

        try
        {
            if (_dispatcher.CheckAccess())
                DisposeGpuPresenterOnDispatcher();
            else
                _dispatcher.Invoke(DisposeGpuPresenterOnDispatcher, DispatcherPriority.Send, CancellationToken.None);
        }
        catch
        {
            _d3dPresenter = null;
        }
    }

    private void DisposeGpuPresenterOnDispatcher()
    {
        var presenter = _d3dPresenter;
        if (presenter == null)
            return;

        _d3dPresenter = null;
        presenter.FramePresented -= OnD3DFramePresented;
        presenter.RefreshRequested -= ForceRefresh;
        presenter.Failed -= OnD3DPresenterFailed;
        if (ReferenceEquals(_host.Source, presenter.ImageSource) || ReferenceEquals(_host.Source, _gpuCaptureImage?.Image))
            _host.Source = null;
        _gpuCaptureImage = null;
        presenter.Dispose();
        ResetGpuGeometryTracking();
    }

    private void ResetGpuGeometryTracking()
    {
        lock (_sync)
        {
            _lastPresentedGpuGeometry = default;
            _hasPresentedGpuGeometry = false;
        }
        _hasUploadedGpuFrame = false;
        _hasLastSubmittedGpuFrame = false;
        _hasPresentedCpuFrame = false;
        _hasVisibleFrame = false;
        Interlocked.Increment(ref _refreshVersion);
    }

    private void OnD3DFramePresented(GpuGeometry geom)
    {
        if (!_isActive) return;
        _hasVisibleFrame = true;
        Interlocked.Increment(ref _dbgPresentCount);
        Interlocked.Increment(ref _totalPresentedFrames);

        bool geometryChanged;
        lock (_sync)
        {
            geometryChanged = !_hasPresentedGpuGeometry || !_lastPresentedGpuGeometry.Equals(geom);
            if (geometryChanged)
            {
                _lastPresentedGpuGeometry = geom;
                _hasPresentedGpuGeometry = true;
            }
        }
        if (geometryChanged)
            _onGpuGeometry?.Invoke(geom);

        // Geometry is applied before the initialized source becomes visible so
        var presenter = _d3dPresenter;
        if (_gpuMode && presenter != null)
        {
            ImageSource source = presenter.ImageSource;
            if (CropGpuPresentationToCapture)
            {
                if (_gpuCaptureImage == null || !ReferenceEquals(_gpuCaptureImage.Source, source))
                    _gpuCaptureImage = new GlassCaptureImage(source, SurfaceWidth, SurfaceHeight);
                _gpuCaptureImage.SetCaptureSize((int)geom.SrcW, (int)geom.SrcH);
                source = _gpuCaptureImage.Image;
            }
            if (!ReferenceEquals(_host.Source, source)) _host.Source = source;
        }
        if (_waitingForGpuPresentation) SignalWakeEventSafe();
    }

    public ImageSource? ImageSource => _d3dPresenter?.ImageSource;

    // Morphing windows can reveal pixels beyond the previous frame's small
    // bounds before capture catches up. Fill a stable envelope for those hosts.
    private volatile bool _captureFullSurface;
    public bool CaptureFullSurface
    {
        get => _captureFullSurface;
        set
        {
            if (_captureFullSurface == value) return;
            _captureFullSurface = value;
            Interlocked.Increment(ref _refreshVersion);
        }
    }

    // Keep the desktop texture stationary while the lens moves inside it.
    // Recentring every capture makes texture uploads and HWND movement race.
    private (int X, int Y, int Width, int Height)? _fullSurfaceCaptureBounds;

    public int MaxRegionWidth => _maxRegionW;
    public int MaxRegionHeight => _maxRegionH;
    public int SurfaceWidth => _maxRegionW + GpuSamplingMarginLimit * 2;
    public int SurfaceHeight => _maxRegionH + GpuSamplingMarginLimit * 2;

    private void OnD3DPresenterFailed(Exception ex)
    {
        if (Interlocked.Exchange(ref _gpuFailureSignaled, 1) != 0)
            return;

        _gpuMode = false;
        _mapsDirty = true;
        int failureGeneration = Volatile.Read(ref _renderGeneration);

        void NotifyFailure()
        {
            if (failureGeneration != Volatile.Read(ref _renderGeneration)) return;
            try { _onGpuFailure?.Invoke(ex); }
            catch { /* fallback must not take down the UI thread */ }
            if (_isActive && !_exactBitBltCapture && _mag == null)
            {
                try
                {
                    AcquireMagnifier();
                }
                catch (Exception magEx)
                {
                    RuntimeLog.Log(LogCategory, $"[{_logTag}] Magnifier init failed: {magEx.Message}");
                    _magReady = false;
                }
            }
            if (_onGpuFailure == null)
                DisposeGpuPresenter();
        }

        try
        {
            if (_dispatcher.CheckAccess())
                NotifyFailure();
            else
                _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)NotifyFailure);
        }
        catch
        {
            DisposeGpuPresenter();
        }
    }

    private int _dbgFrameCount;
    private int _dbgPresentCount;
    private double _dbgLastLogMs;

}
