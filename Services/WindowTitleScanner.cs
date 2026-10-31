using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

public interface IWindowTitleScanner
{
    List<string> GetAllWindowTitles(bool isThrottled);
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
    private static partial int vn_wtscanner_is_address_bar_value(
        ReadOnlySpan<ushort> val, int valLen, out byte outIsAddr);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_wtscanner_looks_like_url(
        ReadOnlySpan<ushort> str, int strLen, out byte outLooksLike);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_wtscanner_normalize_url(
        ReadOnlySpan<ushort> str, int strLen,
        Span<ushort> outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_wtscanner_is_media_url(
        ReadOnlySpan<ushort> url, int urlLen, out byte outIsMedia);

    private readonly object _cacheLock = new();
    // All title enumerations are serialized by _cacheLock.
    private char[] _titleBuffer = new char[256];
    private List<string> _cachedWindowTitles = new();
    private DateTime _lastWindowEnumTime = DateTime.MinValue;

    public List<string> GetAllWindowTitles(bool isThrottled)
    {
        lock (_cacheLock)
        {
            // Throttled players need fallback detection, but full desktop scans
            // should back off instead of increasing their polling frequency.
            int cacheDurationMs = isThrottled ? 2000 : 700;
            if ((DateTime.UtcNow - _lastWindowEnumTime).TotalMilliseconds < cacheDurationMs)
            {
                return new List<string>(_cachedWindowTitles);
            }

            var titles = new List<string>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                // USER32 reads foreign-process captions without WM_GETTEXT.
                // Our own windows can synchronously message a UI thread while
                // this background scanner holds the cache lock.
                GetWindowThreadProcessId(hWnd, out uint processId);
                if (processId == 0 || processId == (uint)Environment.ProcessId)
                    return true;

                AddMatchingWindowTitle(hWnd, titles);

                return true;
            }, IntPtr.Zero);

            _cachedWindowTitles = titles;
            _lastWindowEnumTime = DateTime.UtcNow;
            return new List<string>(titles);
        }
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

