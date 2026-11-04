using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D9;
using Windows.Graphics.Imaging;

namespace VNotch.Controllers;

internal sealed partial class D3DImageFramePresenter
{
    private readonly bool _webcam;

    [LibraryImport("VNotch.Native.dll")]
    internal static partial int vn_webcam_write_surface(nint bitmap, nint target, int pitch, int width, int height);

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
