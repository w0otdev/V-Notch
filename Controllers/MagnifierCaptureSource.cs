using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class MagnifierCaptureSource : IDisposable
{
    private const string LogTag = "LIQUIDGLASS";
    private const string MagDll = "Magnification.dll";
    private const string WC_MAGNIFIER = "Magnifier";
    private const int MW_FILTERMODE_EXCLUDE = 0;

    private const int WS_CHILD = 0x40000000;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int LWA_ALPHA = 0x2;
    private const int SW_SHOWNA = 8;
    private const uint PM_REMOVE = 1;

#pragma warning disable IDE1006, S101 // Match Win32 SDK structure naming conventions
    [StructLayout(LayoutKind.Sequential)]
    private struct MAGIMAGEHEADER
    {
        public uint width;
        public uint height;
        public Guid format;
        public uint stride;
        public uint offset;
        public UIntPtr cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptx;
        public int pty;
    }
#pragma warning restore IDE1006, S101

#pragma warning disable S107 // Win32 MagImageScalingCallback signature is mandated by the Windows Magnification API
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MagImageScalingCallback(
        IntPtr hwnd, IntPtr srcdata, MAGIMAGEHEADER srcheader,
        IntPtr destdata, MAGIMAGEHEADER destheader,
        Win32Interop.RECT unclipped, Win32Interop.RECT clipped, IntPtr dirty);
#pragma warning restore S107

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(MagDll, SetLastError = true)] private static extern bool MagInitialize();
    [DllImport(MagDll, SetLastError = true)] private static extern bool MagUninitialize();
    [DllImport(MagDll, SetLastError = true)] private static extern bool MagSetWindowSource(IntPtr hwnd, Win32Interop.RECT rect);
    [DllImport(MagDll, SetLastError = true)] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int dwFilterMode, int count, IntPtr[] pHWND);
    [DllImport(MagDll, SetLastError = true)] private static extern bool MagSetImageScalingCallback(IntPtr hwnd, MagImageScalingCallback callback);

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte alpha, int flags);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private static readonly object _sharedSync = new();
    private static MagnifierCaptureSource? _sharedInstance;
    private static int _sharedRefCount;

    public static MagnifierCaptureSource AcquireShared(IntPtr excludeHwnd)
    {
        lock (_sharedSync)
        {
            if (_sharedInstance == null || !_sharedInstance._running || !_sharedInstance.IsReady)
            {
                try
                {
                    _sharedInstance?.Dispose();
                }
                catch (Exception)
                {
                    // Ignore errors when replacing an unready shared instance.
                }
                _sharedInstance = new MagnifierCaptureSource();
                _sharedInstance.Initialize(excludeHwnd);
            }
            else if (excludeHwnd != IntPtr.Zero)
            {
                _sharedInstance.AddExcludeHwnd(excludeHwnd);
            }
            _sharedRefCount++;
            _sharedInstance._captureEnabled = true;
            _sharedInstance._request.Set();
            return _sharedInstance;
        }
    }

    public static void ReleaseShared(IntPtr excludeHwnd)
    {
        lock (_sharedSync)
        {
            if (excludeHwnd != IntPtr.Zero && _sharedInstance != null)
            {
                _sharedInstance.RemoveExcludeHwnd(excludeHwnd);
            }
            _sharedRefCount = Math.Max(0, _sharedRefCount - 1);
            if (_sharedRefCount == 0 && _sharedInstance != null)
            {
                _sharedInstance._captureEnabled = false;
                _sharedInstance._request.Set();
            }
        }
    }

    public static void ShutdownShared()
    {
        lock (_sharedSync)
        {
            try
            {
                _sharedInstance?.Dispose();
            }
            catch (Exception)
            {
                // Ignore errors during shared shutdown.
            }
            _sharedInstance = null;
            _sharedRefCount = 0;
        }
    }

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _captureEnabled = true;
    private readonly ManualResetEventSlim _initDone = new(false);
    private readonly AutoResetEvent _request = new(false);
    private readonly ManualResetEventSlim _frameReceivedEvent = new(false);
    private readonly ManualResetEventSlim _unfilteredFrameReceivedEvent = new(false);

    private IntPtr _hostWnd;
    private IntPtr _magWnd;
#pragma warning disable IDE0052 // Keep delegate reference alive to prevent GC collection while native Win32 window is alive
    private MagImageScalingCallback? _callback;   // keep alive
    private static readonly WndProcDelegate SharedWndProc = DefWindowProcW;
    private WndProcDelegate? _wndProc;             // keep alive
