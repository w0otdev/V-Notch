using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vortice.Direct3D9;

namespace VNotch.Controllers;

internal sealed partial class D3DImageFramePresenter
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GpuFrame
    {
        public int X, Y, Width, Height;
        public float Blur;
        public int Refract;
        public fixed float Lens[128];

        public void Set(int register, double value)
        {
            float scalar = (float)value;
            Lens[register * 4] = float.IsFinite(scalar) ? scalar : 0;
        }

        public readonly float Get(int register)
        {
            fixed (float* lens = Lens)
                return lens[register * 4];
        }

        // Second component of a register; only the native D3D11 shader reads it.
        public void SetY(int register, double value)
        {
            float scalar = (float)value;
            Lens[register * 4 + 1] = float.IsFinite(scalar) ? scalar : 0;
        }

        public readonly float GetY(int register)
        {
            fixed (float* lens = Lens)
                return lens[register * 4 + 1];
        }

        public readonly bool LensEquals(in GpuFrame other)
        {
            fixed (float* a = Lens, b = other.Lens)
                return new ReadOnlySpan<byte>(a, 128 * sizeof(float)).SequenceEqual(new ReadOnlySpan<byte>(b, 128 * sizeof(float)));
        }

        public readonly bool FrameEquals(in GpuFrame other) =>
            X == other.X && Y == other.Y && Width == other.Width && Height == other.Height &&
            Blur == other.Blur && (Refract & 1) == (other.Refract & 1) && LensEquals(in other);
    }

    private sealed class GpuHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal GpuHandle(long value) : base(true) => SetHandle((IntPtr)value);
        protected override bool ReleaseHandle() { NativeGpuDestroy(handle.ToInt64()); return true; }
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_create_on_adapter", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeGpuCreate(IntPtr device, int width, int height, long renderLuid, out long handle,
        Span<char> adapterName, uint nameCapacity);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_render")]
    private static partial int NativeGpuRender(GpuHandle handle, in GpuFrame frame);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_render_windows")]
    private static partial int NativeGpuRenderWindows(GpuHandle handle, in GpuFrame frame);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_wait_backdrop")]
    private static partial uint NativeGpuWaitBackdrop(GpuHandle handle, SafeWaitHandle interrupt, uint timeoutMs);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_render_pixels")]
    private static partial int NativeGpuRenderPixels(GpuHandle handle, in GpuFrame frame, IntPtr pixels, int length, int stride);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_wait_ready")]
    private static partial int NativeGpuWaitReady(GpuHandle handle, uint timeoutMs);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_present_ready")]
    private static partial int NativeGpuPresentReady(GpuHandle handle, IntPtr surface);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_destroy")]
    private static partial void NativeGpuDestroy(long handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_reset")]
    private static partial void NativeGpuReset(GpuHandle handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_find_adapter")]
    private static partial uint NativeGpuFindAdapter(IntPtr direct3D, IntPtr hwnd);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_find_preferred_adapter")]
    private static partial uint NativeGpuFindPreferredAdapter(IntPtr direct3D, IntPtr hwnd, out long renderLuid);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_capture_status")]
    private static partial int NativeGpuCaptureStatus(GpuHandle handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_capture_backend")]
    private static partial int NativeGpuCaptureBackend(GpuHandle handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_borderless_status")]
    private static partial int NativeGpuBorderlessStatus(GpuHandle handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_fence_status")]
    private static partial int NativeGpuFenceStatus(GpuHandle handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_set_interval")]
    private static partial int NativeGpuSetInterval(GpuHandle handle, double intervalMs);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_take_capture_count")]
    private static partial ulong NativeGpuTakeCaptureCount(GpuHandle handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_consumer_stalls")]
    private static partial ulong NativeGpuConsumerStalls(GpuHandle handle);

    private GpuHandle? _gpuHandle;
    private bool _hasGpuBackBuffer;
    private GpuFrame _pendingGpuFrame;
    internal GpuFrame? PresentedGpuFrame { get; private set; }
    private double _appliedCaptureIntervalMs;
    private string _lastCaptureRoute = "waiting";
    private string _renderAdapterName = "waiting";
    internal double LastGpuRenderMs { get; private set; }
    internal double LastGpuWaitMs { get; private set; }
    internal double SubmittedGpuRenderMs { get; private set; }
    internal double SubmittedGpuWaitMs { get; private set; }
    internal long GpuCaptureRequests { get; private set; }
    internal long GpuRenderSkips { get; private set; }
    internal long GpuCompletionRetries { get; private set; }
    internal long GpuWorkerTimeouts { get; private set; }
    // The worker waits outside the surface lock. Stop and UI handoff can wake
    // it through interrupt even while the underlying window stays static.
    internal bool TryWaitForBackdrop(SafeWaitHandle interrupt, int timeoutMs, out bool arrived)
    {
        arrived = false;
        if (_gpuHandle is not { } handle) return false;
        try
        {
            uint result = NativeGpuWaitBackdrop(handle, interrupt, (uint)timeoutMs);
            arrived = result == 1; // WAIT_OBJECT_0 + 1
            return result != uint.MaxValue; // WAIT_FAILED
        }
        catch (ObjectDisposedException) { return false; }
    }
    internal ulong GpuConsumerRetries
    {
        get { lock (_surfaceSync) return _gpuHandle == null ? 0 : NativeGpuConsumerStalls(_gpuHandle); }
    }
    internal ulong TakeCaptureCount()
    {
        lock (_surfaceSync) return _gpuHandle == null ? 0 : NativeGpuTakeCaptureCount(_gpuHandle);
    }
    internal string CaptureStatus
    {
        get
        {
            lock (_surfaceSync)
                return $"renderGpu={_renderAdapterName} route={_lastCaptureRoute} backend={(_gpuHandle == null ? "waiting" : NativeGpuCaptureBackend(_gpuHandle) switch { 1 => "DXGI", 2 => "WGC", _ => "waiting" })} " +
                    $"capture=0x{(_gpuHandle == null ? 1 : NativeGpuCaptureStatus(_gpuHandle)):X8} " +
                    $"borderless=0x{(_gpuHandle == null ? 1 : NativeGpuBorderlessStatus(_gpuHandle)):X8} " +
                    $"fence=0x{(_gpuHandle == null ? 1 : NativeGpuFenceStatus(_gpuHandle)):X8} " +
                    $"consumer-stalls={(_gpuHandle == null ? 0 : NativeGpuConsumerStalls(_gpuHandle))}";
        }
    }

    private void CreateGpuResources()
    {
        lock (_surfaceSync)
        {
            if (_gpuHandle != null) return;
            Span<char> name = stackalloc char[128]; // DXGI_ADAPTER_DESC1.Description
            Marshal.ThrowExceptionForHR(NativeGpuCreate(_device!.NativePointer, _surfaceWidth, _surfaceHeight,
                _glassRenderLuid, out long handle, name, (uint)name.Length));
            var nextHandle = new GpuHandle(handle);
            try
            {
                int end = name.IndexOf('\0');
                _renderAdapterName = (end < 0 ? name : name[..end]).ToString();
                _renderSurface = _device.CreateRenderTarget((uint)_surfaceWidth, (uint)_surfaceHeight,
                    Format.A8R8G8B8, MultisampleType.None, 0, lockable: false);
                Volatile.Write(ref _gpuHandle, nextHandle);
            }
            catch { nextHandle.Dispose(); throw; }
            _lastDirtyWidth = _surfaceWidth;
            _lastDirtyHeight = _surfaceHeight;
        }
    }

    internal bool CaptureGpuFrame(in GpuFrame frame, LiquidGlassController.GpuGeometry tag, int generation, out bool submitted,
        IntPtr pixels = default, int stride = 0, int length = 0, bool exactWindows = false)
    {
        submitted = false;
        if (_disposed || _failed) return false;
        try
        {
            EnsureResources();
            bool schedule;
            lock (_surfaceSync)
            {
                if (_disposed || !_acceptingFrames || generation != _sessionGeneration || _gpuHandle == null) return true;
                // Preserve the pending pixels and their geometry until WPF consumes them.
                if (_pendingFrame) return true;
                double intervalMs = Volatile.Read(ref _frameIntervalMs);
                if (intervalMs != _appliedCaptureIntervalMs)
                {
                    Marshal.ThrowExceptionForHR(NativeGpuSetInterval(_gpuHandle, intervalMs));
                    _appliedCaptureIntervalMs = intervalMs;
                }
                long renderStarted = Stopwatch.GetTimestamp();
                GpuCaptureRequests++;
                _lastCaptureRoute = exactWindows ? "windows" : pixels == IntPtr.Zero ? "desktop" : "pixels";
                int hr = exactWindows ? NativeGpuRenderWindows(_gpuHandle, in frame) : pixels == IntPtr.Zero
                    ? NativeGpuRender(_gpuHandle, in frame)
                    : NativeGpuRenderPixels(_gpuHandle, in frame, pixels, length, stride);
                long renderEnded = Stopwatch.GetTimestamp();
                LastGpuRenderMs = (renderEnded - renderStarted) * 1000.0 / Stopwatch.Frequency;
                switch (GlassGpuResultPolicy.Classify(exactWindows, hr))
                {
                    case GlassGpuRenderOutcome.UseBackdropFallback: return false;
                    case GlassGpuRenderOutcome.KeepPrevious: GpuRenderSkips++; return true;
                    case GlassGpuRenderOutcome.Fail: Marshal.ThrowExceptionForHR(hr); break;
                }
                // Wait briefly on the capture worker, so the first UI dispatch
                // normally receives a completed texture. Never wait on the UI.
                int waitResult = NativeGpuWaitReady(_gpuHandle, 4);
                Marshal.ThrowExceptionForHR(waitResult);
                if (waitResult != 0) GpuWorkerTimeouts++;
                LastGpuWaitMs = (Stopwatch.GetTimestamp() - renderEnded) * 1000.0 / Stopwatch.Frequency;
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _uploadTag = tag;
                _pendingGpuFrame = frame;
                _lastUploadTicks = renderStarted;
                _pendingFrame = true;
                _pendingForcePresent = true;
                submitted = true;
                schedule = !_presentQueued;
                _presentQueued = true;
            }
            RequestRenderingHook();
            if (schedule) SchedulePresent();
            return true;
        }
        catch (Exception ex) { ReportFailure(ex); return false; }
    }

    // Called only while D3DImage is writable. Native code copies the completed
    // frame into this persistent back buffer and fences its shared source.
    private bool TakeReadyGpuSurface()
    {
        int hr = NativeGpuPresentReady(_gpuHandle!, _renderSurface!.NativePointer);
        Marshal.ThrowExceptionForHR(hr);
        if (hr != 0) { GpuCompletionRetries++; ScheduleRetry(); return false; }
        _hasGpuBackBuffer = true;
        return true;
    }
}
