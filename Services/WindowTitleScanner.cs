using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

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

public sealed class WindowTitleScanner : IWindowTitleScanner, IDisposable
{
    private const string HttpPrefix = "http://";
    private const string HttpsPrefix = "https://";
    private const string SpotifyWebPlayerHost = "open.spotify.com";

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static readonly string[] _platformKeywords =
    {
        "spotify", "youtube", "soundcloud", "facebook", "tiktok", "instagram", "twitter", " / x", "apple music", "apple", "music",
        "twitch", "discord", "vesktop", "netflix", "tidal", "deezer", "bandcamp", "bilibili", "哔哩哔哩", "vimeo", "crunchyroll", "prime video", "disney",
        "picture in picture", "picture-in-picture", "hình trong hình", "hinh trong hinh", "bild-in-bild", "image dans l'image",
        "pantalla en pantalla", "cuadro en cuadro", "画中画", "畫中畫", "子母画面", "子母畫面", "ピクチャー イン ピクチャー", "ピクチャーインピクチャー",
        "картинка в картинке", "imagem na imagem", "imagem sobre imagem", "finestra mobile", "gambar dalam gambar", "resim içinde resim", "화면 속 화면", "pip"
    };

    private static readonly string[] _browserProcessNames =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "zen",
        "arc", "coccoc", "sidekick", "thorium", "waterfox", "floorp", "librewolf",
        "chromium", "whale", "yandex", "wavebox", "helium", "supermium", "browser"
    };

    private readonly object _cacheLock = new();
    private List<string> _cachedWindowTitles = new();
    private DateTime _lastWindowEnumTime = DateTime.MinValue;

    public List<string> GetAllWindowTitles(bool isThrottled)
    {
        lock (_cacheLock)
        {
            int cacheDurationMs = isThrottled ? 300 : 700;
            if ((DateTime.UtcNow - _lastWindowEnumTime).TotalMilliseconds < cacheDurationMs)
            {
                return _cachedWindowTitles;
            }

            var titles = new List<string>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                int length = GetWindowTextLength(hWnd);
                if (length == 0)
                {
                    return true;
                }

                var sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();

                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                string lowerTitle = title.ToLowerInvariant();
                if (_platformKeywords.Any(k => lowerTitle.Contains(k, StringComparison.Ordinal)))
                {
                    titles.Add(title);
                }

                return true;
            }, IntPtr.Zero);

            _cachedWindowTitles = titles;
            _lastWindowEnumTime = DateTime.UtcNow;
            return titles;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private string? _cachedBrowserUrl;
    private DateTime _lastBrowserUrlTime = DateTime.MinValue;

    private string? _cachedAnyBrowserMediaUrl;
    private DateTime _lastAnyBrowserMediaUrlTime = DateTime.MinValue;

    private bool _cachedSpotifyWebPlayerOpen;
    private DateTime _lastSpotifyWebPlayerTime = DateTime.MinValue;

    private static volatile int _cachedInspectionAllowed = -1;
    private static FileSystemWatcher? _settingsWatcher;
    private static readonly object _inspectionLock = new();

    static WindowTitleScanner()
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appDataPath, "V-Notch");
            if (Directory.Exists(dir))
            {
                _settingsWatcher = new FileSystemWatcher(dir, "settings.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _settingsWatcher.Changed += (_, _) => InvalidateInspectionAllowed();
                _settingsWatcher.Created += (_, _) => InvalidateInspectionAllowed();
                _settingsWatcher.Renamed += (_, _) => InvalidateInspectionAllowed();
            }
        }
        catch
        {
            // Ignore file watcher initialization issues
        }
    }

    public static void UpdateInspectionAllowed(bool allowed)
    {
        _cachedInspectionAllowed = allowed ? 1 : 0;
    }

    public static void InvalidateInspectionAllowed()
    {
        _cachedInspectionAllowed = -1;
    }

    public static bool IsBrowserUrlInspectionAllowed()
    {
        int cached = _cachedInspectionAllowed;
        if (cached != -1)
            return cached == 1;

        lock (_inspectionLock)
        {
            if (_cachedInspectionAllowed != -1)
                return _cachedInspectionAllowed == 1;

            bool allowed = true;
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string settingsPath = Path.Combine(appDataPath, "V-Notch", "settings.json");
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty(nameof(Models.NotchSettings.EnableBrowserUrlInspection), out var urlInspection) && !urlInspection.GetBoolean())
                        allowed = false;
                }
            }
            catch (Exception)
            {
                // Settings file may be missing, locked, or malformed; default to inspection allowed
            }

            _cachedInspectionAllowed = allowed ? 1 : 0;
            return allowed;
        }
    }

    private int _generation;
    private readonly AutoResetEvent _wakeWorkerEvent = new(false);
    private readonly Thread _workerThread;
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
    internal Func<bool> SpotifyWebPlayerDetector { get; set; } = DetectSpotifyWebPlayer;

    public WindowTitleScanner()
    {
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "VNotch.WindowTitleScanner.Worker",
            Priority = ThreadPriority.BelowNormal
        };
        _workerThread.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _wakeWorkerEvent.Set();
        }
        catch (ObjectDisposedException) { }
    }

    public string? TryGetBrowserUrl()
    {
        if (!IsBrowserUrlInspectionAllowed())
            return null;

        lock (_cacheLock)
        {
            if ((DateTime.UtcNow - _lastBrowserUrlTime).TotalMilliseconds < 1000)
                return _cachedBrowserUrl;

            if (!_browserUrlScanActive)
            {
                _browserUrlScanActive = true;
                _browserUrlScanGen = _generation;
                _pendingBrowserUrlScan = true;
                _wakeWorkerEvent.Set();
            }
            else if (_browserUrlScanGen != _generation)
            {
                _browserUrlScanGen = _generation;
                _pendingBrowserUrlScan = true;
                _wakeWorkerEvent.Set();
            }

            return _cachedBrowserUrl;
        }
    }

    public string? TryGetMediaUrlFromAnyBrowser()
    {
        if (!IsBrowserUrlInspectionAllowed())
            return null;

        lock (_cacheLock)
        {
            int ttlMs = !string.IsNullOrEmpty(_cachedAnyBrowserMediaUrl) ? 1500 : 400;
            if ((DateTime.UtcNow - _lastAnyBrowserMediaUrlTime).TotalMilliseconds < ttlMs)
                return _cachedAnyBrowserMediaUrl;

            if (!_anyBrowserScanActive)
            {
                _anyBrowserScanActive = true;
                _anyBrowserScanGen = _generation;
                _pendingAnyBrowserScan = true;
                _wakeWorkerEvent.Set();
            }
            else if (_anyBrowserScanGen != _generation)
            {
                _anyBrowserScanGen = _generation;
                _pendingAnyBrowserScan = true;
                _wakeWorkerEvent.Set();
            }

            return _cachedAnyBrowserMediaUrl;
        }
    }

    private bool _cachedPipActive;
    private IntPtr _cachedPipHwnd;
    private string _cachedPipTitle = string.Empty;
    private DateTime _lastPipCheckTime = DateTime.MinValue;
    private string? _lastPipProcessName;

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
            _lastPipCheckTime = DateTime.MinValue;
            _lastPipProcessName = null;
        }
    }

    public bool IsPipActive(string? processName = null)
    {
        lock (_cacheLock)
        {
            CheckPipCache(processName);
            return _cachedPipActive;
        }
    }

    public bool TryGetPipWindow(out IntPtr pipHwnd, out string pipTitle, string? processName = null)
    {
        lock (_cacheLock)
        {
            CheckPipCache(processName);
            pipHwnd = _cachedPipHwnd;
            pipTitle = _cachedPipTitle;
            return _cachedPipActive;
        }
    }

    private void CheckPipCache(string? processName)
    {
        int ttlMs = _cachedPipActive ? 400 : 700;
        if ((DateTime.UtcNow - _lastPipCheckTime).TotalMilliseconds < ttlMs &&
            string.Equals(processName, _lastPipProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cachedPipActive = PipDetector.TryFindPipWindow(out _cachedPipHwnd, out _cachedPipTitle, processName);
        _lastPipCheckTime = DateTime.UtcNow;
        _lastPipProcessName = processName;
    }

    public bool IsSpotifyWebPlayerOpen()
    {
        lock (_cacheLock)
        {
            int ttlMs = _cachedSpotifyWebPlayerOpen ? 3000 : 1000;
            if ((DateTime.UtcNow - _lastSpotifyWebPlayerTime).TotalMilliseconds < ttlMs)
                return _cachedSpotifyWebPlayerOpen;

            if (!_spotifyScanActive)
            {
                _spotifyScanActive = true;
                _spotifyScanGen = _generation;
                _pendingSpotifyScan = true;
                _wakeWorkerEvent.Set();
            }
            else if (_spotifyScanGen != _generation)
            {
                _spotifyScanGen = _generation;
                _pendingSpotifyScan = true;
                _wakeWorkerEvent.Set();
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
                                url = BrowserUrlExtractor();
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
                                if (_generation == capturedGen)
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
                                url = AnyBrowserMediaUrlExtractor();
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
                                if (_generation == capturedGen)
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
                                isSpotify = SpotifyWebPlayerDetector();
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
                                if (_generation == capturedGen)
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
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.ProcessName.ToLowerInvariant();
        }
        catch (Exception)
        {
            // Process may have exited before query; skip window
            return null;
        }
    }

    private static bool IsBrowserProcess(string processName) =>
        _browserProcessNames.Any(name => processName.Contains(name, StringComparison.Ordinal));

    private static string? ExtractUrlFromWindowHandle(IntPtr hwnd, string processName)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element == null) return null;

            AutomationElement? addressBar = processName.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                ? element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"))
                : FindChromiumAddressBar(element);

            if (addressBar != null &&
                addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object? urlPattern))
            {
                var vp = (ValuePattern)urlPattern;
                string url = vp.Current.Value ?? "";

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && url.Contains('.'))
                    url = HttpsPrefix + url;

                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }

            var tabUrl = TryFindMediaUrlInTabs(element);
            if (!string.IsNullOrEmpty(tabUrl))
                return tabUrl;
        }
        catch (Exception)
        {
            // UI Automation can throw COM/ElementNotAvailable exceptions during window enumeration
        }

        return null;
    }

    private static AutomationElement? FindChromiumAddressBar(AutomationElement element)
    {
        var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        var edits = element.FindAll(TreeScope.Descendants, editCondition);

        foreach (AutomationElement edit in edits)
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

    private static bool IsAddressBarValue(string val) =>
        val.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
        val.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
        val.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase) ||
        val.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase) ||
        val.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase) ||
        val.Contains(".com/", StringComparison.OrdinalIgnoreCase) ||
        val.Contains(".org/", StringComparison.OrdinalIgnoreCase);

    private static string? TryFindMediaUrlInTabs(AutomationElement root)
    {
        try
        {
            var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            var tabs = root.FindAll(TreeScope.Descendants, tabCondition);
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

    private static string? TryReadTabUrl(AutomationElement tab)
    {
        try
        {
            string help = tab.Current.HelpText ?? string.Empty;
            if (LooksLikeUrl(help)) return NormalizeUrl(help);

            string name = tab.Current.Name ?? string.Empty;
            if (LooksLikeUrl(name)) return NormalizeUrl(name);

            var subtree = tab.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.IsControlElementProperty, true));

            foreach (AutomationElement child in subtree)
            {
                string childHelp = child.Current.HelpText ?? string.Empty;
                if (LooksLikeUrl(childHelp)) return NormalizeUrl(childHelp);

                string childName = child.Current.Name ?? string.Empty;
                if (LooksLikeUrl(childName)) return NormalizeUrl(childName);

                if (child.TryGetCurrentPattern(ValuePattern.Pattern, out object? p))
                {
                    string v = ((ValuePattern)p).Current.Value ?? string.Empty;
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
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase) ||
               s.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("twitch.tv/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("discord.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains(SpotifyWebPlayerHost + "/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("music.apple.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("tidal.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("deezer.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("bandcamp.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("netflix.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("bilibili.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("vimeo.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("crunchyroll.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUrl(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase) && s.Contains('.'))
        {
            s = HttpsPrefix + s;
        }
        return s;
    }

    private static bool IsMediaUrl(string url)
    {
        return url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("music.youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("twitch.tv/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("discord.com/channels/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("discord.com/app", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(SpotifyWebPlayerHost + "/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("music.apple.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("listen.tidal.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("deezer.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("bandcamp.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("netflix.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("bilibili.com/video", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("vimeo.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("crunchyroll.com/watch", StringComparison.OrdinalIgnoreCase);
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

    private static bool WindowHasSpotifyWebPlayer(IntPtr hwnd, string processName)
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
        var urlBar = element.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
        if (urlBar != null &&
            urlBar.TryGetCurrentPattern(ValuePattern.Pattern, out object? fp))
        {
            string val = ((ValuePattern)fp).Current.Value ?? "";
            return val.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool ChromiumAddressBarHasSpotify(AutomationElement element)
    {
        var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        var edits = element.FindAll(TreeScope.Descendants, editCondition);
        foreach (AutomationElement edit in edits)
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
        var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
        var tabs = element.FindAll(TreeScope.Descendants, tabCondition);
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

    private static bool TabReferencesSpotifyWebPlayer(AutomationElement tab)
    {
        try
        {
            string help = tab.Current.HelpText ?? string.Empty;
            if (help.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

            string name = tab.Current.Name ?? string.Empty;
            if (name.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

            var subtree = tab.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.IsControlElementProperty, true));
            foreach (AutomationElement child in subtree)
            {
                string childHelp = child.Current.HelpText ?? string.Empty;
                if (childHelp.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

                string childName = child.Current.Name ?? string.Empty;
                if (childName.Contains(SpotifyWebPlayerHost, StringComparison.OrdinalIgnoreCase)) return true;

                if (child.TryGetCurrentPattern(ValuePattern.Pattern, out object? p))
                {
                    string v = ((ValuePattern)p).Current.Value ?? string.Empty;
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