#pragma warning restore IDE0052

    private readonly object _filterLock = new();
    private readonly HashSet<IntPtr> _filterHwnds = new();
    private readonly List<Func<IntPtr>> _dynamicExcludeProviders = new();
    private volatile bool _filterListDirty = true;

    private CaptureRequest _activeRequest;

    private readonly record struct CaptureRequest(int X, int Y, int Width, int Height);

    // Completed frame double buffer
    private readonly object _frameLock = new();
    private byte[] _completedBuffer = Array.Empty<byte>();
    private int _completedWidth, _completedHeight;
    private int _completedX, _completedY;
    private bool _hasCompletedFrame;
    private bool _hasUnfilteredFrame;
    private long _completedFrameTicks;
    private int _filterVersion;
    private int _appliedFilterVersion;
    private int _completedFilterVersion;

    public bool IsReady { get; private set; }

    public static bool PrewarmUnfilteredDesktopFrame(TimeSpan timeout)
    {
        MagnifierCaptureSource source = AcquireShared(IntPtr.Zero);
        try
        {
            lock (source._frameLock)
            {
                if (source._hasUnfilteredFrame)
                    return true;
                source._unfilteredFrameReceivedEvent.Reset();
            }

            source._request.Set();
            return source._unfilteredFrameReceivedEvent.Wait(timeout);
        }
        finally
        {
            ReleaseShared(IntPtr.Zero);
        }
    }

    public void AddExcludeHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_filterLock)
        {
            if (_filterHwnds.Add(hwnd))
            {
                _filterListDirty = true;
                _filterVersion++;
            }
        }
        _request.Set();
    }

    public void RegisterExcludeHwndProvider(Func<IntPtr>? provider)
    {
        if (provider == null) return;
        lock (_filterLock)
        {
            if (!_dynamicExcludeProviders.Contains(provider))
                _dynamicExcludeProviders.Add(provider);
        }
    }

    public void UnregisterExcludeHwndProvider(Func<IntPtr>? provider)
    {
        if (provider == null) return;
        lock (_filterLock)
        {
            _dynamicExcludeProviders.Remove(provider);
        }
    }

    private void PollDynamicExcludeProviders()
    {
        lock (_filterLock)
        {
            for (int i = 0; i < _dynamicExcludeProviders.Count; i++)
            {
                IntPtr h;
                try { h = _dynamicExcludeProviders[i](); }
                catch { continue; }
                if (h != IntPtr.Zero && _filterHwnds.Add(h))
                {
                    _filterListDirty = true;
                    _filterVersion++;
                }
            }
        }
    }

    public void RemoveExcludeHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_filterLock)
        {
            if (_filterHwnds.Remove(hwnd))
            {
                _filterListDirty = true;
                _filterVersion++;
            }
        }
        _request.Set();
    }

#pragma warning disable S3776 // High performance filter list compilation requires nested checks
    private void ApplyFiltersIfDirty()
    {
        PollDynamicExcludeProviders();
        if (!_filterListDirty || _magWnd == IntPtr.Zero) return;
        var list = new List<IntPtr>();
        int version;
        lock (_filterLock)
        {
            foreach (var h in _filterHwnds)
            {
                if (h != IntPtr.Zero && IsWindow(h) && !list.Contains(h))
                {
                    // Rely on WDA_EXCLUDEFROMCAPTURE; passing display-affinity layered windows
                    // to MagSetWindowFilterList causes DWM to render black regions.
                    if (GetWindowDisplayAffinity(h, out uint aff) && aff == WDA_EXCLUDEFROMCAPTURE)
                        continue;
                    list.Add(h);
                }
            }
            _filterListDirty = false;
            version = _filterVersion;
        }
        if (_hostWnd != IntPtr.Zero && !list.Contains(_hostWnd)) list.Add(_hostWnd);

        IntPtr[] arr = list.ToArray();
        if (MagSetWindowFilterList(_magWnd, MW_FILTERMODE_EXCLUDE, arr.Length, arr))
        {
            _appliedFilterVersion = version;
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            RuntimeLog.Log(LogTag, $"MagSetWindowFilterList failed (err={err}, count={arr.Length}, version={version}).");
            // Retain the last displayed texture until exclusion is working;
            // capturing the glass itself creates dark recursive feedback.
            _appliedFilterVersion = -1;
            _filterListDirty = true;
        }
    }
