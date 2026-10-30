using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;
using Vortice.Direct3D9;
using Vortice.Mathematics;

namespace VNotch.Controllers;

internal sealed partial class D3DImageFramePresenter : IDisposable
{
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_score_d3d9_adapter", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeScoreD3D9Adapter(
        ReadOnlySpan<char> description,
        int descLen,
        uint vendorId,
        out long outScore);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_should_submit_frame")]
    private static partial int NativeShouldSubmitFrame(
        long nowTicks,
        long lastSubmitTicks,
        long stopwatchFreq,
        double activeIntervalMs,
        byte forcePresent,
        out byte outShouldSubmit);

    private readonly Dispatcher _dispatcher;
    private readonly object _surfaceSync = new();
    private readonly ID3DFrameTarget _image;

    private IDirect3D9Ex? _direct3D;
    private IDirect3DDevice9Ex? _device;
    private IDirect3DSurface9? _uploadSurface;
    private IDirect3DSurface9? _renderSurface;
    // Keep D3DImage surface separate from _renderSurface during resize uploads
    // so WPF draws the last complete frame until replacement is ready.
    private IDirect3DSurface9? _attachedSurface;
    private readonly int _surfaceWidth;
    private readonly int _surfaceHeight;
    private int _frameWidth;
    private int _frameHeight;
    // Track recently presented frame bounds to avoid full envelope dirtying
    // while ensuring shrinking frames properly refresh previously covered areas.
    private int _lastDirtyWidth;
    private int _lastDirtyHeight;
    private bool _pendingFrame;
    internal bool HasPendingFrame => Volatile.Read(ref _pendingFrame);
    private bool _presentQueued;
    private int _retryScheduled;
    private readonly DispatcherTimer _retryTimer;
    private readonly Action _presentAction;
    private GlassDirtyRows _pendingDirtyRows;
    private long _transferredBytes;
    private long _submittedFrames;
    private long _queueTicks;
    private long _lastUploadTicks;
    private long _lastSubmitTicks;
    private double _frameIntervalMs = 1000.0 / 60.0;
    private bool _pendingForcePresent;
    private readonly bool _ultimateGpuMode;
    private readonly uint _adapterIndex;

    internal bool IsUltimateGpuMode => _ultimateGpuMode;
    internal uint AdapterIndex => _adapterIndex;

    internal void SetFrameIntervalMs(double intervalMs)
    {
        if (double.IsFinite(intervalMs) && intervalMs > 0.0)
            Volatile.Write(ref _frameIntervalMs, intervalMs);
    }

    internal (long Bytes, long Frames, double MeanQueueMs) TakeStatistics()
    {
        long frames = Interlocked.Exchange(ref _submittedFrames, 0);
        long ticks = Interlocked.Exchange(ref _queueTicks, 0);
        return (Interlocked.Exchange(ref _transferredBytes, 0), frames,
            frames == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / frames);
    }
    private bool _disposed;
    private bool _failed;
    private int _sessionGeneration;
    private bool _acceptingFrames;

    private LiquidGlassController.GpuGeometry _uploadTag;
    private LiquidGlassController.GpuGeometry _presentedTag;

    public ImageSource ImageSource => _image.ImageSource;

    public event Action<LiquidGlassController.GpuGeometry>? FramePresented;
    public event Action<Exception>? Failed;
    public event Action? RefreshRequested;

    public D3DImageFramePresenter(
        Dispatcher dispatcher,
        IntPtr windowHandle,
        int surfaceWidth,
        int surfaceHeight,
        ID3DFrameTarget? frameTarget = null,
        bool ultimateGpuMode = false)
    {
        _image = frameTarget ?? new D3DFrameTarget();
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
        _ultimateGpuMode = ultimateGpuMode;
        _presentAction = PresentPendingFrame;
        // CompositionTarget.Rendering already retries at the display cadence.
        // This fallback yields to input and allows one frame interval between attempts.
        _retryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 60)
        };
        _retryTimer.Tick += OnRetryTick;

        // WPF D3DImage accepts IDirect3DSurface9 back buffers, even on current Windows.
        _direct3D = D3D9.Direct3DCreate9Ex();
        try
        {
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

            uint preferredAdapter = ultimateGpuMode ? SelectBestHardwareAdapter(_direct3D) : 0u;
            const CreateFlags deviceFlags =
                CreateFlags.Multithreaded |
                CreateFlags.FpuPreserve |
                CreateFlags.HardwareVertexProcessing;

            if (preferredAdapter != 0u)
            {
                try
                {
                    _device = _direct3D.CreateDeviceEx(
                        preferredAdapter,
                        DeviceType.Hardware,
                        windowHandle,
                        deviceFlags,
                        present);
                    _adapterIndex = preferredAdapter;
                }
                catch
                {
                    _device = null;
                }
            }

            if (_device == null)
            {
                _device = _direct3D.CreateDeviceEx(
                    0,
                    DeviceType.Hardware,
                    windowHandle,
                    deviceFlags,
                    present);
                _adapterIndex = 0u;
            }
        }
        catch
        {
            _retryTimer.Stop();
            _retryTimer.Tick -= OnRetryTick;
            _direct3D.Dispose();
            throw;
        }

        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        CompositionTarget.Rendering += OnRendering;
    }

    internal static uint SelectBestHardwareAdapter(IDirect3D9Ex direct3D)
    {
        try
        {
            uint count = (uint)direct3D.AdapterCount;
            if (count <= 1) return 0u;

            uint bestAdapter = 0u;
            long bestScore = long.MinValue;
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    AdapterIdentifier id = direct3D.GetAdapterIdentifier(i);
                    string desc = id.Description ?? string.Empty;
                    uint vendorId = (uint)id.VendorId;
                    if (NativeScoreD3D9Adapter(desc.AsSpan(), desc.Length, vendorId, out long score) == 0 &&
                        score > bestScore)
                    {
                        bestScore = score;
                        bestAdapter = i;
                    }
                }
                catch
                {
                    // Ignore adapters whose identifiers cannot be queried.
                }
            }
            return bestAdapter;
        }
        catch
        {
            return 0u;
        }
    }

