using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vortice.Direct3D9;
using Vortice.Mathematics;

namespace VNotch.Controllers;

/// <summary>
/// Presents CPU-captured BGRA frames through a Direct3D 9 render-target surface.
/// The worker copies into a system-memory upload surface; a render-priority UI
/// callback uploads and scales the newest frame into one stable presentation
/// surface. Keeping that D3DImage back buffer at a fixed size is important: notch
/// width/height animate every compositor frame, and repeatedly replacing the back
/// buffer during that animation can make DWM briefly composite a black surface.
/// </summary>
internal sealed class D3DImageFramePresenter : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _surfaceSync = new();
    private readonly D3DImage _image = new();

    private IDirect3D9Ex? _direct3D;
    private IDirect3DDevice9Ex? _device;
    private IDirect3DSurface9? _uploadSurface;
    private IDirect3DSurface9? _renderSurface;
    // The surface currently owned by D3DImage can differ from _renderSurface
    // while a resized frame is being uploaded. Keeping them separate lets WPF
    // continue drawing the last complete frame until its replacement is ready.
    private IDirect3DSurface9? _attachedSurface;
    private readonly int _surfaceWidth;
    private readonly int _surfaceHeight;
    private int _frameWidth;
    private int _frameHeight;
    // Captured frames occupy only the top-left corner of the fixed presentation
    // surface. Dirtying the whole surface made WPF's render thread copy the full
    // envelope every present, which saturates it once two glass windows (notch +
    // settings) present at once. Track the largest recently presented frame so a
    // shrinking frame still refreshes the area the previous frame covered.
    private int _lastDirtyWidth;
    private int _lastDirtyHeight;
    private bool _pendingFrame;
    private bool _presentQueued;
    private bool _disposed;
    private bool _failed;

    public D3DImageFramePresenter(
        Dispatcher dispatcher,
        IntPtr windowHandle,
        int surfaceWidth,
        int surfaceHeight)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException("The D3DImage presenter must be created on its UI dispatcher.");
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        if (surfaceWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceWidth));
        if (surfaceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceHeight));

        _surfaceWidth = surfaceWidth;
        _surfaceHeight = surfaceHeight;

        _direct3D = D3D9.Direct3DCreate9Ex();
        var present = new PresentParameters
        {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = Format.Unknown,
            BackBufferCount = 1,
            MultiSampleType = MultisampleType.None,
            SwapEffect = SwapEffect.Discard,
            DeviceWindowHandle = windowHandle,
            Windowed = true,
            EnableAutoDepthStencil = false,
            PresentationInterval = PresentInterval.Immediate
        };

        _device = _direct3D.CreateDeviceEx(
            0,
            DeviceType.Hardware,
            windowHandle,
            CreateFlags.Multithreaded |
            CreateFlags.FpuPreserve |
            CreateFlags.HardwareVertexProcessing,
            present);

        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
    }

    public ImageSource ImageSource => _image;

    public event Action? FramePresented;
    public event Action<Exception>? Failed;

    public bool UploadFrame(IntPtr source, int width, int height, int sourceStride)
    {
        if (_disposed || _failed || source == IntPtr.Zero || width <= 0 || height <= 0)
            return false;
        if (width > _surfaceWidth || height > _surfaceHeight)
            return false;

        int rowBytes = checked(width * 4);
        if (sourceStride < rowBytes)
            return false;

        try
        {
            EnsureResources();

            bool shouldSchedule = false;
            lock (_surfaceSync)
            {
                if (_disposed || _uploadSurface == null)
                    return false;

                LockedRectangle locked = _uploadSurface.LockRect(LockFlags.None);
                try
                {
                    CopyRows(source, sourceStride, locked.DataPointer, locked.Pitch, rowBytes, height);
                }
                finally
                {
                    _uploadSurface.UnlockRect();
                }

                _frameWidth = width;
                _frameHeight = height;

                // Intermediate frames may be overwritten while the UI is busy.
                // The presenter consumes the newest complete capture, preventing a
                // dispatcher queue from growing behind the live desktop.
                _pendingFrame = true;
                if (!_presentQueued)
                {
                    _presentQueued = true;
                    shouldSchedule = true;
                }
            }

            if (shouldSchedule)
                SchedulePresent();

            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return false;
        }
    }

    private void EnsureResources()
    {
        if (_uploadSurface != null && _renderSurface != null)
            return;

        if (_dispatcher.CheckAccess())
        {
            CreateResources();
            return;
        }

        _dispatcher.Invoke(
            CreateResources,
            DispatcherPriority.Send);
    }

    private void CreateResources()
    {
        if (_disposed || _device == null)
            throw new ObjectDisposedException(nameof(D3DImageFramePresenter));

        if (_uploadSurface != null && _renderSurface != null)
            return;

        IDirect3DSurface9? nextUpload = null;
        IDirect3DSurface9? nextRender = null;
        try
        {
            nextUpload = _device.CreateOffscreenPlainSurface(
                (uint)_surfaceWidth,
                (uint)_surfaceHeight,
                Format.X8R8G8B8,
                Pool.SystemMemory);
            nextRender = _device.CreateRenderTarget(
                (uint)_surfaceWidth,
                (uint)_surfaceHeight,
                Format.X8R8G8B8,
                MultisampleType.None,
                0,
                lockable: false);
        }
        catch
        {
            nextUpload?.Dispose();
            nextRender?.Dispose();
            throw;
        }

        lock (_surfaceSync)
        {
            _uploadSurface?.Dispose();
            if (_renderSurface != null && !ReferenceEquals(_renderSurface, _attachedSurface))
                _renderSurface.Dispose();
            _uploadSurface = nextUpload;
            _renderSurface = nextRender;

            _frameWidth = 0;
            _frameHeight = 0;
            // The fresh render target is uninitialized; the first present must
            // push its full extent to WPF regardless of the frame's size.
            _lastDirtyWidth = _surfaceWidth;
            _lastDirtyHeight = _surfaceHeight;
            _pendingFrame = false;
            _presentQueued = false;

            // Do not attach this target until PresentPendingFrame has populated it.
            // After that first attach it remains the D3DImage back buffer for the
            // presenter's entire lifetime, including every notch resize animation.
        }
    }

    private void SchedulePresent()
    {
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)PresentPendingFrame);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void PresentPendingFrame()
    {
        if (_disposed || _failed)
            return;

        try
        {
            bool presented = false;
            IDirect3DSurface9? retiredSurface = null;
            lock (_surfaceSync)
            {
                _presentQueued = false;

                if (!_pendingFrame || !_image.IsFrontBufferAvailable ||
                    _device == null || _uploadSurface == null ||
                    _renderSurface == null)
                    return;

                int frameWidth = _frameWidth;
                int frameHeight = _frameHeight;
                if (frameWidth <= 0 || frameHeight <= 0)
                    return;

                _image.Lock();
                try
                {
                    var frameRect = new Vortice.Direct3D9.Rect(
                        0, 0, frameWidth, frameHeight);
                    _device.UpdateSurface(
                        _uploadSurface,
                        frameRect,
                        _renderSurface,
                        new Int2(0, 0));

                    if (!ReferenceEquals(_attachedSurface, _renderSurface))
                    {
                        retiredSurface = _attachedSurface;
                        _image.SetBackBuffer(
                            D3DResourceType.IDirect3DSurface9,
                            _renderSurface.NativePointer,
                            enableSoftwareFallback: true);
                        _attachedSurface = _renderSurface;
                    }

                    int dirtyW = Math.Min(
                        Math.Max(frameWidth, _lastDirtyWidth), _surfaceWidth);
                    int dirtyH = Math.Min(
                        Math.Max(frameHeight, _lastDirtyHeight), _surfaceHeight);
                    _image.AddDirtyRect(new Int32Rect(0, 0, dirtyW, dirtyH));
                    _lastDirtyWidth = frameWidth;
                    _lastDirtyHeight = frameHeight;
                    _pendingFrame = false;
                    presented = true;
                }
                finally
                {
                    _image.Unlock();
                }

            }

            if (retiredSurface != null && !ReferenceEquals(retiredSurface, _renderSurface))
                retiredSurface.Dispose();

            if (presented)
                FramePresented?.Invoke();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed || _failed || !_image.IsFrontBufferAvailable)
            return;

        try
        {
            bool shouldSchedule = false;
            lock (_surfaceSync)
            {
                AttachBackBuffer();
                if (_pendingFrame && !_presentQueued)
                {
                    _presentQueued = true;
                    shouldSchedule = true;
                }
            }
            if (shouldSchedule)
                SchedulePresent();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void AttachBackBuffer()
    {
        if (_attachedSurface == null || !_image.IsFrontBufferAvailable)
            return;

        _image.Lock();
        try
        {
            _image.SetBackBuffer(
                D3DResourceType.IDirect3DSurface9,
                _attachedSurface.NativePointer,
                enableSoftwareFallback: true);
            // WPF discards its copy of the surface across a front-buffer loss;
            // the next present must refresh the full envelope once.
            _lastDirtyWidth = _surfaceWidth;
            _lastDirtyHeight = _surfaceHeight;
        }
        finally
        {
            _image.Unlock();
        }
    }

    private void DetachBackBuffer()
    {
        _image.Lock();
        try
        {
            _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        }
        finally
        {
            _image.Unlock();
        }
    }

    private void ReportFailure(Exception ex)
    {
        if (_failed || _disposed)
            return;

        _failed = true;
        if (_dispatcher.CheckAccess())
            Failed?.Invoke(ex);
        else
            _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Failed?.Invoke(ex));
    }

    private static unsafe void CopyRows(
        IntPtr source,
        int sourceStride,
        IntPtr destination,
        int destinationStride,
        int rowBytes,
        int height)
    {
        byte* src = (byte*)source;
        byte* dst = (byte*)destination;
        for (int y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(
                src + y * sourceStride,
                dst + y * destinationStride,
                destinationStride,
                rowBytes);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_dispatcher.CheckAccess())
        {
            DisposeOnDispatcher();
            return;
        }

        try
        {
            _dispatcher.Invoke(DisposeOnDispatcher, DispatcherPriority.Send);
        }
        catch
        {
            // The dispatcher can already be shutting down. Native resources are
            // still released by their finalizers in that terminal path.
            _disposed = true;
        }
    }

    private void DisposeOnDispatcher()
    {
        if (_disposed)
            return;

        _disposed = true;
        _image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;

        lock (_surfaceSync)
        {
            try { DetachBackBuffer(); } catch { }
            _uploadSurface?.Dispose();
            if (_renderSurface != null && !ReferenceEquals(_renderSurface, _attachedSurface))
                _renderSurface.Dispose();
            _attachedSurface?.Dispose();
            _device?.Dispose();
            _direct3D?.Dispose();
            _uploadSurface = null;
            _renderSurface = null;
            _attachedSurface = null;
            _frameWidth = 0;
            _frameHeight = 0;
            _device = null;
            _direct3D = null;
            _pendingFrame = false;
            _presentQueued = false;
        }
    }
}