#pragma warning restore S3776

    public bool Initialize(IntPtr excludeHwnd)
    {
        if (excludeHwnd != IntPtr.Zero)
        {
            lock (_filterLock) { _filterHwnds.Add(excludeHwnd); }
        }
        _filterListDirty = true;
        _running = true;
        _thread = new Thread(PumpThread)
        {
            IsBackground = true,
            Name = "LiquidGlassMagnifier",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _initDone.Wait(2000);
        return IsReady;
    }

    private ulong _frameCounter;

    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits, out int actualX, out int actualY)
    {
        actualX = x;
        actualY = y;
        if (!IsReady || !_running || destBits == IntPtr.Zero || w <= 0 || h <= 0) return false;

        // Glass windows consume stationary desktop crops; keep shared source fixed
        // to avoid requests overwriting each other or mislabeling callbacks.
        ulong previous;
        lock (_frameLock) previous = _frameCounter;
        _frameReceivedEvent.Reset();
        _request.Set();
        bool ready;
        lock (_frameLock)
            ready = _hasCompletedFrame && _completedFilterVersion == Volatile.Read(ref _filterVersion);
        if (!ready) _frameReceivedEvent.Wait(12);
        else
        {
            lock (_frameLock) ready = _frameCounter != previous;
            if (!ready) _frameReceivedEvent.Wait(2);
        }

        lock (_frameLock)
        {
            if (!HasUsableFrameCore(x, y, w, h)) return false;

            return CopyDesktopCrop(_completedBuffer, _completedWidth, _completedHeight,
                _completedX, _completedY, x, y, w, h, destBits);
        }
    }

    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits) =>
        CaptureInto(x, y, w, h, destBits, out _, out _);

    // Recovery probes must not wait for a callback or interrupt the working backend.
    internal bool HasUsableFrame(int x, int y, int width, int height)
    {
        if (!IsReady || !_running || width <= 0 || height <= 0) return false;
        _request.Set();
        lock (_frameLock)
            return HasUsableFrameCore(x, y, width, height);
    }

    private bool HasUsableFrameCore(int x, int y, int width, int height) =>
        _hasCompletedFrame &&
        _completedFilterVersion == Volatile.Read(ref _filterVersion) &&
        // Requests actively repaint the Magnifier even for a static desktop.
        // A cached image is not proof that callbacks are still delivering frames.
        Environment.TickCount64 - _completedFrameTicks <= 500 &&
        !IsUninitializedZeroCrop(_completedBuffer, _completedWidth, _completedHeight,
            _completedX, _completedY, x, y, width, height);

    private long _magSourceFailCount;
    private long _callbackEnterCount;

    internal string GetDebugStatus(int x, int y, int width, int height)
    {
        int filterCount;
        lock (_filterLock) filterCount = _filterHwnds.Count;
        lock (_frameLock)
        {
            long age = _hasCompletedFrame ? Environment.TickCount64 - _completedFrameTicks : -1;
            bool zeroCrop = _hasCompletedFrame && IsUninitializedZeroCrop(
                _completedBuffer, _completedWidth, _completedHeight,
                _completedX, _completedY, x, y, width, height);
            uint sampleCrop = 0;
            uint sampleCenter = 0;
            int nonZeroCount = 0;
            if (_completedBuffer.Length >= 4 && _completedWidth > 0 && _completedHeight > 0)
            {
                int cx = Math.Clamp(x - _completedX + width / 2, 0, _completedWidth - 1);
                int cy = Math.Clamp(y - _completedY + height / 2, 0, _completedHeight - 1);
                int offCrop = (cy * _completedWidth + cx) * 4;
                if (offCrop + 4 <= _completedBuffer.Length)
                    sampleCrop = BitConverter.ToUInt32(_completedBuffer, offCrop);
                int offCenter = ((_completedHeight / 2) * _completedWidth + (_completedWidth / 2)) * 4;
                if (offCenter + 4 <= _completedBuffer.Length)
                    sampleCenter = BitConverter.ToUInt32(_completedBuffer, offCenter);
                int step = Math.Max(4, (_completedBuffer.Length / 4096) & ~3);
                for (int i = 0; i + 3 < _completedBuffer.Length; i += step)
                {
                    if (_completedBuffer[i] != 0 || _completedBuffer[i + 1] != 0 || _completedBuffer[i + 2] != 0 || _completedBuffer[i + 3] != 0)
                        nonZeroCount++;
                }
            }
            return $"ready={IsReady} running={_running} enabled={_captureEnabled} hasFrame={_hasCompletedFrame} " +
                   $"frames={_frameCounter} cbEnter={Interlocked.Read(ref _callbackEnterCount)} srcFail={Interlocked.Read(ref _magSourceFailCount)} " +
                   $"filters={filterCount} filterVer={_completedFilterVersion}/{Volatile.Read(ref _filterVersion)} (applied={_appliedFilterVersion}) " +
                   $"ageMs={age} zeroCrop={zeroCrop} sampleCrop=0x{sampleCrop:X8} sampleCenter=0x{sampleCenter:X8} nonZero={nonZeroCount} " +
                   $"frame={_completedWidth}x{_completedHeight}@({_completedX},{_completedY}) req=({x},{y} {width}x{height})";
        }
    }

