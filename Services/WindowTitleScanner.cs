using System.Runtime.InteropServices;
using System.Threading;

using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

public interface IWindowTitleScanner
{
    List<string> GetAllWindowTitles(bool isThrottled);
    bool HasYouTubeWindowMatch(string track, bool isThrottled) =>
        PlatformDetector.HasReliableWindowMatch(GetAllWindowTitles(isThrottled), track, "YouTube");
    MediaWindowTitle? FindMediaWindowTitle(bool recovery, bool isThrottled)
    {
        foreach (string title in GetAllWindowTitles(isThrottled))
            if (WindowTitleScanner.ParseMediaWindowTitle(title, recovery) is { } match) return match;
        return null;
    }
    string? TryGetBrowserUrl();
    string? TryGetMediaUrlFromAnyBrowser();

    bool IsSpotifyWebPlayerOpen();
    bool IsPipActive(string? processName = null);
    bool TryGetPipWindow(out IntPtr pipHwnd, out string pipTitle, string? processName = null);

    void InvalidateUrlCaches();
}

public sealed partial class WindowTitleScanner : IWindowTitleScanner, IDisposable
{
    private const string HttpsPrefix = "https://";
    private const string SpotifyWebPlayerHost = "open.spotify.com";


    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_wtscanner_matches_platform_title(
        ReadOnlySpan<ushort> title, int titleLen, out byte outMatches);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_wtscanner_is_browser_process(
        ReadOnlySpan<ushort> procName, int procLen, out byte outIsBrowser);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_wtscanner_is_media_url(
        ReadOnlySpan<ushort> url, int urlLen, out byte outIsMedia);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct VnVisibleWindowEntry
    {
        public long Hwnd;
        public uint ProcessId;
        public int TitleLen;
        public byte MatchesPlatformTitle;
        public fixed char Title[256];
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_wtscanner_collect_visible_windows(
        uint excludePid,
        byte onlyPlatformTitles,
        Span<VnVisibleWindowEntry> outEntries,
        int entriesCap,
        out int outCount);

    private readonly object _cacheLock = new();
    // All title enumerations are serialized by _cacheLock.
    private char[] _titleBuffer = new char[256];
    private List<string> _cachedWindowTitles = new();
    private DateTime _lastWindowEnumTime = DateTime.MinValue;
    private bool _titleNotificationsActive;

    internal Action<Func<IntPtr, bool>>? WindowEnumerator { get; set; }

    internal void SetTitleNotificationsActive(bool active)
    {
        lock (_cacheLock) { _titleNotificationsActive = active; _lastWindowEnumTime = _lastMediaTitleScan = _lastRecoveryTitleScan = _lastYouTubeMatchScan = DateTime.MinValue; }
    }

    internal void InvalidateWindowTitles()
    {
        lock (_cacheLock) _lastWindowEnumTime = _lastMediaTitleScan = _lastRecoveryTitleScan = _lastYouTubeMatchScan = DateTime.MinValue;
    }

    public List<string> GetAllWindowTitles(bool isThrottled)
    {
        lock (_cacheLock)
        {
            // Throttled players need fallback detection, but full desktop scans
            // should back off instead of increasing their polling frequency.
            int cacheDurationMs = _titleNotificationsActive ? 30_000 : isThrottled ? 2000 : 700;
            if ((DateTime.UtcNow - _lastWindowEnumTime).TotalMilliseconds < cacheDurationMs)
            {
                return new List<string>(_cachedWindowTitles);
            }

            var titles = new List<string>();
            uint currentPid = (uint)Environment.ProcessId;

            if (WindowEnumerator != null)
            {
                WindowEnumerator(hWnd => TryCollectForeignVisibleWindowTitle(hWnd, currentPid, titles));
            }
            else if (!TryCollectPlatformWindowTitlesNative(currentPid, titles))
            {
                EnumWindows((hWnd, _) => TryCollectForeignVisibleWindowTitle(hWnd, currentPid, titles), IntPtr.Zero);
            }

            _cachedWindowTitles = titles;
            _lastWindowEnumTime = DateTime.UtcNow;
            return new List<string>(titles);
        }
    }

    private bool TryCollectForeignVisibleWindowTitle(IntPtr hWnd, uint excludePid, List<string> titles)
    {
        if (!IsWindowVisible(hWnd))
            return true;

        // USER32 reads foreign-process captions without WM_GETTEXT.
        // Our own windows can synchronously message a UI thread while
        // this background scanner holds the cache lock.
        GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == 0 || processId == excludePid)
            return true;

        AddMatchingWindowTitle(hWnd, titles);
        return true;
    }

