using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D9;
using Windows.Graphics.Imaging;
using Windows.Media.Capture.Frames;
using Microsoft.Win32.SafeHandles;
using static VNotch.Controllers.WebcamCaptureController;

namespace VNotch.Controllers;

internal sealed partial class D3DImageFramePresenter
{
    private readonly bool _webcam;

    [LibraryImport("VNotch.Native.dll")]
    internal static partial int vn_webcam_write_surface(nint bitmap, nint target, int pitch, int width, int height);

    private sealed class WebcamGpuHandle(long value) : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() { NativeWebcamDestroy(handle.ToInt64()); return true; }
        internal void Initialize() => SetHandle((nint)value);
    }
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_webcam_gpu_create")]
    private static partial int NativeWebcamCreate(nint device, int width, int height, out long handle);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_webcam_gpu_submit")]
    private static partial int NativeWebcamSubmit(WebcamGpuHandle handle, nint surface, nint frame);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_webcam_gpu_present")]
    private static partial int NativeWebcamPresent(WebcamGpuHandle handle, nint surface);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_webcam_gpu_destroy")]
    private static partial void NativeWebcamDestroy(long handle);
    private WebcamGpuHandle? _webcamGpuHandle;
    private bool _webcamGpuDisabled, _webcamGpuFrame;

    internal SurfaceFrameResult UploadWebcamSurface(MediaFrameReference frame, int token)
    {
        bool transferred = false;
        try
        {
            lock (_surfaceSync)
            {
                if (!_acceptingFrames || token != _sessionGeneration || _disposed) return SurfaceFrameResult.Dropped;
                if (_failed || _uploadSurface == null || _webcamGpuDisabled) return SurfaceFrameResult.Fallback;
                if (_pendingFrame) return SurfaceFrameResult.Dropped;
                if (_webcamGpuHandle == null)
                {
                    Marshal.ThrowExceptionForHR(NativeWebcamCreate(_device!.NativePointer, _surfaceWidth, _surfaceHeight, out long handle));
                    _webcamGpuHandle = new WebcamGpuHandle(handle);
                    _webcamGpuHandle.Initialize();
                }
                var surface = frame.VideoMediaFrame.Direct3DSurface;
                int hr = NativeWebcamSubmit(_webcamGpuHandle,
                    ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr,
                    ((WinRT.IWinRTObject)frame).NativeObject.ThisPtr);
                GC.KeepAlive(frame);
                if (hr < 0) { _webcamGpuDisabled = true; return SurfaceFrameResult.Fallback; }
                if (hr != 0) return SurfaceFrameResult.Dropped;
                transferred = true;
                _webcamGpuFrame = true;
                _frameWidth = _surfaceWidth; _frameHeight = _surfaceHeight;
                _pendingDirtyRows = new GlassDirtyRows(0, _frameHeight);
                _pendingFrame = true;
                _lastUploadTicks = Stopwatch.GetTimestamp();
                if (_presentQueued) return SurfaceFrameResult.Transferred;
                _presentQueued = true;
            }
            RequestRenderingHook();
            SchedulePresent();
            return SurfaceFrameResult.Transferred;
        }
        catch
        {
            _webcamGpuDisabled = true;
            return transferred ? SurfaceFrameResult.Transferred : SurfaceFrameResult.Fallback;
        }
    }

    private bool TakeReadyWebcamSurface()
    {
        if (!_webcamGpuFrame) return true;
        int hr = NativeWebcamPresent(_webcamGpuHandle!, _uploadSurface!.NativePointer);
        if (hr == 1) { ScheduleRetry(); return false; }
        _webcamGpuFrame = false;
        if (hr < 0)
        {
            _webcamGpuDisabled = true;
            _pendingFrame = false;
            return false;
        }
        return true;
    }

    private void DisposeWebcamGpu()
    {
        _webcamGpuHandle?.Dispose();
        _webcamGpuHandle = null;
        _webcamGpuFrame = false;
        _webcamGpuDisabled = false;
    }

    private IDirect3DSurface9 CreateWebcamTarget() => _device!.CreateRenderTarget(
        (uint)_surfaceWidth, (uint)_surfaceHeight, Format.X8R8G8B8, MultisampleType.None, 0, lockable: true);

    internal bool FitsWebcam(int width, int height, int token) =>
        width == _surfaceWidth && height == _surfaceHeight && token == _sessionGeneration;

    internal bool UploadWebcam(SoftwareBitmap bitmap, int token)
    {
        try
        {
            lock (_surfaceSync)
            {
                if (!_acceptingFrames || token != _sessionGeneration || _disposed) return true;
                if (_failed || _uploadSurface == null) return false;
                if (_webcamGpuFrame) return true;
                var locked = _uploadSurface.LockRect(LockFlags.None);
                try
                {
                    var source = ((WinRT.IWinRTObject)bitmap).NativeObject;
                    Marshal.ThrowExceptionForHR(vn_webcam_write_surface(source.ThisPtr, locked.DataPointer,
                        locked.Pitch, _surfaceWidth, _surfaceHeight));
                    GC.KeepAlive(bitmap);
                }
                finally { _uploadSurface.UnlockRect(); }
                _frameWidth = _surfaceWidth;
                _frameHeight = _surfaceHeight;
                _pendingDirtyRows = new GlassDirtyRows(0, _frameHeight);
                _pendingFrame = true;
                _lastUploadTicks = Stopwatch.GetTimestamp();
                if (_presentQueued) return true;
                _presentQueued = true;
            }
            RequestRenderingHook();
            SchedulePresent();
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return false;
        }
    }
}