#pragma warning disable S3776 // Win32 message pump STA loop handles device lifecycle, window layout, and DWM sync
    private void PumpThread()
    {
        try
        {
            if (!MagInitialize())
            {
                RuntimeLog.Log(LogTag, "MagInitialize failed.");
                _initDone.Set();
                return;
            }

            IntPtr hInst = GetModuleHandleW(null);
            const string hostClass = "VNotchMagHost";
            _wndProc = SharedWndProc;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                lpszClassName = hostClass
            };
            RegisterClassW(ref wc);   // harmless if already registered

            int screenX = Win32Interop.GetSystemMetrics(76); // SM_XVIRTUALSCREEN
            int screenY = Win32Interop.GetSystemMetrics(77); // SM_YVIRTUALSCREEN
            int screenW = Win32Interop.GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
            int screenH = Win32Interop.GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
            if (screenW <= 0) screenW = Win32Interop.GetSystemMetrics(0); // SM_CXSCREEN
            if (screenH <= 0) screenH = Win32Interop.GetSystemMetrics(1); // SM_CYSCREEN
            int magWindowW = screenW;
            int magWindowH = screenH;

            _hostWnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW,
                hostClass, "VNotchMagHost", WS_POPUP,
                screenX, screenY, magWindowW, magWindowH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hostWnd == IntPtr.Zero) { RuntimeLog.Log(LogTag, "Mag host create failed."); Cleanup(); _initDone.Set(); return; }

            // Set alpha to 1 (virtually invisible, keeps DWM composition active at full monitor refresh rate)
            SetLayeredWindowAttributes(_hostWnd, 0, 1, LWA_ALPHA);
            ShowWindow(_hostWnd, SW_SHOWNA);

            _magWnd = CreateWindowExW(
                0, WC_MAGNIFIER, "VNotchMag", (uint)(WS_CHILD | WS_VISIBLE),
                0, 0, magWindowW, magWindowH, _hostWnd, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_magWnd == IntPtr.Zero) { RuntimeLog.Log(LogTag, "Mag control create failed."); Cleanup(); _initDone.Set(); return; }

            ApplyFiltersIfDirty();

            _callback = ScalingCallback;
            if (!MagSetImageScalingCallback(_magWnd, _callback))
            {
                RuntimeLog.Log(LogTag, "MagSetImageScalingCallback unsupported.");
                Cleanup(); _initDone.Set(); return;
            }

            IsReady = true;
            _initDone.Set();

            bool hostShown = true;

            // High-frequency pump loop: update source rect and query DWM for the freshest frame
            while (_running)
            {
                if (_request.WaitOne(_captureEnabled ? 1 : 25))
                {
                    if (!_running) break;
                    if (!_captureEnabled)
                    {
                        if (hostShown) ShowWindow(_hostWnd, 0);
                        hostShown = false;
                        lock (_frameLock)
                        {
                            // Release desktop-sized BGRA buffers when consumers are idle to prevent
                            // excessive memory retention.
                            _hasCompletedFrame = false;
                            _hasUnfilteredFrame = false;
                            _completedBuffer = Array.Empty<byte>();
                            _frameReceivedEvent.Reset();
                            _unfilteredFrameReceivedEvent.Reset();
                        }
                        continue;
                    }
                    if (!hostShown) ShowWindow(_hostWnd, SW_SHOWNA);
                    hostShown = true;
                    ApplyFiltersIfDirty();

                    // A fixed source also gives callbacks an unambiguous physical
                    // origin. `unclipped` is the scaled destination, not this origin.
                    var req = new CaptureRequest(
                        Win32Interop.GetSystemMetrics(76),
                        Win32Interop.GetSystemMetrics(77),
                        Win32Interop.GetSystemMetrics(78),
                        Win32Interop.GetSystemMetrics(79));

                    if (req.Width > 0 && req.Height > 0)
                    {
                        _activeRequest = req;
                        if (req.Width != magWindowW || req.Height != magWindowH)
                        {
                            magWindowW = req.Width;
                            magWindowH = req.Height;
                            MoveWindow(_hostWnd, req.X, req.Y, magWindowW, magWindowH, false);
                            MoveWindow(_magWnd, 0, 0, magWindowW, magWindowH, false);
                        }

                        var rect = new Win32Interop.RECT
                        {
                            Left = req.X,
                            Top = req.Y,
                            Right = req.X + req.Width,
                            Bottom = req.Y + req.Height
                        };

                        // Refresh source on every capture request using fixed physical bounds to
                        // ensure fresh desktop frames without jitter in the shared texture.
                        if (!MagSetWindowSource(_magWnd, rect))
                        {
                            Interlocked.Increment(ref _magSourceFailCount);
                            continue;
                        }

                        InvalidateRect(_magWnd, IntPtr.Zero, false);
                        UpdateWindow(_magWnd);
                    }
                }

                DrainMessages();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogTag, $"Magnifier pump exception: {ex.Message}");
            _initDone.Set();
        }
        finally
        {
            Cleanup();
        }
    }
