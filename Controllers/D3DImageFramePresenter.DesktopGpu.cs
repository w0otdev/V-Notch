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
    }

    private sealed class GpuHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal GpuHandle(long value) : base(true) => SetHandle((IntPtr)value);
        protected override bool ReleaseHandle() { NativeGpuDestroy(handle.ToInt64()); return true; }
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_create")]
    private static partial int NativeGpuCreate(IntPtr device, int width, int height, out long handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_glass_gpu_render")]
    private static partial int NativeGpuRender(GpuHandle handle, in GpuFrame frame);
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

    private readonly bool _useGpuCapture;
    private GpuHandle? _gpuHandle;
    private bool _hasGpuBackBuffer;
    private double _appliedCaptureIntervalMs;
    internal double LastGpuRenderMs { get; private set; }
    internal double LastGpuWaitMs { get; private set; }
    internal double SubmittedGpuRenderMs { get; private set; }
    internal double SubmittedGpuWaitMs { get; private set; }
    internal long GpuCaptureRequests { get; private set; }
    internal long GpuRenderSkips { get; private set; }
    internal long GpuCompletionRetries { get; private set; }
    internal long GpuWorkerTimeouts { get; private set; }
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
                return $"backend={(_gpuHandle == null ? "waiting" : NativeGpuCaptureBackend(_gpuHandle) switch { 1 => "DXGI", 2 => "WGC", _ => "waiting" })} " +
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
            Marshal.ThrowExceptionForHR(NativeGpuCreate(_device!.NativePointer, _surfaceWidth, _surfaceHeight, out long handle));
            _gpuHandle = new GpuHandle(handle);
            _renderSurface = _device.CreateRenderTarget((uint)_surfaceWidth, (uint)_surfaceHeight,
                Format.A8R8G8B8, MultisampleType.None, 0, lockable: false);
            _lastDirtyWidth = _surfaceWidth;
            _lastDirtyHeight = _surfaceHeight;
        }
    }

    internal bool CaptureGpuFrame(in GpuFrame frame, LiquidGlassController.GpuGeometry tag, int generation, out bool submitted)
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
                int hr = NativeGpuRender(_gpuHandle, in frame);
                long renderEnded = Stopwatch.GetTimestamp();
                LastGpuRenderMs = (renderEnded - renderStarted) * 1000.0 / Stopwatch.Frequency;
                Marshal.ThrowExceptionForHR(hr);
                if (hr != 0) { GpuRenderSkips++; return true; }
                // Wait briefly on the capture worker, so the first UI dispatch
                // normally receives a completed texture. Never wait on the UI.
                int waitResult = NativeGpuWaitReady(_gpuHandle, 4);
                Marshal.ThrowExceptionForHR(waitResult);
                if (waitResult != 0) GpuWorkerTimeouts++;
                LastGpuWaitMs = (Stopwatch.GetTimestamp() - renderEnded) * 1000.0 / Stopwatch.Frequency;
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _uploadTag = tag;
                _lastUploadTicks = renderStarted;
                _pendingFrame = true;
                _pendingForcePresent = true;
                _pendingDirtyRows = new GlassDirtyRows(0, frame.Height);
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
