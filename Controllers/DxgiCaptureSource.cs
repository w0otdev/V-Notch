using System;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VNotch.Controllers;

/// <summary>
/// Desktop Duplication capture source. Duplicates the output that actually
/// contains the requested rectangle (virtual-desktop coordinates), translates
/// those coordinates into output space, serves the last complete frame when the
/// desktop is static (WAIT_TIMEOUT is not a failure), and re-creates the
/// duplication after ACCESS_LOST, resolution changes or secure-desktop
/// transitions instead of dying permanently.
/// </summary>
public sealed class DxgiCaptureSource : IDisposable
{
    private readonly object _sync = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;

    private int _outputLeft;
    private int _outputTop;
    private int _width;
    private int _height;
    private bool _hasFrame;
    private bool _disposed;
    private long _nextInitAttemptTicks;

    private const int ReinitThrottleMs = 500;

    public DxgiCaptureSource()
    {
        // Fail fast so the caller can fall back immediately when duplication is
        // unavailable (blocked by policy, older Windows, RDP without support...).
        lock (_sync)
        {
            if (!TryInitDuplication(int.MinValue, int.MinValue))
                throw new InvalidOperationException("Desktop Duplication is unavailable.");
        }
    }

    /// <summary>(Re)creates device + duplication for the output containing the
    /// given virtual-desktop point; falls back to the first enumerated output.
    /// Must be called under <see cref="_sync"/>.</summary>
    private bool TryInitDuplication(int px, int py)
    {
        ReleaseDeviceLocked();
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            int adapterIdx = -1, outputIdx = -1;
            int firstAdapterIdx = -1, firstOutputIdx = -1;
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                    {
                        using (output)
                        {
                            var dc = output.Description.DesktopCoordinates;
                            if (firstAdapterIdx < 0) { firstAdapterIdx = (int)a; firstOutputIdx = (int)o; }
                            if (px >= dc.Left && px < dc.Right && py >= dc.Top && py < dc.Bottom)
                            {
                                adapterIdx = (int)a;
                                outputIdx = (int)o;
                            }
                        }
                        if (adapterIdx >= 0) break;
                    }
                }
                if (adapterIdx >= 0) break;
            }

            if (adapterIdx < 0) { adapterIdx = firstAdapterIdx; outputIdx = firstOutputIdx; }
            if (adapterIdx < 0) return false;

            factory.EnumAdapters1((uint)adapterIdx, out IDXGIAdapter1 targetAdapter).CheckError();
            using (targetAdapter)
            {
                // DuplicateOutput requires the D3D device to live on the adapter
                // that owns the output, so the device is created per target.
                D3D11.D3D11CreateDevice(
                    targetAdapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_0 },
                    out _device,
                    out _context).CheckError();

                targetAdapter.EnumOutputs((uint)outputIdx, out IDXGIOutput output).CheckError();
                using (output)
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
                    _duplication = output1.DuplicateOutput(dxgiDevice);

                    var dc = output.Description.DesktopCoordinates;
                    _outputLeft = dc.Left;
                    _outputTop = dc.Top;
                    _width = dc.Right - dc.Left;
                    _height = dc.Bottom - dc.Top;
                }

                _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_width,
                    Height = (uint)_height,
                    Format = Format.B8G8R8A8_UNorm,
                    ArraySize = 1,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MipLevels = 1,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging
                });
            }

            _hasFrame = false;
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("LIQUIDGLASS", $"DXGI init failed: {ex.Message}");
            ReleaseDeviceLocked();
            return false;
        }
    }

    private bool RectOnCurrentOutput(int x, int y, int w, int h)
    {
        int cx = x + w / 2, cy = y + h / 2;
        return cx >= _outputLeft && cx < _outputLeft + _width &&
               cy >= _outputTop && cy < _outputTop + _height;
    }

    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits)
    {
        if (w <= 0 || h <= 0 || destBits == IntPtr.Zero) return false;

        lock (_sync)
        {
            if (_disposed) return false;

            if (_duplication == null || !RectOnCurrentOutput(x, y, w, h))
            {
                long now = Environment.TickCount64;
                if (now < _nextInitAttemptTicks) return false;
                _nextInitAttemptTicks = now + ReinitThrottleMs;
                if (!TryInitDuplication(x + w / 2, y + h / 2)) return false;
            }

            try
            {
                var res = _duplication!.AcquireNextFrame(0, out _, out IDXGIResource? desktopResource);
                if (res.Success)
                {
                    try
                    {
                        using var tex = desktopResource!.QueryInterface<ID3D11Texture2D>();
                        _context!.CopyResource(_stagingTexture!, tex);
                        _hasFrame = true;
                    }
                    finally
                    {
                        desktopResource?.Dispose();
                        _duplication.ReleaseFrame();
                    }
                }
                else if (res == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    // Static desktop: no new frame arrived, but the staging
                    // texture still holds the last complete one — serve that
                    // instead of reporting a failure.
                    if (!_hasFrame) return false;
                }
                else
                {
                    // ACCESS_LOST and friends (resolution change, secure
                    // desktop, driver reset): drop the duplication and
                    // re-create it on the next call.
                    RuntimeLog.Log("LIQUIDGLASS", $"DXGI AcquireNextFrame failed ({res}); scheduling re-init.");
                    ReleaseDuplicationLocked();
                    _nextInitAttemptTicks = Environment.TickCount64 + ReinitThrottleMs;
                    return false;
                }

                var mapped = _context!.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        byte* src = (byte*)mapped.DataPointer;
                        byte* dst = (byte*)destBits;
                        int srcStride = (int)mapped.RowPitch;
                        int dstStride = w * 4;

                        // Translate virtual-desktop coordinates into this
                        // output's texture space, then clamp the copy to the
                        // texture without ever letting min > max (a request
                        // wider than the output copies what exists).
                        int tx = x - _outputLeft;
                        int ty = y - _outputTop;
                        int copyW = Math.Min(w, _width);
                        int srcX = Math.Clamp(tx, 0, _width - copyW);
                        long copyBytes = (long)copyW * 4;

                        for (int row = 0; row < h; row++)
                        {
                            int sy = Math.Clamp(ty + row, 0, _height - 1);
                            Buffer.MemoryCopy(
                                src + (long)sy * srcStride + (long)srcX * 4,
                                dst + (long)row * dstStride,
                                dstStride,
                                copyBytes);
                        }
                    }
                }
                finally
                {
                    _context.Unmap(_stagingTexture!, 0);
                }

                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("LIQUIDGLASS", $"DXGI capture failed: {ex.Message}");
                ReleaseDuplicationLocked();
                _nextInitAttemptTicks = Environment.TickCount64 + ReinitThrottleMs;
                return false;
            }
        }
    }

    private void ReleaseDuplicationLocked()
    {
        _duplication?.Dispose();
        _duplication = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _hasFrame = false;
    }

    private void ReleaseDeviceLocked()
    {
        ReleaseDuplicationLocked();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseDeviceLocked();
        }
    }
}