#pragma warning restore S3776

    internal static bool IsCompleteFrame(
        int requestedWidth,
        int requestedHeight,
        int receivedWidth,
        int receivedHeight,
        int receivedStride,
        int bufferLength)
    {
        if (requestedWidth <= 0 || requestedHeight <= 0 ||
            receivedWidth < requestedWidth || receivedHeight < requestedHeight)
            return false;

        long rowBytes = (long)requestedWidth * 4;
        if (receivedStride < rowBytes) return false;

        long requiredBytes = ((long)requestedHeight - 1) * receivedStride + rowBytes;
        return requiredBytes <= bufferLength;
    }

    private static void DrainMessages()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

#pragma warning disable S3776, S6640, S107 // High performance unmanaged Magnification callback with fixed native signature
    private unsafe bool ScalingCallback(IntPtr hwnd, IntPtr srcdata, MAGIMAGEHEADER srcheader,
        IntPtr destdata, MAGIMAGEHEADER destheader,
        Win32Interop.RECT unclipped, Win32Interop.RECT clipped, IntPtr dirty)
    {
        try
        {
            Interlocked.Increment(ref _callbackEnterCount);
            if (srcdata == IntPtr.Zero || !_captureEnabled) return false;

            int w = (int)srcheader.width;
            int rows = (int)srcheader.height;
            int srcStride = (int)srcheader.stride;
            if (w <= 0 || rows <= 0 || srcStride <= 0 || rows > 16384 || srcStride > 1 << 18) return false;
            ulong available = srcheader.cbSize.ToUInt64();
            if (srcheader.offset > available ||
                !IsCompleteFrame(w, rows, w, rows, srcStride,
                    (int)Math.Min(int.MaxValue, available - srcheader.offset)) ||
                w < _activeRequest.Width || rows < _activeRequest.Height)
            {
                RuntimeLog.Log(LogTag, $"ScalingCallback rejected: w={w} rows={rows} stride={srcStride} offset={srcheader.offset} cbSize={available} req={_activeRequest.Width}x{_activeRequest.Height}");
                return false;
            }

            int dstStride = checked(w * 4);
            int needed = checked(dstStride * rows);

            byte* src = (byte*)srcdata + srcheader.offset;

            lock (_frameLock)
            {
                if (!_captureEnabled) return false;
                if (_completedBuffer.Length < needed)
                    _completedBuffer = new byte[needed];

                fixed (byte* dstBase = _completedBuffer)
                {
                    if (srcStride == dstStride)
                    {
                        Buffer.MemoryCopy(src, dstBase, _completedBuffer.Length, (long)needed);
                    }
                    else
                    {
                        for (int row = 0; row < rows; row++)
                        {
                            byte* s = src + row * srcStride;
                            byte* d = dstBase + row * dstStride;
                            Buffer.MemoryCopy(s, d, dstStride, dstStride);
                        }
                    }
                }

                _completedWidth = w;
                _completedHeight = rows;
                _completedX = _activeRequest.X;
                _completedY = _activeRequest.Y;
                _completedFilterVersion = _appliedFilterVersion;
                _hasCompletedFrame = true;
                _completedFrameTicks = Environment.TickCount64;
                _frameCounter++;

                if (_appliedFilterVersion == 0)
                {
                    _hasUnfilteredFrame = true;
                    _unfilteredFrameReceivedEvent.Set();
                }
            }

            _frameReceivedEvent.Set();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
#pragma warning restore S3776, S6640, S107

#pragma warning disable S6640, S107 // High performance unmanaged memory blitting
    internal static unsafe bool CopyDesktopCrop(byte[] source, int sourceWidth, int sourceHeight,
        int desktopX, int desktopY, int x, int y, int width, int height, IntPtr destination)
    {
        if (destination == IntPtr.Zero || sourceWidth <= 0 || sourceHeight <= 0 ||
            width <= 0 || height <= 0 || (long)sourceWidth * sourceHeight * 4 > source.Length)
            return false;

        // Extend edge pixels when a sampling margin crosses the virtual desktop.
        // No transparent/uninitialized pixels can enter a valid glass texture.
        fixed (byte* bytes = source)
        {
            uint* src = (uint*)bytes;
            uint* dst = (uint*)destination;
            long offsetX = (long)x - desktopX;

            long offsetY = (long)y - desktopY;
            for (int row = 0; row < height; row++)
            {
                int sy = (int)Math.Clamp(offsetY + row, 0, sourceHeight - 1);
                if (offsetX >= 0 && offsetX + width <= sourceWidth)
                {
                    Buffer.MemoryCopy(src + sy * sourceWidth + (int)offsetX,
                        dst + row * width, (long)width * 4, (long)width * 4);
                }
                else
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sx = (int)Math.Clamp(offsetX + col, 0, sourceWidth - 1);
                        dst[row * width + col] = src[sy * sourceWidth + sx];
                    }
                }
            }
        }
        return true;
    }
