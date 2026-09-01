using System;
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
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _initDone = new(false);
    private readonly AutoResetEvent _request = new(false);
    private readonly ManualResetEventSlim _frameReceivedEvent = new(false);

    private IntPtr _excludeHwnd;
    private IntPtr _hostWnd;
    private IntPtr _magWnd;
    private MagImageScalingCallback? _callback;   // keep alive
    private WndProcDelegate? _wndProc;             // keep alive

    private const int MagWindowW = 1600;
    private const int MagWindowH = 700;

    private readonly object _requestSync = new();
    private CaptureRequest _pendingRequest;
    private CaptureRequest _activeRequest;

    private readonly record struct CaptureRequest(int X, int Y, int Width, int Height);

    // Completed frame double buffer
    private readonly object _frameLock = new();
    private byte[] _completedBuffer = Array.Empty<byte>();
    private int _completedWidth, _completedHeight;
    private int _completedX, _completedY;
    private bool _hasCompletedFrame;
    private ulong _frameCounter;
    private ulong _lastServedFrame;

    public bool IsReady { get; private set; }

    public bool Initialize(IntPtr excludeHwnd)
    {
        _excludeHwnd = excludeHwnd;
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

        bool isFirstFrame = !_hasCompletedFrame;
        bool rectChanged;
        lock (_requestSync)
        {
            rectChanged = _pendingRequest.X != x || _pendingRequest.Y != y ||
                          _pendingRequest.Width != w || _pendingRequest.Height != h;
            _pendingRequest = new CaptureRequest(x, y, w, h);
            _request.Set();
        }

        if (isFirstFrame || rectChanged)
        {
            _frameReceivedEvent.Reset();
            _frameReceivedEvent.Wait(isFirstFrame ? 60 : 15);
        }
        else
        {
            // If the latest frame was already served, wait up to 3ms for DWM's next VSync callback
            ulong curFrame;
            lock (_frameLock)
            {
                curFrame = _frameCounter;
            }
            if (curFrame <= _lastServedFrame)
            {
                _frameReceivedEvent.Wait(3);
            }
        }

        lock (_frameLock)
        {
            if (!_hasCompletedFrame || _completedWidth != w || _completedHeight != h)
                return false;

            _lastServedFrame = _frameCounter;
            actualX = _completedX;
            actualY = _completedY;
            return CopyToDest(destBits, w, h);
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
            _wndProc = DefWindowProcW;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                lpszClassName = hostClass
            };
            RegisterClassW(ref wc);   // harmless if already registered

            _hostWnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW,
                hostClass, "VNotchMagHost", WS_POPUP,
                0, 0, MagWindowW, MagWindowH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hostWnd == IntPtr.Zero) { RuntimeLog.Log("LIQUIDGLASS", "Mag host create failed."); Cleanup(); _initDone.Set(); return; }

            // Set alpha to 1 (virtually invisible, keeps DWM composition active at full monitor refresh rate)
            SetLayeredWindowAttributes(_hostWnd, 0, 1, LWA_ALPHA);
            ShowWindow(_hostWnd, SW_SHOWNA);

            _magWnd = CreateWindowExW(
                0, WC_MAGNIFIER, "VNotchMag", (uint)(WS_CHILD | WS_VISIBLE),
                0, 0, MagWindowW, MagWindowH, _hostWnd, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_magWnd == IntPtr.Zero) { RuntimeLog.Log("LIQUIDGLASS", "Mag control create failed."); Cleanup(); _initDone.Set(); return; }

            var exclude = new[] { _excludeHwnd, _hostWnd, _magWnd };
            MagSetWindowFilterList(_magWnd, MW_FILTERMODE_EXCLUDE, exclude.Length, exclude);

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

            // High-frequency pump loop: update source rect and query DWM for the freshest frame
            while (_running)
            {
                if (_request.WaitOne(1))
                {
                    if (!_running) break;
                    CaptureRequest req;
                    lock (_requestSync)
                    {
                        req = _pendingRequest;
                    }

                    if (req.Width > 0 && req.Height > 0)
                    {
                        _activeRequest = req;
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
                            MagSetWindowSource(_magWnd, rect);
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
            if (w <= 0 || rows <= 0 || srcStride <= 0 || rows > 4096 || srcStride > 1 << 18) return false;

            int dstStride = checked(w * 4);
            int needed = checked(dstStride * rows);

            byte* src = (byte*)srcdata;

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
                _completedX = unclipped.Left;
                _completedY = unclipped.Top;
                _hasCompletedFrame = true;
                _frameCounter++;
            }

            _frameReceivedEvent.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool CopyToDest(IntPtr dest, int w, int h)
    {
        byte[] src;
        lock (_frameLock)
        {
            if (!_hasCompletedFrame || _completedWidth != w || _completedHeight != h) return false;
            src = _completedBuffer;
        }

        long bytes = (long)w * h * 4;
        if (src.Length < bytes) return false;

        fixed (byte* srcBase = src)
            Buffer.MemoryCopy(srcBase, (void*)dest, bytes, bytes);

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
        _frameReceivedEvent.Dispose();
    }
}