    private static unsafe bool TryCollectPlatformWindowTitlesNative(uint excludePid, List<string> titles)
    {
        Span<VnVisibleWindowEntry> stackEntries = stackalloc VnVisibleWindowEntry[32];
        int status = vn_wtscanner_collect_visible_windows(excludePid, 1, stackEntries, stackEntries.Length, out int count);
        if (status == 0)
        {
            AppendWindowTitles(stackEntries[..Math.Min(count, stackEntries.Length)], titles);
            return true;
        }
        if (status == -2 && count > stackEntries.Length)
        {
            var heapEntries = new VnVisibleWindowEntry[count];
            status = vn_wtscanner_collect_visible_windows(excludePid, 1, heapEntries, heapEntries.Length, out count);
            if (status == 0)
            {
                AppendWindowTitles(heapEntries.AsSpan(0, Math.Min(count, heapEntries.Length)), titles);
                return true;
            }
        }
        return false;
    }

    private static unsafe void AppendWindowTitles(ReadOnlySpan<VnVisibleWindowEntry> entries, List<string> titles)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            int len = Math.Clamp(entries[i].TitleLen, 0, 255);
            if (len <= 0) continue;
            fixed (char* ptr = entries[i].Title)
            {
                titles.Add(new string(ptr, 0, len));
            }
        }
    }

    private static bool TryCollectVisibleWindowsNative(uint excludePid, out VnVisibleWindowEntry[] entries, out int count)
    {
        entries = Array.Empty<VnVisibleWindowEntry>();
        count = 0;
        var buffer = new VnVisibleWindowEntry[64];
        int status = vn_wtscanner_collect_visible_windows(excludePid, 0, buffer, buffer.Length, out int actual);
        if (status == 0)
        {
            entries = buffer;
            count = Math.Min(actual, buffer.Length);
            return true;
        }
        if (status == -2 && actual > buffer.Length)
        {
            buffer = new VnVisibleWindowEntry[actual];
            status = vn_wtscanner_collect_visible_windows(excludePid, 0, buffer, buffer.Length, out actual);
            if (status == 0)
            {
                entries = buffer;
                count = Math.Min(actual, buffer.Length);
                return true;
            }
        }
        return false;
    }

    private unsafe void AddMatchingWindowTitle(IntPtr hWnd, List<string> titles)
    {
        int length = GetWindowTextLength(hWnd);
        if (length <= 0) return;

        if (_titleBuffer.Length <= length)
            Array.Resize(ref _titleBuffer, Math.Max(length + 1, _titleBuffer.Length * 2));

        int copied;
        fixed (char* buffer = _titleBuffer)
            copied = GetWindowText(hWnd, buffer, _titleBuffer.Length);
        if (copied <= 0) return;

        ReadOnlySpan<char> title = _titleBuffer.AsSpan(0, copied);
        if (MatchesPlatformTitle(title))
            titles.Add(new string(title));
    }

    internal static bool MatchesPlatformTitle(ReadOnlySpan<char> title)
    {
        return vn_wtscanner_matches_platform_title(MemoryMarshal.Cast<char, ushort>(title), title.Length, out byte matches) == 0 && matches != 0;
    }



    private string? _cachedBrowserUrl;
    private DateTime _lastBrowserUrlTime = DateTime.MinValue;

    private string? _cachedAnyBrowserMediaUrl;
    private DateTime _lastAnyBrowserMediaUrlTime = DateTime.MinValue;

    private bool _cachedSpotifyWebPlayerOpen;
    private DateTime _lastSpotifyWebPlayerTime = DateTime.MinValue;

    // SettingsService and the settings UI publish consent after a successful load
    // or a user change. Merely using this type never creates files or watcher handles.
    private static int _inspectionAllowed;
    private static int _inspectionVersion;
    private readonly Func<bool>? _browserInspectionAllowed;
    private int _observedInspectionVersion = -1;

    public WindowTitleScanner() { }

    internal WindowTitleScanner(Func<bool> browserInspectionAllowed) =>
        _browserInspectionAllowed = browserInspectionAllowed ?? throw new ArgumentNullException(nameof(browserInspectionAllowed));

    private bool InspectionAllowed => _browserInspectionAllowed?.Invoke() ?? IsBrowserUrlInspectionAllowed();

    public static void UpdateInspectionAllowed(bool allowed)
    {
        if (Interlocked.Exchange(ref _inspectionAllowed, allowed ? 1 : 0) != (allowed ? 1 : 0))
            Interlocked.Increment(ref _inspectionVersion);
    }

    public static void InvalidateInspectionAllowed()
    {
        UpdateInspectionAllowed(false);
    }

    public static bool IsBrowserUrlInspectionAllowed() => Volatile.Read(ref _inspectionAllowed) == 1;

    private bool CanInspectBrowser()
    {
        int version = _browserInspectionAllowed == null ? Volatile.Read(ref _inspectionVersion) : 0;
        bool allowed = InspectionAllowed;
        lock (_cacheLock)
        {
            if (_observedInspectionVersion != version || !allowed)
            {
                InvalidateUrlCaches();
                _observedInspectionVersion = version;
            }
            return allowed && !_disposed;
        }
    }

    private int _generation;
    private readonly AutoResetEvent _wakeWorkerEvent = new(false);
    private Thread? _workerThread;
    private volatile bool _disposed;

    private bool _browserUrlScanActive;
    private int _browserUrlScanGen;
    private bool _pendingBrowserUrlScan;

    private bool _anyBrowserScanActive;
    private int _anyBrowserScanGen;
    private bool _pendingAnyBrowserScan;

    private bool _spotifyScanActive;
    private int _spotifyScanGen;
    private bool _pendingSpotifyScan;

    internal Func<string?> BrowserUrlExtractor { get; set; } = ExtractBrowserUrlCore;
    internal Func<string?> AnyBrowserMediaUrlExtractor { get; set; } = ExtractMediaUrlFromAllBrowserWindows;
    internal delegate bool PipWindowFinderDelegate(out IntPtr pipHwnd, out string pipTitle, string? processName);

    internal Func<bool> SpotifyWebPlayerDetector { get; set; } = DetectSpotifyWebPlayer;
    internal PipWindowFinderDelegate PipWindowFinder { get; set; } = static (out IntPtr hwnd, out string title, string? proc) => PipDetector.TryFindPipWindow(out hwnd, out title, proc);

    internal bool IsWorkerStarted => _workerThread != null;
    internal bool IsWorkerAlive => _workerThread?.IsAlive == true;

    // Called under _cacheLock. Title-only polling does not need a UIA worker.
    private void WakeWorker()
    {
        if (_workerThread == null)
        {
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "VNotch.WindowTitleScanner.Worker",
                Priority = ThreadPriority.BelowNormal
            };
            _workerThread.Start();
        }
        _wakeWorkerEvent.Set();
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++; // Do not publish results from an in-flight UIA call.
            if (_workerThread == null) _wakeWorkerEvent.Dispose();
            else _wakeWorkerEvent.Set();
        }
    }

    // Must be called under _cacheLock.
    private void ScheduleWorkerScanLocked(ref bool scanActive, ref int scanGen, ref bool pendingFlag)
    {
        if (!scanActive)
        {
            scanActive = true;
            scanGen = _generation;
            pendingFlag = true;
            WakeWorker();
        }
        else if (scanGen != _generation)
        {
            scanGen = _generation;
            pendingFlag = true;
            WakeWorker();
        }
    }

    public string? TryGetBrowserUrl()
    {
        if (!CanInspectBrowser())
            return null;

        lock (_cacheLock)
        {
            if (_disposed) return null;
            if ((DateTime.UtcNow - _lastBrowserUrlTime).TotalMilliseconds < 1000)
                return _cachedBrowserUrl;

            ScheduleWorkerScanLocked(ref _browserUrlScanActive, ref _browserUrlScanGen, ref _pendingBrowserUrlScan);
            return _cachedBrowserUrl;
        }
    }

    public string? TryGetMediaUrlFromAnyBrowser()
    {
        if (!CanInspectBrowser())
            return null;

        lock (_cacheLock)
        {
            if (_disposed) return null;
            int ttlMs = !string.IsNullOrEmpty(_cachedAnyBrowserMediaUrl) ? 1500 : 400;
            if ((DateTime.UtcNow - _lastAnyBrowserMediaUrlTime).TotalMilliseconds < ttlMs)
                return _cachedAnyBrowserMediaUrl;

            ScheduleWorkerScanLocked(ref _anyBrowserScanActive, ref _anyBrowserScanGen, ref _pendingAnyBrowserScan);
            return _cachedAnyBrowserMediaUrl;
        }
    }

    private bool _cachedPipActive;
    private IntPtr _cachedPipHwnd;
    private string _cachedPipTitle = string.Empty;
    private string? _cachedPipWindowProcessName;
    private DateTime _lastPipCheckTime = DateTime.MinValue;

    private string? _cachedProcessPipQueryName;
    private bool _cachedProcessPipActive;
    private IntPtr _cachedProcessPipHwnd;
    private string _cachedProcessPipTitle = string.Empty;
    private DateTime _lastProcessPipCheckTime = DateTime.MinValue;

    public void InvalidateUrlCaches()
    {
        lock (_cacheLock)
        {
            _generation++;
            _cachedBrowserUrl = null;
            _lastBrowserUrlTime = DateTime.MinValue;
            _cachedAnyBrowserMediaUrl = null;
            _lastAnyBrowserMediaUrlTime = DateTime.MinValue;
            _cachedSpotifyWebPlayerOpen = false;
            _lastSpotifyWebPlayerTime = DateTime.MinValue;
            _cachedPipActive = false;
            _cachedPipHwnd = IntPtr.Zero;
            _cachedPipTitle = string.Empty;
            _cachedPipWindowProcessName = null;
            _lastPipCheckTime = DateTime.MinValue;
            _cachedProcessPipQueryName = null;
            _cachedProcessPipActive = false;
            _cachedProcessPipHwnd = IntPtr.Zero;
            _cachedProcessPipTitle = string.Empty;
            _lastProcessPipCheckTime = DateTime.MinValue;
        }
    }

    public bool IsPipActive(string? processName = null)
    {
        lock (_cacheLock)
        {
            return TryGetPipWindowCore(out _, out _, processName);
        }
    }

    public bool TryGetPipWindow(out IntPtr pipHwnd, out string pipTitle, string? processName = null)
    {
        lock (_cacheLock)
        {
            return TryGetPipWindowCore(out pipHwnd, out pipTitle, processName);
        }
    }

    private bool TryGetPipWindowCore(out IntPtr pipHwnd, out string pipTitle, string? processName)
    {
        int ttlMs = _cachedPipActive ? 400 : 700;
        if ((DateTime.UtcNow - _lastPipCheckTime).TotalMilliseconds >= ttlMs)
        {
            _cachedPipActive = PipWindowFinder(out _cachedPipHwnd, out _cachedPipTitle, null);
            _cachedPipTitle ??= string.Empty;
            _cachedPipWindowProcessName = null;
            if (_cachedPipActive && _cachedPipHwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(_cachedPipHwnd, out uint pipPid);
                if (pipPid != 0)
                    _cachedPipWindowProcessName = ProcessNameResolver.TryGetName(pipPid);
            }
            _lastPipCheckTime = DateTime.UtcNow;
        }

        if (string.IsNullOrEmpty(processName))
        {
            pipHwnd = _cachedPipHwnd;
            pipTitle = _cachedPipTitle;
            return _cachedPipActive;
        }

        if (!_cachedPipActive)
        {
            pipHwnd = IntPtr.Zero;
            pipTitle = string.Empty;
            return false;
        }

        if (string.Equals(_cachedPipWindowProcessName, processName, StringComparison.OrdinalIgnoreCase))
        {
            pipHwnd = _cachedPipHwnd;
            pipTitle = _cachedPipTitle;
            return true;
        }

        int processTtlMs = _cachedProcessPipActive ? 400 : 700;
        if (!string.Equals(_cachedProcessPipQueryName, processName, StringComparison.OrdinalIgnoreCase) ||
            (DateTime.UtcNow - _lastProcessPipCheckTime).TotalMilliseconds >= processTtlMs)
        {
            _cachedProcessPipQueryName = processName;
            _cachedProcessPipActive = PipWindowFinder(out _cachedProcessPipHwnd, out _cachedProcessPipTitle, processName);
            _cachedProcessPipTitle ??= string.Empty;
            _lastProcessPipCheckTime = DateTime.UtcNow;
        }

        pipHwnd = _cachedProcessPipHwnd;
        pipTitle = _cachedProcessPipTitle;
        return _cachedProcessPipActive;
    }

    public bool IsSpotifyWebPlayerOpen()
    {
        if (!CanInspectBrowser()) return false;
        lock (_cacheLock)
        {
            if (_disposed) return false;
            int ttlMs = _cachedSpotifyWebPlayerOpen ? 3000 : 1000;
            if ((DateTime.UtcNow - _lastSpotifyWebPlayerTime).TotalMilliseconds < ttlMs)
                return _cachedSpotifyWebPlayerOpen;

            ScheduleWorkerScanLocked(ref _spotifyScanActive, ref _spotifyScanGen, ref _pendingSpotifyScan);
            return _cachedSpotifyWebPlayerOpen;
        }
    }

    private enum ScanType
    {
        None,
        BrowserUrl,
        AnyBrowserMediaUrl,
        Spotify
    }

    private void WorkerLoop()
    {
        try { RunWorkerScans(); }
        finally
        {
            // UIA may be slow to return. Disposal never blocks the UI waiting for
            // COM; the worker releases its wait handle after its final call exits.
            lock (_cacheLock) { _wakeWorkerEvent.Dispose(); }
        }
    }

    private T? RunExtractorSafe<T>(string scanName, Func<T> extractor)
    {
        try
        {
            return InspectionAllowed ? extractor() : default;
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("UIA-SCANNER", () => $"{scanName} scan failed: {ex.Message}");
            return default;
        }
    }

    private void CompleteWorkerScan<T>(
        int capturedGen,
        ref int scanGen,
        ref bool scanActive,
        ref T cachedValue,
        ref DateTime lastScanTime,
        T value)
    {
        lock (_cacheLock)
        {
            if (scanGen == capturedGen)
                scanActive = false;
            if (_generation == capturedGen && InspectionAllowed)
            {
                cachedValue = value;
                lastScanTime = DateTime.UtcNow;
            }
        }
    }

    private void RunWorkerScans()
    {
        while (!_disposed)
        {
            try
            {
                _wakeWorkerEvent.WaitOne();
            }
            catch (ThreadAbortException)
            {
                break;
            }
            catch (Exception)
            {
                if (_disposed) break;
            }

            if (_disposed) break;

            while (!_disposed)
            {
                ScanType nextScan = ScanType.None;
                int capturedGen;

                lock (_cacheLock)
                {
                    capturedGen = _generation;

                    if (_pendingBrowserUrlScan)
                    {
                        nextScan = ScanType.BrowserUrl;
                        _pendingBrowserUrlScan = false;
                    }
                    else if (_pendingAnyBrowserScan)
                    {
                        nextScan = ScanType.AnyBrowserMediaUrl;
                        _pendingAnyBrowserScan = false;
                    }
                    else if (_pendingSpotifyScan)
                    {
                        nextScan = ScanType.Spotify;
                        _pendingSpotifyScan = false;
                    }
                }

                if (nextScan == ScanType.None)
                    break;

                switch (nextScan)
                {
                    case ScanType.BrowserUrl:
                        CompleteWorkerScan(
                            capturedGen,
                            ref _browserUrlScanGen,
                            ref _browserUrlScanActive,
                            ref _cachedBrowserUrl,
                            ref _lastBrowserUrlTime,
                            RunExtractorSafe("BrowserUrl", BrowserUrlExtractor));
                        break;

                    case ScanType.AnyBrowserMediaUrl:
                        CompleteWorkerScan(
                            capturedGen,
                            ref _anyBrowserScanGen,
                            ref _anyBrowserScanActive,
                            ref _cachedAnyBrowserMediaUrl,
                            ref _lastAnyBrowserMediaUrlTime,
                            RunExtractorSafe("AnyBrowserMediaUrl", AnyBrowserMediaUrlExtractor));
                        break;

                    case ScanType.Spotify:
                        CompleteWorkerScan(
                            capturedGen,
                            ref _spotifyScanGen,
                            ref _spotifyScanActive,
                            ref _cachedSpotifyWebPlayerOpen,
                            ref _lastSpotifyWebPlayerTime,
                            RunExtractorSafe("Spotify", SpotifyWebPlayerDetector));
                        break;
                }
            }
        }
    }

    private static string? ExtractMediaUrlFromAllBrowserWindows()
    {
        var foregroundUrl = ExtractBrowserUrlCore();
        if (!string.IsNullOrEmpty(foregroundUrl) && IsMediaUrl(foregroundUrl))
            return foregroundUrl;

        if (TryCollectVisibleWindowsNative((uint)Environment.ProcessId, out var entries, out int count))
        {
            for (int i = 0; i < count; i++)
            {
                uint pid = entries[i].ProcessId;
                if (pid == 0) continue;
                string? processName = TryGetProcessName((int)pid);
                if (processName == null || !IsBrowserProcess(processName)) continue;

                var url = ExtractUrlFromWindowHandle((IntPtr)entries[i].Hwnd, processName);
                if (!string.IsNullOrEmpty(url) && IsMediaUrl(url))
                    return url;
            }
            return null;
        }

        string? foundUrl = null;

        EnumWindows((hWnd, _) =>
        {
            var url = TryGetBrowserWindowMediaUrl(hWnd);
            if (url != null)
            {
                foundUrl = url;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return foundUrl;
    }

    private static string? TryGetBrowserWindowMediaUrl(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd) || GetWindowTextLength(hWnd) == 0) return null;

        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return null;

        string? processName = TryGetProcessName((int)pid);
        if (processName == null || !IsBrowserProcess(processName)) return null;

        var url = ExtractUrlFromWindowHandle(hWnd, processName);
        return (!string.IsNullOrEmpty(url) && IsMediaUrl(url)) ? url : null;
    }

    private static string? TryGetProcessName(int pid)
    {
        return pid > 0 ? ProcessNameResolver.TryGetName((uint)pid)?.ToLowerInvariant() : null;
    }

    private static bool IsBrowserProcess(string processName)
    {
        ReadOnlySpan<char> sp = processName != null ? processName.AsSpan() : ReadOnlySpan<char>.Empty;
        return vn_wtscanner_is_browser_process(MemoryMarshal.Cast<char, ushort>(sp), sp.Length, out byte isBrowser) == 0 && isBrowser != 0;
    }

    private const int DefaultUiaTimeoutMs = 450;
    private static readonly object UiaStaLock = new();
    private static readonly SemaphoreSlim UiaSlot = new(1, 1);
    private static readonly AutoResetEvent UiaWorkAvailable = new(false);
    private static Thread? _uiaStaThread;
    private static Action? _uiaCurrentWork;
    private static int _uiaInFlight;
    private static int _activeUiaStaThreads;

    internal static int ActiveUiaStaThreadCount => Volatile.Read(ref _activeUiaStaThreads);

    internal static T? RunWithTimeout<T>(Func<T> func, int timeoutMs = DefaultUiaTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (timeoutMs < 0)
            return default;

        if (!UiaSlot.Wait(0) && !UiaSlot.Wait(Math.Min(timeoutMs, 15)))
            return default;

        if (Interlocked.CompareExchange(ref _uiaInFlight, 1, 0) != 0)
        {
            UiaSlot.Release();
            return default;
        }

        object callSync = new();
        T? result = default;
        bool completed = false;

        try
        {
            lock (UiaStaLock)
            {
                _uiaCurrentWork = () =>
                {
                    T? value = default;
                    try
                    {
                        value = func();
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Debug("UIA-SCANNER", () => $"UIA STA work failed: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _uiaInFlight, 0);
                        UiaSlot.Release();
                        lock (callSync)
                        {
                            result = value;
                            completed = true;
                            Monitor.Pulse(callSync);
                        }
                    }
                };

                EnsureUiaStaThreadLocked();
                UiaWorkAvailable.Set();
            }

            lock (callSync)
            {
                if (!completed)
                {
                    Monitor.Wait(callSync, timeoutMs);
                }

                return completed ? result : default;
            }
        }
        catch
        {
            Interlocked.Exchange(ref _uiaInFlight, 0);
            UiaSlot.Release();
            throw;
        }
    }

    private static void EnsureUiaStaThreadLocked()
    {
        if (_uiaStaThread is { IsAlive: true })
            return;

        Interlocked.Increment(ref _activeUiaStaThreads);
        var thread = new Thread(UiaStaWorkerLoop)
        {
            IsBackground = true,
            Name = "VNotch-UIA-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        _uiaStaThread = thread;
        thread.Start();
    }

    private static void UiaStaWorkerLoop()
    {
        try
        {
            while (true)
            {
                UiaWorkAvailable.WaitOne();
                Action? work;
                lock (UiaStaLock)
                {
                    work = _uiaCurrentWork;
                    _uiaCurrentWork = null;
                }

                work?.Invoke();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeUiaStaThreads);
        }
    }

    private static string? ExtractUrlFromWindowHandle(IntPtr hwnd, string processName) =>
        RunWithTimeout(() => ExtractUrlFromWindowHandleCore(hwnd, processName));

    private static string? ExtractUrlFromWindowHandleCore(IntPtr hwnd, string processName)
        => ScanNativeBrowser(hwnd, processName).Url;

    private static bool DetectSpotifyWebPlayer()
    {
        bool found = false;

        try
        {
            if (TryCollectVisibleWindowsNative((uint)Environment.ProcessId, out var entries, out int count))
            {
                for (int i = 0; i < count; i++)
                {
                    uint pid = entries[i].ProcessId;
                    if (pid == 0) continue;
                    string? processName = TryGetProcessName((int)pid);
                    if (processName == null || !IsBrowserProcess(processName)) continue;
                    if (WindowHasSpotifyWebPlayer((IntPtr)entries[i].Hwnd, processName))
                        return true;
                }
                return false;
            }

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || GetWindowTextLength(hWnd) == 0) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return true;

                string? processName = TryGetProcessName((int)pid);
                if (processName == null || !IsBrowserProcess(processName)) return true;

                if (WindowHasSpotifyWebPlayer(hWnd, processName))
                {
                    found = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Window enumeration or UI Automation exceptions safely ignored
        }

        return found;
    }

    private static bool WindowHasSpotifyWebPlayer(IntPtr hwnd, string processName) =>
        RunWithTimeout(() => WindowHasSpotifyWebPlayerCore(hwnd, processName));

    private static bool WindowHasSpotifyWebPlayerCore(IntPtr hwnd, string processName)
        => ScanNativeBrowser(hwnd, processName).Spotify;

    private static string? ExtractBrowserUrlCore()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;

            string? processName = TryGetProcessName((int)pid);
            if (processName == null || !IsBrowserProcess(processName)) return null;

            return ExtractUrlFromWindowHandle(hwnd, processName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowTitleScanner] Browser URL extraction failed: {ex.Message}");
        }

        return null;
    }
}
