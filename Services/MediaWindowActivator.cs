using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using VNotch.Models;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

internal static class MediaWindowActivator
{
    private const string LogTag = "MEDIA-ACTIVATOR";
    private const string YouTubeToken = "youtube";
    private const string SpotifyToken = "spotify";
    private const string SoundCloudToken = "soundcloud";
    private const string BrowserToken = "browser";

    private const string MsEdgeProcess = "msedge";
    private const string ChromeProcess = "chrome";
    private const string FirefoxProcess = "firefox";
    private const string BraveProcess = "brave";
    private const string OperaProcess = "opera";
    private const string VivaldiProcess = "vivaldi";
    private const string ThoriumProcess = "thorium";

    private static readonly HashSet<string> KnownBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        MsEdgeProcess, ChromeProcess, FirefoxProcess, BraveProcess, OperaProcess, VivaldiProcess,
        "browser", "arc", "sidekick", "zen", "coccoc", ThoriumProcess,
        "waterfox", "floorp", "librewolf", "chromium", "whale",
        "yandex", "wavebox", "helium", "supermium"
    };

    private static readonly (string Token, MediaPlatform Platform, string[] Candidates)[] AppPlatformProcessMappings =
    {
        (SpotifyToken, MediaPlatform.Spotify, new[] { "Spotify" }),
        ("discord", MediaPlatform.Discord, new[] { "Discord", "DiscordCanary", "DiscordPTB", "Vesktop" }),
        ("twitch", MediaPlatform.Twitch, new[] { "Twitch" }),
        ("tidal", MediaPlatform.Tidal, new[] { "TIDAL" }),
        ("deezer", MediaPlatform.Deezer, new[] { "Deezer" }),
        ("applemusic", MediaPlatform.AppleMusic, new[] { "AppleMusic" }),
        ("apple music", MediaPlatform.AppleMusic, new[] { "AppleMusic" }),
    };

    private static readonly string[] CommonBrowserProcesses =
    {
        MsEdgeProcess, ChromeProcess, FirefoxProcess, BraveProcess, OperaProcess,
        VivaldiProcess, "zen", "arc", ThoriumProcess
    };

    public static bool TryActivateForMedia(MediaInfo info)
    {
        if (info.IsPictureInPicture || info.IsVideoSource)
        {
            if (TryActivatePipWindow(info))
            {
                return true;
            }
        }

        var candidates = GetProcessCandidates(info).ToList();
        var processNames = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);

        if (TryActivateExactBrowserTab(info, processNames))
        {
            return true;
        }

        bool preferBrowserTabMatch = info.IsVideoSource || info.Platform == MediaPlatform.SoundCloud;

        if (preferBrowserTabMatch && TryActivateBestMatchingWindow(info, processNames, out _))
        {
            return true;
        }

        if (TryActivateByProcessCandidates(candidates))
        {
            return true;
        }

        return TryActivateBestMatchingWindow(info, processNames, out _);
    }

    private static bool TryActivateByProcessCandidates(IEnumerable<string> candidates)
        => candidates.Any(TryActivateProcessWindows);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? appId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private static bool IsChromiumWindow(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        return GetClassName(hwnd, className, className.Capacity) > 0 &&
            className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
    }

    private static bool IsMediaBrowserWindow(IntPtr hwnd, string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return false;
        GetWindowThreadProcessId(hwnd, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string processName = process.ProcessName;
            if (!IsBrowserProcess(processName) && !IsChromiumWindow(hwnd)) return false;

            // Match the session owner, including Chromium forks with custom executable
            // names. Window class alone also matches Electron, so it is insufficient.
            if (Regex.IsMatch(sourceAppId, @"(?:^|[\\/.!_\-])" + Regex.Escape(processName) +
                @"(?:$|[\\/.!_\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            uint length = 0;
            const int insufficientBuffer = 122;
            if (GetApplicationUserModelId(process.Handle, ref length, null) != insufficientBuffer ||
                length == 0 || length > 32768) return false;
            var appId = new StringBuilder((int)length);
            return GetApplicationUserModelId(process.Handle, ref length, appId) == 0 &&
                string.Equals(appId.ToString(), sourceAppId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsBrowserMediaSession(string sourceAppId)
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            found = IsWindowVisible(hwnd) && IsMediaBrowserWindow(hwnd, sourceAppId);
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    internal static bool TryGoBackInMediaTab(MediaInfo info)
    {
        string track = NormalizeTitle(info.CurrentTrack);
        if (track.Length < 3) return false;

        // Restrict navigation to the browser owning the media session, and require
        // the actual track title. A platform-only match can select another tab.
        IntPtr targetWindow = IntPtr.Zero;
        AutomationElement? targetTab = null;
        bool ambiguous = false;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                try
                {
                    if (!IsMediaBrowserWindow(hwnd, info.SourceAppId)) return true;

                    var root = AutomationElement.FromHandle(hwnd);
                    var tabs = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                    foreach (AutomationElement tab in tabs)
                    {
                        if (!NormalizeTitle(tab.Current.Name).Contains(track, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (targetTab != null)
                        {
                            ambiguous = true;
                            return false;
                        }
                        targetWindow = hwnd;
                        targetTab = tab;
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Log(LogTag, $"Browser back: window inspection failed: {ex.Message}");
                }
                return true;
            }, IntPtr.Zero);

            if (ambiguous || targetTab == null || !TrySelectTab(targetTab)) return false;
            if (!targetTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) ||
                selection is not SelectionItemPattern selected || !selected.Current.IsSelected)
                return false;

            var window = AutomationElement.FromHandle(targetWindow);
            if (IsChromiumWindow(targetWindow))
            {
                // Chromium handles APPCOMMAND_BROWSER_BACK regardless of UI language
                // or toolbar layout. Address the matched window instead of emitting
                // a global shortcut that could navigate a different application.
                const uint wmAppCommand = 0x0319;
                const int browserBackward = 1;
                return SendMessageTimeout(targetWindow, wmAppCommand, targetWindow,
                    new IntPtr(browserBackward << 16), 0x0002, 500, out _) != IntPtr.Zero;
            }
            // Search browser toolbars only; a web page may also contain Back buttons.
            var toolbars = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
            foreach (AutomationElement toolbar in toolbars)
            {
                bool inDocument = false;
                for (var parent = TreeWalker.ControlViewWalker.GetParent(toolbar);
                     parent != null && !Automation.Compare(parent, window);
                     parent = TreeWalker.ControlViewWalker.GetParent(parent))
                {
                    if (parent.Current.ControlType == ControlType.Document)
                    {
                        inDocument = true;
                        break;
                    }
                }
                if (inDocument) continue;

                var buttons = toolbar.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement button in buttons)
                {
                    string name = button.Current.Name ?? string.Empty;
                    bool isBack = button.Current.AutomationId == "back_button" ||
                        name.Equals("Back", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Back (", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Quay lại", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Quay lại (", StringComparison.OrdinalIgnoreCase);
                    if (!isBack || !button.Current.IsEnabled || button.Current.IsOffscreen) continue;
                    if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) &&
                        pattern is InvokePattern invoke)
                    {
                        invoke.Invoke();
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, $"Browser back failed: {ex.Message}");
        }
        return false;
    }

    private static bool TryActivateProcessWindows(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex.ToString());
            return false;
        }

        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero && TryActivateWindow(hwnd))
                    return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(LogTag, ex.ToString());
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    public static bool TryActivateExactBrowserTab(MediaInfo info, ISet<string> processNames)
    {
        IntPtr bestHwnd = IntPtr.Zero;
        AutomationElement? bestTabItem = null;
        int bestScore = 0;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId == 0) return true;

                string processName;
                try { processName = Process.GetProcessById((int)processId).ProcessName; }
                catch { return true; }

                if (!IsCandidateBrowserWindow(processName, processNames)) return true;

                InspectBrowserWindowTabs(hwnd, info, ref bestHwnd, ref bestTabItem, ref bestScore);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, $"TryActivateExactBrowserTab failed: {ex.Message}");
        }

        if (bestHwnd != IntPtr.Zero && bestTabItem != null && bestScore >= 30)
        {
            bool activated = TryActivateWindow(bestHwnd);
            bool tabSelected = TrySelectTab(bestTabItem);
            return activated || tabSelected;
        }

        return false;
    }

    private static bool TryActivatePipWindow(MediaInfo info)
    {
        string? targetProc = null;
        if (!string.IsNullOrEmpty(info.SourceAppId))
        {
            var match = Regex.Match(info.SourceAppId, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase);
            if (match.Success) targetProc = match.Groups[1].Value;
        }

        if (PipDetector.TryFindPipWindow(out IntPtr pipHwnd, out _, targetProc, info.CurrentTrack))
        {
            BringWindowToTop(pipHwnd);
            return TryActivateWindow(pipHwnd);
        }

        return false;
    }

    private static bool IsCandidateBrowserWindow(string processName, ISet<string> processNames)
    {
        if (!IsBrowserProcess(processName)) return false;
        if (processNames.Count > 0 && processNames.Any(IsBrowserProcess) && !processNames.Contains(processName)) return false;
        return true;
    }

    private static void InspectBrowserWindowTabs(
        IntPtr hwnd,
        MediaInfo info,
        ref IntPtr bestHwnd,
        ref AutomationElement? bestTabItem,
        ref int bestScore)
    {
        try
        {
            var rootElement = AutomationElement.FromHandle(hwnd);
            if (rootElement == null) return;

            var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            var tabs = rootElement.FindAll(TreeScope.Descendants, tabCondition);
            if (tabs == null || tabs.Count == 0) return;

            foreach (AutomationElement tab in tabs)
            {
                string tabTitle = tab.Current.Name ?? string.Empty;
                string tabHelp = tab.Current.HelpText ?? string.Empty;

                int score = ScoreTabItem(tabTitle, tabHelp, info);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHwnd = hwnd;
                    bestTabItem = tab;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, $"Error inspecting browser tabs for HWND {hwnd}: {ex.Message}");
        }
    }

    private static bool TrySelectTab(AutomationElement tabItem)
    {
        try
        {
            if (tabItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern) &&
                pattern is SelectionItemPattern selPattern)
            {
                selPattern.Select();
                return true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, $"SelectionItemPattern failed: {ex.Message}");
        }

        try
        {
            if (tabItem.TryGetCurrentPattern(InvokePattern.Pattern, out object? invPattern) &&
                invPattern is InvokePattern invokePattern)
            {
                invokePattern.Invoke();
                return true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, $"InvokePattern failed: {ex.Message}");
        }

        return false;
    }

    private static int ScoreTabItem(string tabTitle, string tabHelp, MediaInfo info)
    {
        if (string.IsNullOrWhiteSpace(tabTitle) && string.IsNullOrWhiteSpace(tabHelp))
            return 0;

        int score = 0;
        string titleLower = tabTitle.ToLowerInvariant();
        string helpLower = tabHelp.ToLowerInvariant();

        string track = NormalizeTitle(info.CurrentTrack);
        string artist = NormalizeTitle(info.CurrentArtist);
        string youtubeTitle = NormalizeTitle(info.YouTubeTitle);
        string videoId = info.YouTubeVideoId?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!string.IsNullOrEmpty(videoId) && (helpLower.Contains(videoId) || titleLower.Contains(videoId)))
        {
            score += 200;
        }

        if (!string.IsNullOrWhiteSpace(youtubeTitle) && youtubeTitle.Length > 2 && titleLower.Contains(youtubeTitle))
        {
            score += 150;
        }

        if (!string.IsNullOrWhiteSpace(track) && track.Length > 2 && titleLower.Contains(track))
        {
            score += 140;
        }

        if (!string.IsNullOrWhiteSpace(artist) && artist.Length > 2 &&
            artist is not YouTubeToken and not BrowserToken and not SpotifyToken and not SoundCloudToken &&
            titleLower.Contains(artist))
        {
            score += 70;
        }

        score += ScorePlatformKeywords(titleLower, helpLower, info.Platform);
        return score;
    }

    private static int ScorePlatformKeywords(string titleLower, string helpLower, MediaPlatform platform)
    {
        bool Match(string token) => titleLower.Contains(token) || helpLower.Contains(token);

        return platform switch
        {
            MediaPlatform.YouTube when Match(YouTubeToken) => 50,
            MediaPlatform.Twitch when Match("twitch") => 50,
            MediaPlatform.Discord when Match("discord") || titleLower.Contains("vesktop") => 50,
            MediaPlatform.SoundCloud when Match(SoundCloudToken) => 50,
            MediaPlatform.Spotify when Match(SpotifyToken) => 50,
            MediaPlatform.Tidal when Match("tidal") => 50,
            MediaPlatform.Deezer when Match("deezer") => 50,
            MediaPlatform.Bandcamp when Match("bandcamp") => 50,
            MediaPlatform.Netflix when Match("netflix") => 50,
            MediaPlatform.Bilibili when titleLower.Contains("bilibili") || titleLower.Contains("哔哩哔哩") => 50,
            MediaPlatform.Vimeo when Match("vimeo") => 50,
            MediaPlatform.Instagram when Match("instagram") => 50,
            MediaPlatform.Twitter when titleLower.Contains("twitter") || titleLower.Contains("x.com") || titleLower.Contains(" / x") => 50,
            _ => 0
        };
    }

    public static IEnumerable<string> GetProcessCandidates(MediaInfo info)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string sourceAppId = info.SourceAppId ?? string.Empty;

        ExtractProcessesFromSourceAppId(sourceAppId, candidates);
        AppendPlatformProcesses(info, sourceAppId, candidates);
        AppendBrowserProcesses(info, sourceAppId, candidates);

        return candidates;
    }

    private static void ExtractProcessesFromSourceAppId(string sourceAppId, ISet<string> candidates)
    {
        foreach (Match match in Regex.Matches(sourceAppId, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase))
        {
            string name = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                candidates.Add(name);
        }
    }

    private static void AppendPlatformProcesses(MediaInfo info, string sourceAppId, ISet<string> candidates)
    {
        foreach (var (token, platform, procs) in AppPlatformProcessMappings)
        {
            if (sourceAppId.Contains(token, StringComparison.OrdinalIgnoreCase) || info.Platform == platform)
            {
                foreach (var p in procs) candidates.Add(p);
            }
        }

        if (sourceAppId.Contains("vesktop", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("Vesktop");
        }
    }

    private static void AppendBrowserProcesses(MediaInfo info, string sourceAppId, ISet<string> candidates)
    {
        foreach (var browser in KnownBrowserProcesses.Where(b => sourceAppId.Contains(b, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(browser);
        }

        if (info.IsVideoSource || info.Platform == MediaPlatform.SoundCloud)
        {
            foreach (var browser in CommonBrowserProcesses)
                candidates.Add(browser);
        }
    }

    public static bool TryActivateBestMatchingWindow(MediaInfo info, ISet<string> processNames, out bool usedBrowser)
    {
        usedBrowser = false;
        IntPtr bestHwnd = IntPtr.Zero;
        string bestProcessName = string.Empty;
        int bestScore = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0) return true;

            string processName;
            try { processName = Process.GetProcessById((int)processId).ProcessName; }
            catch { return true; }

            if (processNames.Count > 0 && !processNames.Contains(processName)) return true;

            EvaluateWindowCandidate(hwnd, processName, info, ref bestHwnd, ref bestProcessName, ref bestScore);
            return true;
        }, IntPtr.Zero);

        usedBrowser = IsBrowserProcess(bestProcessName);
        return bestHwnd != IntPtr.Zero && TryActivateWindow(bestHwnd);
    }

    private static void EvaluateWindowCandidate(
        IntPtr hwnd,
        string processName,
        MediaInfo info,
        ref IntPtr bestHwnd,
        ref string bestProcessName,
        ref int bestScore)
    {
        string title = GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return;

        int score = ScoreWindow(title, processName, info);
        if (score <= 0 && info.IsVideoSource && IsBrowserProcess(processName)) score = 1;
        if (score > bestScore)
        {
            bestScore = score;
            bestHwnd = hwnd;
            bestProcessName = processName;
        }
    }

    public static bool TryActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOW);

        return SetForegroundWindow(hwnd);
    }

    public static bool IsBrowserProcess(string processName)
        => !string.IsNullOrEmpty(processName) && KnownBrowserProcesses.Contains(processName);

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static int ScoreWindow(string title, string processName, MediaInfo info)
    {
        int score = 0;
        string source = info.MediaSource ?? string.Empty;
        string track = NormalizeTitle(info.CurrentTrack);
        string artist = NormalizeTitle(info.CurrentArtist);
        string window = NormalizeTitle(title);

        if (!string.IsNullOrWhiteSpace(source) && title.Contains(source, StringComparison.OrdinalIgnoreCase)) score += 80;
        if (!string.IsNullOrWhiteSpace(track) && window.Contains(track, StringComparison.OrdinalIgnoreCase)) score += 140;
        if (!string.IsNullOrWhiteSpace(artist) && artist is not YouTubeToken and not BrowserToken && window.Contains(artist, StringComparison.OrdinalIgnoreCase)) score += 70;

        if (PipDetector.IsPipTitle(title)) score += 220;

        score += ScoreWindowPlatform(title, info.Platform);

        if (info.IsVideoSource && IsBrowserProcess(processName)) score += 25;

        return score;
    }

    private static int ScoreWindowPlatform(string title, MediaPlatform platform)
    {
        bool Match(string token) => title.Contains(token, StringComparison.OrdinalIgnoreCase);

        return platform switch
        {
            MediaPlatform.YouTube when Match("YouTube") => 90,
            MediaPlatform.Twitch when Match("Twitch") => 90,
            MediaPlatform.Discord when Match("Discord") || Match("Vesktop") => 90,
            MediaPlatform.SoundCloud when Match("SoundCloud") => 90,
            MediaPlatform.Tidal when Match("TIDAL") => 90,
            MediaPlatform.Deezer when Match("Deezer") => 90,
            MediaPlatform.Bandcamp when Match("Bandcamp") => 90,
            MediaPlatform.Netflix when Match("Netflix") => 90,
            MediaPlatform.Bilibili when Match("bilibili") || title.Contains("哔哩哔哩") => 90,
            MediaPlatform.Vimeo when Match("Vimeo") => 90,
            MediaPlatform.Instagram when Match("Instagram") => 90,
            MediaPlatform.Twitter when Match("Twitter") || Match(" / X") => 90,
            _ => 0
        };
    }

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string normalized = Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+-\s+(youtube|twitch|discord|google chrome|microsoft edge|mozilla firefox|brave|opera|vivaldi|zen|arc|cốc cốc|thorium|waterfox|floorp).*$", "", RegexOptions.IgnoreCase);
        return normalized;
    }
}
