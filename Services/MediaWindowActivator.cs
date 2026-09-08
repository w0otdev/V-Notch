using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            MediaPlatform.Facebook when Match("facebook") => 50,
            MediaPlatform.TikTok when Match("tiktok") => 50,
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
            MediaPlatform.Facebook when Match("Facebook") => 90,
            MediaPlatform.TikTok when Match("TikTok") => 90,
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
