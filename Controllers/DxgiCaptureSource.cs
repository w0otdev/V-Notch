using System;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VNotch.Controllers;

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
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                    out _device,
                    out _context).CheckError();

                targetAdapter.EnumOutputs((uint)outputIdx, out IDXGIOutput output).CheckError();
                using (output)
                {
                    using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
                    IDXGIOutputDuplication? dupl = null;
                    try
                    {
                        using var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
                        if (output5 != null)
                        {
                            dupl = output5.DuplicateOutput1(dxgiDevice, 0, new[] { Format.B8G8R8A8_UNorm });
                        }
                    }
                    catch { }

                    if (dupl == null)
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        dupl = output1.DuplicateOutput(dxgiDevice);
                    }
                    _duplication = dupl;

                    var dc = output.Description.DesktopCoordinates;
                    _outputLeft = dc.Left;
                    _outputTop = dc.Top;
                    _width = dc.Right - dc.Left;
                    _height = dc.Bottom - dc.Top;
                }
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

    private int _stagingW;
    private int _stagingH;

    private ID3D11Texture2D? EnsureStagingTexture(int w, int h)
    {
        if (_device == null || w <= 0 || h <= 0) return null;
        if (_stagingTexture != null && _stagingW >= w && _stagingH >= h && _stagingW <= w + 256 && _stagingH <= h + 256)
            return _stagingTexture;

        _stagingTexture?.Dispose();
        _stagingW = (w + 63) / 64 * 64;
        _stagingH = (h + 63) / 64 * 64;

        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_stagingW,
            Height = (uint)_stagingH,
            Format = Format.B8G8R8A8_UNorm,
            ArraySize = 1,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        });
        return _stagingTexture;
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

            int tx = x - _outputLeft;
            int ty = y - _outputTop;
            int srcLeft = Math.Clamp(tx, 0, _width);
            int srcTop = Math.Clamp(ty, 0, _height);
            int srcRight = Math.Clamp(tx + w, 0, _width);
            int srcBottom = Math.Clamp(ty + h, 0, _height);
            int copyW = Math.Max(0, srcRight - srcLeft);
            int copyH = Math.Max(0, srcBottom - srcTop);

            if (copyW <= 0 || copyH <= 0) return false;

            int dstOffsetX = Math.Max(0, srcLeft - tx);
            int dstOffsetY = Math.Max(0, srcTop - ty);

            var staging = EnsureStagingTexture(copyW, copyH);
            if (staging == null) return false;

            try
            {
                var res = _duplication!.AcquireNextFrame(2, out _, out IDXGIResource? desktopResource);
                if (res.Success)
                {
                    try
                    {
                        using var tex = desktopResource!.QueryInterface<ID3D11Texture2D>();
                        var box = new Box(srcLeft, srcTop, 0, srcRight, srcBottom, 1);
                        _context!.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0, box);
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

                var mapped = _context!.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        byte* src = (byte*)mapped.DataPointer;
                        byte* dst = (byte*)destBits;
                        int srcStride = (int)mapped.RowPitch;
                        int dstStride = w * 4;
                        long copyBytes = (long)copyW * 4;

                        for (int row = 0; row < copyH; row++)
                        {
                            Buffer.MemoryCopy(
                                src + (long)row * srcStride,
                                dst + (long)(row + dstOffsetY) * dstStride + (long)dstOffsetX * 4,
                                dstStride,
                                copyBytes);
                        }
                    }
                }
                finally
                {
                    _context.Unmap(staging, 0);
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

    private ID3D11Texture2D? _sharedTexture;
    private IntPtr _sharedHandle;
    private int _sharedW;
    private int _sharedH;

    public IntPtr SharedHandle => _sharedHandle;
    public int SharedWidth => _sharedW;
    public int SharedHeight => _sharedH;

    private ID3D11Texture2D? EnsureSharedTexture(int w, int h)
    {
        if (_device == null || w <= 0 || h <= 0) return null;
        if (_sharedTexture != null && _sharedW >= w && _sharedH >= h && _sharedW <= w + 256 && _sharedH <= h + 256)
            return _sharedTexture;

        _sharedTexture?.Dispose();
        _sharedHandle = IntPtr.Zero;
        _sharedW = (w + 63) / 64 * 64;
        _sharedH = (h + 63) / 64 * 64;

        _sharedTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_sharedW,
            Height = (uint)_sharedH,
            Format = Format.B8G8R8A8_UNorm,
            ArraySize = 1,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            MiscFlags = ResourceOptionFlags.Shared
        });

        using var dxgiResource = _sharedTexture.QueryInterface<IDXGIResource>();
        _sharedHandle = dxgiResource.SharedHandle;
        return _sharedTexture;
    }

    public bool CaptureGpu(int x, int y, int w, int h, out IntPtr sharedHandle, out int copyW, out int copyH, out int dstOffsetX, out int dstOffsetY)
    {
        sharedHandle = IntPtr.Zero;
        copyW = 0;
        copyH = 0;
        dstOffsetX = 0;
        dstOffsetY = 0;

        if (w <= 0 || h <= 0) return false;

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

            int tx = x - _outputLeft;
            int ty = y - _outputTop;
            int srcLeft = Math.Clamp(tx, 0, _width);
            int srcTop = Math.Clamp(ty, 0, _height);
            int srcRight = Math.Clamp(tx + w, 0, _width);
            int srcBottom = Math.Clamp(ty + h, 0, _height);
            copyW = Math.Max(0, srcRight - srcLeft);
            copyH = Math.Max(0, srcBottom - srcTop);

            if (copyW <= 0 || copyH <= 0) return false;

            dstOffsetX = Math.Max(0, srcLeft - tx);
            dstOffsetY = Math.Max(0, srcTop - ty);

            var sharedTex = EnsureSharedTexture(w, h);
            if (sharedTex == null) return false;

            try
            {
                var res = _duplication!.AcquireNextFrame(2, out _, out IDXGIResource? desktopResource);
                if (res.Success)
                {
                    try
                    {
                        using var tex = desktopResource!.QueryInterface<ID3D11Texture2D>();
                        var box = new Box(srcLeft, srcTop, 0, srcRight, srcBottom, 1);
                        _context!.CopySubresourceRegion(sharedTex, 0, (uint)dstOffsetX, (uint)dstOffsetY, 0, tex, 0, box);
                        _context.Flush();
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
                    if (!_hasFrame) return false;
                }
                else
                {
                    RuntimeLog.Log("LIQUIDGLASS", $"DXGI AcquireNextFrame failed ({res}); scheduling re-init.");
                    ReleaseDuplicationLocked();
                    _nextInitAttemptTicks = Environment.TickCount64 + ReinitThrottleMs;
                    return false;
                }

                sharedHandle = _sharedHandle;
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("LIQUIDGLASS", $"DXGI GPU capture failed: {ex.Message}");
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
        _stagingW = 0;
        _stagingH = 0;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _sharedHandle = IntPtr.Zero;
        _sharedW = 0;
        _sharedH = 0;
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