#pragma warning disable S107 // High performance D3D presentation pipeline requires individual frame parameters
    public bool UploadFrame(IntPtr source, int width, int height, int sourceStride,
        int generation, out bool uploaded, LiquidGlassController.GpuGeometry tag = default, bool forcePresent = false)
#pragma warning restore S107
    {
        uploaded = false;
        if (_disposed || _failed || source == IntPtr.Zero || width <= 0 || height <= 0)
            return false;
        if (width > _surfaceWidth || height > _surfaceHeight)
            return false;

        int rowBytes = checked(width * 4);
        if (sourceStride < rowBytes)
            return false;

        try
        {
            lock (_surfaceSync)
            {
                if (!_acceptingFrames || generation != _sessionGeneration) return true;
            }
            EnsureResources();

            bool shouldSchedule = false;
            lock (_surfaceSync)
            {
                // Stop/Start may run while this worker waits for the dispatcher.
                // An old capture must never enter a reused presentation surface.
                if (!_acceptingFrames || generation != _sessionGeneration) return true;
                if (_disposed || _uploadSurface == null)
                    return false;

                LockedRectangle locked = _uploadSurface.LockRect(LockFlags.None);
                try
                {
                    GlassDirtyRows dirtyRows;
                    if (_frameWidth != width || _frameHeight != height)
                    {
                        // Initialize every pixel when the capture extent changes.
                        CopyRows(source, sourceStride, locked.DataPointer, locked.Pitch, rowBytes, height);
                        dirtyRows = new GlassDirtyRows(0, height);
                    }
                    else
                    {
                        dirtyRows = GlassUploadDelta.CopyChangedRows(source, sourceStride, locked.DataPointer,
                            locked.Pitch, rowBytes, height);
                    }
                    if (dirtyRows.IsEmpty && !forcePresent)
                    {
                        // Pending pixels already represent this frame. Leave their
                        // scheduling/tag intact until WPF consumes them.
                        return true;
                    }
                    // Geometry changes/explicit recovery still submit a frame; only
                    // expand to full height when no specific dirty rows were detected.
                    if (dirtyRows.IsEmpty && forcePresent) dirtyRows = new GlassDirtyRows(0, height);
                    _pendingDirtyRows = _pendingDirtyRows.Union(dirtyRows);
                }
                finally
                {
                    _uploadSurface.UnlockRect();
                }

                _frameWidth = width;
                _frameHeight = height;
                _uploadTag = tag;
                _lastUploadTicks = Stopwatch.GetTimestamp();
                if (forcePresent) _pendingForcePresent = true;

                // Consume newest complete capture directly to prevent dispatcher queues
                // from growing behind the live desktop when the UI thread is busy.
                _pendingFrame = true;
                uploaded = true;
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
            lock (_surfaceSync)
            {
                if (!_acceptingFrames || generation != _sessionGeneration) return true;
            }
            ReportFailure(ex);
            return false;
        }
    }

    private void EnsureResources()
    {
        if (Volatile.Read(ref _uploadSurface) != null && Volatile.Read(ref _renderSurface) != null)
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

        if (Volatile.Read(ref _uploadSurface) != null && Volatile.Read(ref _renderSurface) != null)
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
            // A partial first capture must not expose uninitialized pixels in
            // the fixed-size texture around it. Initialize once, not per frame.
            LockedRectangle initial = nextUpload.LockRect(LockFlags.None);
            try { ClearSurface(initial.DataPointer, initial.Pitch, _surfaceHeight); }
            finally { nextUpload.UnlockRect(); }
            _device.UpdateSurface(nextUpload,
                new Vortice.Direct3D9.Rect(0, 0, _surfaceWidth, _surfaceHeight),
                nextRender, new Int2(0, 0));
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
            // The initialized target still needs one full transfer into WPF's
            // copy on first attach, regardless of the capture region's size.
            _lastDirtyWidth = _surfaceWidth;
            _lastDirtyHeight = _surfaceHeight;
            _pendingFrame = false;
            _pendingDirtyRows = default;
            _presentQueued = false;

            // Attach target only after initial population; it remains the D3DImage
            // back buffer across all subsequent resizes throughout presenter lifetime.
        }
    }

    private void SchedulePresent()
    {
        try
        {
            // Capture must not preempt the animation/layout work that positions
            // its lens. Consume the newest pending frame at render priority.
            _dispatcher.BeginInvoke(DispatcherPriority.Render, _presentAction);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    internal void PresentPendingFrame()
    {
        if (_disposed || _failed)
            return;

        try
        {
            bool presented = false;
            // Never stall the UI while the worker copies a desktop texture.
            bool acquiredSync = Monitor.TryEnter(_surfaceSync);
            if (!acquiredSync)
            {
                ScheduleRetry();
                return;
            }

            try
            {
                _presentQueued = false;
                if (!_acceptingFrames || !_pendingFrame || !_image.IsFrontBufferAvailable || _device == null)
                    return;

                int frameWidth = _frameWidth;
                int frameHeight = _frameHeight;
                if (frameWidth <= 0 || frameHeight <= 0)
                    return;

                if (_renderSurface == null || _uploadSurface == null)
                    return;

                // WPF D3DImage.LockImpl increments its lock count even on timeout.
                // Every non-throwing TryLock needs Unlock; false only forbids writes.
                // Otherwise one busy compositor frame permanently blocks presentation.
                bool imageWritable = _image.TryLock(new Duration(TimeSpan.Zero));
                try
                {
                    if (!imageWritable)
                    {
                        ScheduleRetry();
                        return;
                    }
                    // Transfer full visible region on resize/recovery; otherwise transfer
                    // only rows modified since the last successful present.
                    bool fullDirty = frameWidth != _lastDirtyWidth || frameHeight != _lastDirtyHeight;
                    var dirtyRows = NormalizeDirtyRows(_pendingDirtyRows, frameHeight, fullDirty);
                    int dirtyTop = dirtyRows.Top;
                    int dirtyBottom = dirtyRows.Bottom;
                    var frameRect = new Vortice.Direct3D9.Rect(
                        0, dirtyTop, frameWidth, dirtyBottom);
                    _device.UpdateSurface(
                        _uploadSurface,
                        frameRect,
                        _renderSurface,
                        new Int2(0, dirtyTop));
                    Interlocked.Add(ref _transferredBytes, (long)frameWidth * (dirtyBottom - dirtyTop) * 4);

                    if (!ReferenceEquals(_attachedSurface, _renderSurface))
                    {
                        _image.SetBackBuffer(
                            D3DResourceType.IDirect3DSurface9,
                            _renderSurface.NativePointer,
                            enableSoftwareFallback: true);
                        _attachedSurface = _renderSurface;
                    }

                    int dirtyWidth = Math.Max(frameWidth, _lastDirtyWidth);
                    int dirtyHeight = Math.Max(frameHeight, _lastDirtyHeight);
                    _image.AddDirtyRect(fullDirty
                        ? new Int32Rect(0, 0, dirtyWidth, dirtyHeight)
                        : new Int32Rect(0, dirtyTop, frameWidth, dirtyBottom - dirtyTop));
                    _lastDirtyWidth = frameWidth;
                    _lastDirtyHeight = frameHeight;

                    _pendingFrame = false;
                    _pendingForcePresent = false;
                    _pendingDirtyRows = default;
                    _presentedTag = _uploadTag;
                    presented = true;
                    long submitNow = Stopwatch.GetTimestamp();
                    Volatile.Write(ref _lastSubmitTicks, submitNow);
                    Interlocked.Add(ref _queueTicks, submitNow - _lastUploadTicks);
                    Interlocked.Increment(ref _submittedFrames);
                }
                finally
                {
                    _image.Unlock();
                }
            }
            finally
            {
                Monitor.Exit(_surfaceSync);
            }

            if (presented)
                FramePresented?.Invoke(_presentedTag);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void ScheduleRetry()
    {
        if (_disposed || _failed || Interlocked.CompareExchange(ref _retryScheduled, 1, 0) != 0)
            return;

        // All callers run on the dispatcher. Reuse one timer instead of creating
        // Task/continuation/closure objects during every buffer-contention retry.
        _retryTimer.Start();
    }

    internal static GlassDirtyRows NormalizeDirtyRows(GlassDirtyRows rows, int frameHeight, bool fullDirty)
    {
        if (frameHeight <= 0) throw new ArgumentOutOfRangeException(nameof(frameHeight));
        int top = Math.Clamp(rows.Top, 0, frameHeight);
        int bottom = Math.Clamp(rows.Bottom, 0, frameHeight);
        // Invalid/empty metadata must not lose a frame or disable the presenter.
        return fullDirty || bottom <= top ? new GlassDirtyRows(0, frameHeight) : new GlassDirtyRows(top, bottom);
    }

    internal void PrepareResources() => EnsureResources();

    internal void BeginSession(int generation)
    {
        SuspendPresentation();
        if (_dispatcher.CheckAccess())
        {
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
            {
                if (_disposed || _failed) return;
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
            }));
        }

        lock (_surfaceSync)
        {
            _sessionGeneration = generation;
            _acceptingFrames = true;
            Interlocked.Exchange(ref _queueTicks, 0);
            Interlocked.Exchange(ref _submittedFrames, 0);
            Interlocked.Exchange(ref _transferredBytes, 0);
        }
    }

    internal void SuspendPresentation()
    {
        _retryTimer.Stop();
        Interlocked.Exchange(ref _retryScheduled, 0);
        if (_dispatcher.CheckAccess())
            CompositionTarget.Rendering -= OnRendering;
        else
            _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => CompositionTarget.Rendering -= OnRendering));

        lock (_surfaceSync)
        {
            _acceptingFrames = false;
            _pendingFrame = false;
            _pendingForcePresent = false;
            _pendingDirtyRows = default;
            _presentQueued = false;
            Volatile.Write(ref _lastSubmitTicks, 0);
            // Keep allocations, but require a fresh capture before displaying
            // anything from the next Spotlight session.
            _frameWidth = _frameHeight = 0;
        }
    }

    private void OnRetryTick(object? sender, EventArgs e)
    {
        _retryTimer.Stop();
        Interlocked.Exchange(ref _retryScheduled, 0);
        if (_disposed || _failed) return;
        lock (_surfaceSync)
        {
            if (_attachedSurface == null) AttachBackBuffer();
        }
        if (_pendingFrame) PresentPendingFrame();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Consume at composition boundary so capture presenter does not stall
        // when queued dispatcher callbacks run while WPF owns the buffer.
        if (_disposed || _failed || !Volatile.Read(ref _pendingFrame))
            return;

        long nowTicks = Stopwatch.GetTimestamp();
        long lastSubmit = Volatile.Read(ref _lastSubmitTicks);
        double intervalMs = Volatile.Read(ref _frameIntervalMs);
        byte forceFlag = _pendingForcePresent ? (byte)1 : (byte)0;
        if (NativeShouldSubmitFrame(nowTicks, lastSubmit, Stopwatch.Frequency, intervalMs, forceFlag, out byte shouldSubmit) == 0 &&
            shouldSubmit == 0)
        {
            return;
        }

        PresentPendingFrame();
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
            RefreshRequested?.Invoke();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    internal void AttachBackBuffer()
    {
        var surface = _attachedSurface ?? _renderSurface;
        if (surface == null || !_image.IsFrontBufferAvailable)
            return;

        // Balance the WPF lock count on timeout here as well as during presentation.
        bool imageWritable = _image.TryLock(new Duration(TimeSpan.FromMilliseconds(5)));
        try
        {
            if (!imageWritable) { _attachedSurface = null; ScheduleRetry(); return; }
            _image.SetBackBuffer(
                D3DResourceType.IDirect3DSurface9,
                surface.NativePointer,
                enableSoftwareFallback: true);
            // WPF discards its copy of the surface across a front-buffer loss,
            _attachedSurface = surface;
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

#pragma warning disable S6640 // Pointer-based row blitting is required for high-performance frame presentation
    private static unsafe void ClearSurface(IntPtr pixels, int pitch, int height)
    {
        new Span<byte>((void*)pixels, checked(pitch * height)).Clear();
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

        if (sourceStride == destinationStride && destinationStride == rowBytes)
        {
            long total = (long)rowBytes * height;
            Buffer.MemoryCopy(src, dst, total, total);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(
                src + y * sourceStride,
                dst + y * destinationStride,
                destinationStride,
                rowBytes);
        }
    }
#pragma warning restore S6640

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
        _retryTimer.Stop();
        _retryTimer.Tick -= OnRetryTick;
        CompositionTarget.Rendering -= OnRendering;
        _image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;

        lock (_surfaceSync)
        {
            _acceptingFrames = false;
            try
            {
                DetachBackBuffer();
            }
            catch (Exception)
            {
                // Surface detachment errors during shutdown/device disposal are harmless and ignored.
            }
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