    public string? TryGetBrowserUrl()
    {
        if (!CanInspectBrowser())
            return null;

        lock (_cacheLock)
        {
            if (_disposed) return null;
            if ((DateTime.UtcNow - _lastBrowserUrlTime).TotalMilliseconds < 1000)
                return _cachedBrowserUrl;

            if (!_browserUrlScanActive)
            {
                _browserUrlScanActive = true;
                _browserUrlScanGen = _generation;
                _pendingBrowserUrlScan = true;
                WakeWorker();
            }
            else if (_browserUrlScanGen != _generation)
            {
                _browserUrlScanGen = _generation;
                _pendingBrowserUrlScan = true;
                WakeWorker();
            }

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

            if (!_anyBrowserScanActive)
            {
                _anyBrowserScanActive = true;
                _anyBrowserScanGen = _generation;
                _pendingAnyBrowserScan = true;
                WakeWorker();
            }
            else if (_anyBrowserScanGen != _generation)
            {
                _anyBrowserScanGen = _generation;
                _pendingAnyBrowserScan = true;
                WakeWorker();
            }

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

            if (!_spotifyScanActive)
            {
                _spotifyScanActive = true;
                _spotifyScanGen = _generation;
                _pendingSpotifyScan = true;
                WakeWorker();
            }
            else if (_spotifyScanGen != _generation)
            {
                _spotifyScanGen = _generation;
                _pendingSpotifyScan = true;
                WakeWorker();
            }

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
                int capturedGen = 0;

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
                        {
                            string? url = null;
                            try
                            {
                                if (InspectionAllowed) url = BrowserUrlExtractor();
                            }
                            catch (Exception ex)
                            {
                                RuntimeLog.Debug("UIA-SCANNER", () => $"BrowserUrl scan failed: {ex.Message}");
                            }

                            lock (_cacheLock)
                            {
                                if (_browserUrlScanGen == capturedGen)
                                {
                                    _browserUrlScanActive = false;
                                }
                                if (_generation == capturedGen && InspectionAllowed)
                                {
                                    _cachedBrowserUrl = url;
                                    _lastBrowserUrlTime = DateTime.UtcNow;
                                }
                            }
                            break;
                        }

                    case ScanType.AnyBrowserMediaUrl:
                        {
                            string? url = null;
                            try
                            {
                                if (InspectionAllowed) url = AnyBrowserMediaUrlExtractor();
                            }
                            catch (Exception ex)
                            {
                                RuntimeLog.Debug("UIA-SCANNER", () => $"AnyBrowserMediaUrl scan failed: {ex.Message}");
                            }

                            lock (_cacheLock)
                            {
                                if (_anyBrowserScanGen == capturedGen)
                                {
                                    _anyBrowserScanActive = false;
                                }
                                if (_generation == capturedGen && InspectionAllowed)
                                {
                                    _cachedAnyBrowserMediaUrl = url;
                                    _lastAnyBrowserMediaUrlTime = DateTime.UtcNow;
                                }
                            }
                            break;
                        }

                    case ScanType.Spotify:
                        {
                            bool isSpotify = false;
                            try
                            {
                                if (InspectionAllowed) isSpotify = SpotifyWebPlayerDetector();
                            }
                            catch (Exception ex)
                            {
                                RuntimeLog.Debug("UIA-SCANNER", () => $"Spotify scan failed: {ex.Message}");
                            }

                            lock (_cacheLock)
                            {
                                if (_spotifyScanGen == capturedGen)
                                {
                                    _spotifyScanActive = false;
                                }
                                if (_generation == capturedGen && InspectionAllowed)
                                {
                                    _cachedSpotifyWebPlayerOpen = isSpotify;
                                    _lastSpotifyWebPlayerTime = DateTime.UtcNow;
                                }
                            }
                            break;
                        }
                }
            }
        }
    }

    private static string? ExtractMediaUrlFromAllBrowserWindows()
    {
        var foregroundUrl = ExtractBrowserUrlCore();
        if (!string.IsNullOrEmpty(foregroundUrl) && IsMediaUrl(foregroundUrl))
            return foregroundUrl;

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
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element == null) return null;

            string? fallbackAddressBarUrl = null;
            AutomationElement? addressBar = processName.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                ? FindBrowserChromeElements(element, ControlType.Edit).FirstOrDefault(edit => edit.Current.AutomationId == "urlbar-input")
                : FindChromiumAddressBar(element);

            if (addressBar != null &&
                addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object? urlPattern))
            {
                var vp = (ValuePattern)urlPattern;
                string url = vp.Current.Value ?? "";

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && url.Contains('.'))
                    url = HttpsPrefix + url;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    if (IsMediaUrl(url))
                        return url;

                    fallbackAddressBarUrl = url;
                }
            }

            var tabUrl = TryFindMediaUrlInTabs(element);
            if (!string.IsNullOrEmpty(tabUrl))
                return tabUrl;

            if (!string.IsNullOrWhiteSpace(fallbackAddressBarUrl))
                return fallbackAddressBarUrl;
        }
        catch (Exception)
        {
            // UI Automation can throw COM/ElementNotAvailable exceptions during window enumeration
        }

        return null;
    }

    internal static AutomationElement? FindChromiumAddressBar(AutomationElement element)
    {
        foreach (var toolbar in FindBrowserChromeElements(element, ControlType.ToolBar))
            foreach (var edit in FindBrowserChromeElements(toolbar, ControlType.Edit))
            {
                try
                {
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
                    {
                        var valuePattern = (ValuePattern)pattern;
                        string val = valuePattern.Current.Value ?? "";

                        if (IsAddressBarValue(val))
                        {
                            return edit;
                        }
                    }
                }
                catch (Exception)
                {
                    // ValuePattern may throw if element state changed during UI automation traversal
                }
            }

        return null;
    }

    private static bool IsAddressBarValue(string val)
    {
        ReadOnlySpan<char> sp = val != null ? val.AsSpan() : ReadOnlySpan<char>.Empty;
        return vn_wtscanner_is_address_bar_value(MemoryMarshal.Cast<char, ushort>(sp), sp.Length, out byte isAddr) == 0 && isAddr != 0;
    }

    private static string? TryFindMediaUrlInTabs(AutomationElement root)
    {
        try
        {
            var tabs = FindCachedTabs(root);
            if (tabs == null || tabs.Count == 0) return null;

            foreach (AutomationElement tab in tabs)
            {
                string? candidate = TryReadTabUrl(tab);
                if (!string.IsNullOrEmpty(candidate) && IsMediaUrl(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // UI Automation can throw when reading tabs from tearing-down browsers
        }
        return null;
    }

    // Walk only browser chrome. Never descend into the page's accessibility tree,
    // and bound work even if a provider exposes unusual or deeply nested controls.
    internal static IReadOnlyList<AutomationElement> FindBrowserChromeElements(AutomationElement root, ControlType targetType)
    {
        const int maxElements = 256;
        const int maxDepth = 8;
        var matches = new List<AutomationElement>();
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));
        int visited = 0;
        var cache = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition };
        cache.Add(AutomationElement.ControlTypeProperty);
        while (pending.Count > 0 && visited < maxElements)
        {
            var (parent, depth) = pending.Dequeue();
            if (depth >= maxDepth) continue;
            try
            {
                AutomationElementCollection children;
                using (cache.Activate())
                    children = parent.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement child in children)
                {
                    if (++visited > maxElements) break;
                    var type = child.Cached.ControlType;
                    if (type == ControlType.Document) continue;
                    if (type == targetType) matches.Add(child);
                    else pending.Enqueue((child, depth + 1));
                }
            }
            catch (Exception)
            {
                // A disappearing provider must not discard other browser controls.
            }
        }
        return matches;
    }

    internal static IReadOnlyList<AutomationElement> FindCachedTabs(AutomationElement root)
    {
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.None,
            TreeScope = TreeScope.Element | TreeScope.Children,
            // Match FindAll(Children, IsControlElement=true): direct raw children,
            // filtered below, rather than walking through non-control ancestors.
            TreeFilter = Automation.RawViewCondition
        };
        cache.Add(AutomationElement.HelpTextProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.IsControlElementProperty);
        cache.Add(ValuePattern.Pattern);
        cache.Add(ValuePattern.ValueProperty);
        var tabs = new List<AutomationElement>();
        foreach (var tab in FindBrowserChromeElements(root, ControlType.TabItem))
        {
            try { tabs.Add(tab.GetUpdatedCache(cache)); }
            catch (Exception) { /* Tab closed while capturing its snapshot. */ }
        }
        return tabs;
    }

    internal static string? TryReadTabUrl(AutomationElement tab)
    {
        try
        {
            string help = tab.Cached.HelpText ?? string.Empty;
            if (LooksLikeUrl(help)) return NormalizeUrl(help);

            string name = tab.Cached.Name ?? string.Empty;
            if (LooksLikeUrl(name)) return NormalizeUrl(name);

            var subtree = tab.CachedChildren;

            foreach (AutomationElement child in subtree)
            {
                if (!child.Cached.IsControlElement) continue;
                string childHelp = child.Cached.HelpText ?? string.Empty;
                if (LooksLikeUrl(childHelp)) return NormalizeUrl(childHelp);

                string childName = child.Cached.Name ?? string.Empty;
                if (LooksLikeUrl(childName)) return NormalizeUrl(childName);

                if (child.TryGetCachedPattern(ValuePattern.Pattern, out object? p))
                {
                    string v = ((ValuePattern)p).Cached.Value ?? string.Empty;
                    if (LooksLikeUrl(v)) return NormalizeUrl(v);
                }
            }
        }
        catch (Exception)
        {
            // UI Automation element properties may throw if child is destroyed
        }
        return null;
    }

    private static bool LooksLikeUrl(string s)
    {
        ReadOnlySpan<char> sp = s != null ? s.AsSpan() : ReadOnlySpan<char>.Empty;
        return vn_wtscanner_looks_like_url(MemoryMarshal.Cast<char, ushort>(sp), sp.Length, out byte looksLike) == 0 && looksLike != 0;
    }

    private static string NormalizeUrl(string s)
    {
        ReadOnlySpan<ushort> sp = MemoryMarshal.Cast<char, ushort>(s != null ? s.AsSpan() : ReadOnlySpan<char>.Empty);
        Span<char> buf = stackalloc char[512];
        int rc = vn_wtscanner_normalize_url(sp, sp.Length, MemoryMarshal.Cast<char, ushort>(buf), buf.Length, out int outLen);
        if (rc == 0) return outLen > 0 ? new string(buf[..outLen]) : string.Empty;
        if (rc == -2 && outLen > 0)
        {
            char[] arr = new char[outLen];
            if (vn_wtscanner_normalize_url(sp, sp.Length, MemoryMarshal.Cast<char, ushort>(arr.AsSpan()), arr.Length, out int actual) == 0)
                return new string(arr, 0, actual);
        }
        return s ?? string.Empty;
    }

    private static bool IsMediaUrl(string url)
    {
        ReadOnlySpan<char> sp = url != null ? url.AsSpan() : ReadOnlySpan<char>.Empty;
        return vn_wtscanner_is_media_url(MemoryMarshal.Cast<char, ushort>(sp), sp.Length, out byte isMedia) == 0 && isMedia != 0;
    }

    private static bool DetectSpotifyWebPlayer()
    {
        bool found = false;

        try
        {
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
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element == null) return false;

            if (processName.Contains("firefox", StringComparison.OrdinalIgnoreCase))
            {
                if (FirefoxAddressBarHasSpotify(element)) return true;
            }
            else if (ChromiumAddressBarHasSpotify(element))
            {
                return true;
            }

            return TabsContainSpotify(element);
        }
        catch (Exception)
        {
            // UI Automation tree inspection may throw if window closes
        }

        return false;
    }

    private static bool FirefoxAddressBarHasSpotify(AutomationElement element)
    {
        foreach (var edit in FindBrowserChromeElements(element, ControlType.Edit))
        {
            try
            {
                if (edit.Current.AutomationId == "urlbar-input" &&
                    edit.TryGetCurrentPattern(ValuePattern.Pattern, out object? fp))
                {
                    string val = ((ValuePattern)fp).Current.Value ?? "";
                    return val.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                // UI automation read failure on individual element
            }
        }
        return false;
    }

    private static bool ChromiumAddressBarHasSpotify(AutomationElement element)
    {
        foreach (var toolbar in FindBrowserChromeElements(element, ControlType.ToolBar))
            foreach (var edit in FindBrowserChromeElements(toolbar, ControlType.Edit))
            {
                try
                {
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
                    {
                        string val = ((ValuePattern)pattern).Current.Value ?? "";
                        if (val.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch (Exception)
                {
                    // UI automation read failure on individual element
                }
            }
        return false;
    }

    private static bool TabsContainSpotify(AutomationElement element)
    {
        var tabs = FindCachedTabs(element);
        if (tabs != null)
        {
            foreach (AutomationElement tab in tabs)
            {
                if (TabReferencesSpotifyWebPlayer(tab))
                    return true;
            }
        }
        return false;
    }

    internal static bool TabReferencesSpotifyWebPlayer(AutomationElement tab)
    {
        try
        {
            string help = tab.Cached.HelpText ?? string.Empty;
            if (help.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

            string name = tab.Cached.Name ?? string.Empty;
            if (name.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

            var subtree = tab.CachedChildren;
            foreach (AutomationElement child in subtree)
            {
                if (!child.Cached.IsControlElement) continue;
                string childHelp = child.Cached.HelpText ?? string.Empty;
                if (childHelp.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

                string childName = child.Cached.Name ?? string.Empty;
                if (childName.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

                if (child.TryGetCachedPattern(ValuePattern.Pattern, out object? p))
                {
                    string v = ((ValuePattern)p).Cached.Value ?? string.Empty;
                    if (v.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception)
        {
            // Tab element inspection may throw if tab closes during query
        }

        return false;
    }

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
