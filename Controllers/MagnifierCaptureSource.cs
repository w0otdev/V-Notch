using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class MagnifierCaptureSource : IDisposable
{
    // Set true if colours come out with red/blue swapped on a given machine.
    private static readonly bool SwapRedBlue = false;

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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MagImageScalingCallback(
        IntPtr hwnd, IntPtr srcdata, MAGIMAGEHEADER srcheader,
        IntPtr destdata, MAGIMAGEHEADER destheader,
        Win32Interop.RECT unclipped, Win32Interop.RECT clipped, IntPtr dirty);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(MagDll)] private static extern bool MagInitialize();
    [DllImport(MagDll)] private static extern bool MagUninitialize();
    [DllImport(MagDll)] private static extern bool MagSetWindowSource(IntPtr hwnd, Win32Interop.RECT rect);
    [DllImport(MagDll)] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int dwFilterMode, int count, IntPtr[] pHWND);
    [DllImport(MagDll)] private static extern bool MagSetImageScalingCallback(IntPtr hwnd, MagImageScalingCallback callback);

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
                try { _sharedInstance?.Dispose(); } catch { }
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
            try { _sharedInstance?.Dispose(); } catch { }
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
    private MagImageScalingCallback? _callback;   // keep alive
    private static readonly WndProcDelegate SharedWndProc = DefWindowProcW;
    private WndProcDelegate? _wndProc;             // keep alive

    private readonly object _filterLock = new();
    private readonly HashSet<IntPtr> _filterHwnds = new();
    private volatile bool _filterListDirty = true;

    private int _magWindowW = 2560;
    private int _magWindowH = 1600;

    private CaptureRequest _activeRequest;

    private readonly record struct CaptureRequest(int X, int Y, int Width, int Height);

    // Completed frame double buffer
    private readonly object _frameLock = new();
    private byte[] _completedBuffer = Array.Empty<byte>();
    private int _completedWidth, _completedHeight;
    private int _completedX, _completedY;
    private bool _hasCompletedFrame;
    // A desktop frame captured before an overlay HWND is added to the Magnifier
    // exclusion list. On some WPF layered-window configurations the excluded
    // rectangle is returned as opaque black instead of the desktop behind it.
    private byte[] _unfilteredBuffer = Array.Empty<byte>();
    private int _unfilteredWidth, _unfilteredHeight;
    private int _unfilteredX, _unfilteredY;
    private bool _hasUnfilteredFrame;
    private ulong _frameCounter;
    private int _filterVersion;
    private int _appliedFilterVersion;
    private int _completedFilterVersion;

    public bool IsReady { get; private set; }

    /// <summary>
    /// Captures one desktop frame before any glass overlay is registered as an
    /// excluded HWND. That frame is used only when a later excluded crop is
    /// demonstrably all black.
    /// </summary>
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

    private void ApplyFiltersIfDirty()
    {
        if (!_filterListDirty || _magWnd == IntPtr.Zero) return;
        var list = new List<IntPtr>();
        int version;
        lock (_filterLock)
        {
            foreach (var h in _filterHwnds)
            {
                if (h != IntPtr.Zero && !list.Contains(h))
                {
                    // If the window already has WDA_EXCLUDEFROMCAPTURE, DWM's compositor
                    // excludes it cleanly. Passing a WS_EX_LAYERED window with display
                    // affinity to MagSetWindowFilterList causes DWM to paint the region as solid black.
                    if (GetWindowDisplayAffinity(h, out uint aff) && aff == WDA_EXCLUDEFROMCAPTURE)
                        continue;
                    list.Add(h);
                }
            }
            _filterListDirty = false;
            version = _filterVersion;
        }
        if (_hostWnd != IntPtr.Zero && !list.Contains(_hostWnd)) list.Add(_hostWnd);
        if (_magWnd != IntPtr.Zero && !list.Contains(_magWnd)) list.Add(_magWnd);

        IntPtr[] arr = list.ToArray();
        if (MagSetWindowFilterList(_magWnd, MW_FILTERMODE_EXCLUDE, arr.Length, arr))
            _appliedFilterVersion = version;
        else
        {
            // Retain the last displayed texture until exclusion is working;
            // capturing the glass itself creates dark recursive feedback.
            _appliedFilterVersion = -1;
            _filterListDirty = true;
        }
    }

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

    /// <summary>
    /// Low-latency synchronized capture: requests DWM composite update and copies the
    /// freshest available frame with microsecond synchronization to eliminate 1-2 frame lag.
    /// </summary>
    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits, out int actualX, out int actualY)
    {
        actualX = x;
        actualY = y;
        if (!IsReady || !_running || destBits == IntPtr.Zero || w <= 0 || h <= 0) return false;

        // All glass windows consume crops of the same stationary desktop frame.
        // Moving/resizing a shared magnifier source lets one consumer overwrite
        // another's request and mislabels delayed callbacks during morphs.
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
            if (!_hasCompletedFrame ||
                _completedFilterVersion != Volatile.Read(ref _filterVersion)) return false;

            if (IsEffectivelyBlackCrop(_completedBuffer, _completedWidth, _completedHeight,
                    _completedX, _completedY, x, y, w, h))
            {
                if (_hasUnfilteredFrame &&
                    !IsEffectivelyBlackCrop(_unfilteredBuffer, _unfilteredWidth, _unfilteredHeight,
                        _unfilteredX, _unfilteredY, x, y, w, h))
                {
                    return CopyDesktopCrop(_unfilteredBuffer, _unfilteredWidth, _unfilteredHeight,
                        _unfilteredX, _unfilteredY, x, y, w, h, destBits);
                }
                return false;
            }

            return CopyDesktopCrop(_completedBuffer, _completedWidth, _completedHeight,
                _completedX, _completedY, x, y, w, h, destBits);
        }
    }

    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits) =>
        CaptureInto(x, y, w, h, destBits, out _, out _);

    private void PumpThread()
    {
        try
        {
            if (!MagInitialize())
            {
                RuntimeLog.Log("LIQUIDGLASS", "MagInitialize failed.");
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
            _magWindowW = screenW;
            _magWindowH = screenH;

            _hostWnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW,
                hostClass, "VNotchMagHost", WS_POPUP,
                screenX, screenY, _magWindowW, _magWindowH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hostWnd == IntPtr.Zero) { RuntimeLog.Log("LIQUIDGLASS", "Mag host create failed."); Cleanup(); _initDone.Set(); return; }

            // Set alpha to 1 (virtually invisible, keeps DWM composition active at full monitor refresh rate)
            SetLayeredWindowAttributes(_hostWnd, 0, 1, LWA_ALPHA);
            ShowWindow(_hostWnd, SW_SHOWNA);

            _magWnd = CreateWindowExW(
                0, WC_MAGNIFIER, "VNotchMag", (uint)(WS_CHILD | WS_VISIBLE),
                0, 0, _magWindowW, _magWindowH, _hostWnd, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_magWnd == IntPtr.Zero) { RuntimeLog.Log("LIQUIDGLASS", "Mag control create failed."); Cleanup(); _initDone.Set(); return; }

            ApplyFiltersIfDirty();

            _callback = ScalingCallback;
            if (!MagSetImageScalingCallback(_magWnd, _callback))
            {
                RuntimeLog.Log("LIQUIDGLASS", "MagSetImageScalingCallback unsupported.");
                Cleanup(); _initDone.Set(); return;
            }

            IsReady = true;
            _initDone.Set();

            Win32Interop.RECT lastRect = default;
            bool hasConfiguredRect = false;
            bool hostShown = true;

            // High-frequency pump loop: update source rect and query DWM for the freshest frame
            while (_running)
            {
                if (_request.WaitOne(1))
                {
                    if (!_running) break;
                    if (!_captureEnabled)
                    {
                        if (hostShown) ShowWindow(_hostWnd, 0);
                        hostShown = false;
                        lock (_frameLock) _hasCompletedFrame = false;
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
                        if (req.Width != _magWindowW || req.Height != _magWindowH)
                        {
                            _magWindowW = req.Width;
                            _magWindowH = req.Height;
                            MoveWindow(_hostWnd, req.X, req.Y, _magWindowW, _magWindowH, false);
                            MoveWindow(_magWnd, 0, 0, _magWindowW, _magWindowH, false);
                        }

                        var rect = new Win32Interop.RECT
                        {
                            Left = req.X,
                            Top = req.Y,
                            Right = req.X + req.Width,
                            Bottom = req.Y + req.Height
                        };

                        // Avoid resetting DWM magnifier state if the rect is unchanged
                        if (!hasConfiguredRect ||
                            rect.Left != lastRect.Left || rect.Top != lastRect.Top ||
                            rect.Right != lastRect.Right || rect.Bottom != lastRect.Bottom)
                        {
                            if (!MagSetWindowSource(_magWnd, rect)) continue;
                            lastRect = rect;
                            hasConfiguredRect = true;
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
            RuntimeLog.Log("LIQUIDGLASS", $"Magnifier pump exception: {ex.Message}");
            _initDone.Set();
        }
        finally
        {
            Cleanup();
        }
    }

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

    private bool DrainMessages()
    {
        bool any = false;
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            any = true;
        }
        return any;
    }

    private unsafe bool ScalingCallback(IntPtr hwnd, IntPtr srcdata, MAGIMAGEHEADER srcheader,
        IntPtr destdata, MAGIMAGEHEADER destheader,
        Win32Interop.RECT unclipped, Win32Interop.RECT clipped, IntPtr dirty)
    {
        try
        {
            if (srcdata == IntPtr.Zero) return false;

            int w = (int)srcheader.width;
            int rows = (int)srcheader.height;
            int srcStride = (int)srcheader.stride;
            if (w <= 0 || rows <= 0 || srcStride <= 0 || rows > 16384 || srcStride > 1 << 18) return false;
            ulong available = srcheader.cbSize.ToUInt64();
            if (srcheader.offset > available ||
                !IsCompleteFrame(w, rows, w, rows, srcStride,
                    (int)Math.Min(int.MaxValue, available - srcheader.offset)) ||
                w < _activeRequest.Width || rows < _activeRequest.Height)
                return false;

            int dstStride = checked(w * 4);
            int needed = checked(dstStride * rows);

            byte* src = (byte*)srcdata + srcheader.offset;

            lock (_frameLock)
            {
                if (_completedBuffer.Length < needed)
                    _completedBuffer = new byte[needed];

                fixed (byte* dstBase = _completedBuffer)
                {
                    if (!SwapRedBlue && srcStride == dstStride)
                    {
                        Buffer.MemoryCopy(src, dstBase, _completedBuffer.Length, (long)needed);
                    }
                    else
                    {
                        for (int row = 0; row < rows; row++)
                        {
                            byte* s = src + row * srcStride;
                            byte* d = dstBase + row * dstStride;
                            if (!SwapRedBlue)
                            {
                                Buffer.MemoryCopy(s, d, dstStride, dstStride);
                            }
                            else
                            {
                                for (int p = 0; p < w; p++)
                                {
                                    int o = p << 2;
                                    d[o + 0] = s[o + 2];
                                    d[o + 1] = s[o + 1];
                                    d[o + 2] = s[o + 0];
                                    d[o + 3] = s[o + 3];
                                }
                            }
                        }
                    }
                }

                _completedWidth = w;
                _completedHeight = rows;
                _completedX = _activeRequest.X;
                _completedY = _activeRequest.Y;
                _completedFilterVersion = _appliedFilterVersion;
                _hasCompletedFrame = true;
                _frameCounter++;

                if (_appliedFilterVersion == 0)
                {
                    if (_unfilteredBuffer.Length < needed)
                        _unfilteredBuffer = new byte[needed];
                    Buffer.BlockCopy(_completedBuffer, 0, _unfilteredBuffer, 0, needed);
                    _unfilteredWidth = w;
                    _unfilteredHeight = rows;
                    _unfilteredX = _activeRequest.X;
                    _unfilteredY = _activeRequest.Y;
                    _hasUnfilteredFrame = true;
                    _unfilteredFrameReceivedEvent.Set();
                }
            }

            _frameReceivedEvent.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

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

    private static bool IsEffectivelyBlackCrop(byte[] source, int sourceWidth, int sourceHeight,
        int desktopX, int desktopY, int x, int y, int width, int height)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            (long)sourceWidth * sourceHeight * 4 > source.Length)
            return true;

        const int samplesPerAxis = 6;
        for (int row = 0; row < samplesPerAxis; row++)
        {
            int sy = Math.Clamp(y - desktopY + (height - 1) * row / (samplesPerAxis - 1), 0, sourceHeight - 1);
            for (int column = 0; column < samplesPerAxis; column++)
            {
                int sx = Math.Clamp(x - desktopX + (width - 1) * column / (samplesPerAxis - 1), 0, sourceWidth - 1);
                int offset = (sy * sourceWidth + sx) * 4;
                if (source[offset] > 8 || source[offset + 1] > 8 || source[offset + 2] > 8)
                    return false;
            }
        }

        return true;
    }

    private void Cleanup()
    {
        IsReady = false;
        try
        {
            if (_magWnd != IntPtr.Zero) { DestroyWindow(_magWnd); _magWnd = IntPtr.Zero; }
            if (_hostWnd != IntPtr.Zero) { DestroyWindow(_hostWnd); _hostWnd = IntPtr.Zero; }
            MagUninitialize();
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _running = false;
        IsReady = false;
        _request.Set();
        _frameReceivedEvent.Set();
        try { _thread?.Join(500); } catch { /* ignore */ }
        _thread = null;
        _callback = null;
        _wndProc = null;
        try { _frameReceivedEvent.Dispose(); } catch { }
        try { _unfilteredFrameReceivedEvent.Dispose(); } catch { }
    }
}