#pragma warning restore S6640, S107

#pragma warning disable S107
    private static bool IsUninitializedZeroCrop(byte[] source, int sourceWidth, int sourceHeight,
        int desktopX, int desktopY, int x, int y, int width, int height)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            (long)sourceWidth * sourceHeight * 4 > source.Length)
            return true;

        // Only reject when DWM returned uninitialized all-zero pixels (B=0, G=0, R=0, A=0).
        // Opaque dark/black desktop content has A=255 and must never be rejected.
        const int samplesPerAxis = 6;
        for (int row = 0; row < samplesPerAxis; row++)
        {
            int sy = Math.Clamp(y - desktopY + (height - 1) * row / (samplesPerAxis - 1), 0, sourceHeight - 1);
            for (int column = 0; column < samplesPerAxis; column++)
            {
                int sx = Math.Clamp(x - desktopX + (width - 1) * column / (samplesPerAxis - 1), 0, sourceWidth - 1);
                int offset = (sy * sourceWidth + sx) * 4;
                if (source[offset] > 0 || source[offset + 1] > 0 || source[offset + 2] > 0 || source[offset + 3] > 0)
                    return false;
            }
        }

        return true;
    }
#pragma warning restore S107

    private void Cleanup()
    {
        IsReady = false;
        try
        {
            if (_magWnd != IntPtr.Zero) { DestroyWindow(_magWnd); _magWnd = IntPtr.Zero; }
            if (_hostWnd != IntPtr.Zero) { DestroyWindow(_hostWnd); _hostWnd = IntPtr.Zero; }
            MagUninitialize();
        }
        catch (Exception)
        {
            // Window destruction errors on cleanup are safely ignored.
        }
    }

    public void Dispose()
    {
        _running = false;
        IsReady = false;
        _request.Set();
        _frameReceivedEvent.Set();
        try
        {
            _thread?.Join(500);
        }
        catch (Exception)
        {
            // Thread join timeout during dispose is safely ignored.
        }
        _thread = null;
        _callback = null;
        _wndProc = null;
        try
        {
            _initDone.Dispose();
        }
        catch (Exception)
        {
            // Event handle cleanup errors on dispose are safely ignored.
        }
        try
        {
            _request.Dispose();
        }
        catch (Exception)
        {
            // Event handle cleanup errors on dispose are safely ignored.
        }
        try
        {
            _frameReceivedEvent.Dispose();
        }
        catch (Exception)
        {
            // Event handle cleanup errors on dispose are safely ignored.
        }
        try
        {
            _unfilteredFrameReceivedEvent.Dispose();
        }
        catch (Exception)
        {
            // Event handle cleanup errors on dispose are safely ignored.
        }
    }
}
