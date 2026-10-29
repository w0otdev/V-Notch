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

internal static partial class MediaWindowActivator
{
    private const string LogTag = "MEDIA-ACTIVATOR";
    private const string SpotifyToken = "spotify";

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

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediaactivator_is_browser_process(
        ReadOnlySpan<ushort> procName, int procLen, out byte outIsBrowser);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediaactivator_is_chromium_window(
        IntPtr hwnd, out byte outIsChromium);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediaactivator_try_activate_window(
        IntPtr hwnd, out byte outActivated);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediaactivator_normalize_title(
        ReadOnlySpan<ushort> val, int valLen,
        Span<ushort> outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediaactivator_normalize_media_title(
        ReadOnlySpan<ushort> val, int valLen,
        ReadOnlySpan<ushort> src, int srcLen,
        Span<ushort> outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediaactivator_score_tab_item(
        ReadOnlySpan<ushort> tabTitle, int tabTitleLen,
        ReadOnlySpan<ushort> tabHelp, int tabHelpLen,
        ReadOnlySpan<ushort> track, int trackLen,
        ReadOnlySpan<ushort> artist, int artistLen,
        ReadOnlySpan<ushort> ytTitle, int ytTitleLen,
        ReadOnlySpan<ushort> ytVideoId, int ytVideoIdLen,
        ReadOnlySpan<ushort> mediaSrc, int mediaSrcLen,
        int platform,
        out int outScore);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediaactivator_score_window(
        ReadOnlySpan<ushort> title, int titleLen,
        ReadOnlySpan<ushort> procName, int procLen,
        ReadOnlySpan<ushort> track, int trackLen,
        ReadOnlySpan<ushort> artist, int artistLen,
        ReadOnlySpan<ushort> mediaSrc, int mediaSrcLen,
        int platform,
        byte isVideoSource,
        byte isPipTitle,
        out int outScore);

    public static bool TryActivateForMedia(MediaInfo info)
    {
        if ((info.IsPictureInPicture || info.IsVideoSource) && TryActivatePipWindow(info))
        {
            return true;
        }

        bool preferBrowserTabMatch = info.IsVideoSource || info.Platform == MediaPlatform.SoundCloud;
        var explicitBrowsers = GetExplicitBrowserCandidates(info.SourceAppId ?? string.Empty);
        if (explicitBrowsers.Count > 0)
        {
            if (TryActivateExactBrowserTab(info, explicitBrowsers))
            {
                return true;
            }

            if (preferBrowserTabMatch && TryActivateBestMatchingWindow(info, explicitBrowsers, out _))
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

        if (preferBrowserTabMatch && TryActivateBestMatchingWindow(info, processNames, out _))
        {
            return true;
        }

        if (explicitBrowsers.Count > 0 && TryActivateByProcessCandidates(explicitBrowsers))
        {
            return true;
        }

        if (TryActivateByProcessCandidates(candidates))
        {
            return true;
        }

        return TryActivateBestMatchingWindow(info, processNames, out _);
    }

    private static HashSet<string> GetExplicitBrowserCandidates(string sourceAppId)
    {
        var explicitBrowsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return explicitBrowsers;

        ExtractProcessesFromSourceAppId(sourceAppId, explicitBrowsers);
        explicitBrowsers.RemoveWhere(p => !IsBrowserProcess(p));
        foreach (var browser in KnownBrowserProcesses.Where(b => sourceAppId.Contains(b, StringComparison.OrdinalIgnoreCase)))
        {
            explicitBrowsers.Add(browser);
        }

        return explicitBrowsers;
    }

    private static bool TryActivateByProcessCandidates(IEnumerable<string> candidates)
        => candidates.Any(TryActivateProcessWindows);


    private static bool IsChromiumWindow(IntPtr hwnd)
    {
        return vn_mediaactivator_is_chromium_window(hwnd, out byte isChrom) == 0 && isChrom != 0;
    }

    private static bool IsMediaBrowserWindow(IntPtr hwnd, string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return false;
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0) return false;
        string? resolvedName = ProcessNameResolver.TryGetName(processId);
        if (!string.IsNullOrEmpty(resolvedName) && !IsBrowserProcess(resolvedName) && !IsChromiumWindow(hwnd))
            return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string processName = !string.IsNullOrEmpty(resolvedName) ? resolvedName : process.ProcessName;
            if (!IsBrowserProcess(processName) && !IsChromiumWindow(hwnd)) return false;

            if (Regex.IsMatch(sourceAppId, @"(?:^|[\\/.!_\-])" + Regex.Escape(processName) +
                @"(?:$|[\\/.!_\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                return true;

            return ProcessAppUserModelIdEquals(process.Handle, sourceAppId);
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
        if (!WindowTitleScanner.IsBrowserUrlInspectionAllowed()) return false;
        string track = NormalizeMediaTitle(info.CurrentTrack, info.MediaSource);
        if (track.Length < 3) return false;

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
                    var tabs = WindowTitleScanner.FindBrowserChromeElements(root, ControlType.TabItem);
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
                const uint wmAppCommand = 0x0319;
                const int browserBackward = 1;
                return SendMessageTimeout(targetWindow, wmAppCommand, targetWindow,
                    new IntPtr(browserBackward << 16), 0x0002, 500, out _) != IntPtr.Zero;
            }
            var toolbars = WindowTitleScanner.FindBrowserChromeElements(window, ControlType.ToolBar);
            foreach (AutomationElement toolbar in toolbars)
            {
                var buttons = WindowTitleScanner.FindBrowserChromeElements(toolbar, ControlType.Button);
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

        try
        {
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
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return false;
    }

    public static bool TryActivateExactBrowserTab(MediaInfo info, ISet<string> processNames)
    {
        if (!WindowTitleScanner.IsBrowserUrlInspectionAllowed()) return false;
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

                string? processName = ProcessNameResolver.TryGetName(processId);
                if (string.IsNullOrEmpty(processName)) return true;

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
            var match = Regex.Match(info.SourceAppId, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
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

            var tabs = WindowTitleScanner.FindBrowserChromeElements(rootElement, ControlType.TabItem);
            if (tabs.Count == 0) return;

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
        ReadOnlySpan<ushort> tTitle = MemoryMarshal.Cast<char, ushort>(tabTitle != null ? tabTitle.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> tHelp = MemoryMarshal.Cast<char, ushort>(tabHelp != null ? tabHelp.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> track = MemoryMarshal.Cast<char, ushort>(info.CurrentTrack != null ? info.CurrentTrack.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> artist = MemoryMarshal.Cast<char, ushort>(info.CurrentArtist != null ? info.CurrentArtist.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> ytTitle = MemoryMarshal.Cast<char, ushort>(info.YouTubeTitle != null ? info.YouTubeTitle.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> ytVid = MemoryMarshal.Cast<char, ushort>(info.YouTubeVideoId != null ? info.YouTubeVideoId.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> mSrc = MemoryMarshal.Cast<char, ushort>(info.MediaSource != null ? info.MediaSource.AsSpan() : ReadOnlySpan<char>.Empty);

        vn_mediaactivator_score_tab_item(
            tTitle, tTitle.Length,
            tHelp, tHelp.Length,
            track, track.Length,
            artist, artist.Length,
            ytTitle, ytTitle.Length,
            ytVid, ytVid.Length,
            mSrc, mSrc.Length,
            (int)info.Platform,
            out int score);
        return score;
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
        foreach (Match match in Regex.Matches(sourceAppId, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)))
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

            string? processName = ProcessNameResolver.TryGetName(processId);
            if (string.IsNullOrEmpty(processName)) return true;

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
        return vn_mediaactivator_try_activate_window(hwnd, out byte act) == 0 && act != 0;
    }

    public static bool IsBrowserProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        ReadOnlySpan<ushort> sp = MemoryMarshal.Cast<char, ushort>(processName.AsSpan());
        return vn_mediaactivator_is_browser_process(sp, sp.Length, out byte isBrowser) == 0 && isBrowser != 0;
    }

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
        ReadOnlySpan<ushort> t = MemoryMarshal.Cast<char, ushort>(title != null ? title.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> p = MemoryMarshal.Cast<char, ushort>(processName != null ? processName.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> tr = MemoryMarshal.Cast<char, ushort>(info.CurrentTrack != null ? info.CurrentTrack.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> ar = MemoryMarshal.Cast<char, ushort>(info.CurrentArtist != null ? info.CurrentArtist.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> ms = MemoryMarshal.Cast<char, ushort>(info.MediaSource != null ? info.MediaSource.AsSpan() : ReadOnlySpan<char>.Empty);

        vn_mediaactivator_score_window(
            t, t.Length,
            p, p.Length,
            tr, tr.Length,
            ar, ar.Length,
            ms, ms.Length,
            (int)info.Platform,
            info.IsVideoSource ? (byte)1 : (byte)0,
            PipDetector.IsPipTitle(title ?? string.Empty) ? (byte)1 : (byte)0,
            out int score);
        return score;
    }

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        ReadOnlySpan<ushort> sp = MemoryMarshal.Cast<char, ushort>(value.AsSpan());
        Span<char> buf = stackalloc char[256];
        int rc = vn_mediaactivator_normalize_title(sp, sp.Length, MemoryMarshal.Cast<char, ushort>(buf), buf.Length, out int outLen);
        if (rc == 0) return outLen > 0 ? new string(buf[..outLen]) : string.Empty;
        if (rc == -2 && outLen > 0)
        {
            char[] arr = new char[outLen];
            if (vn_mediaactivator_normalize_title(sp, sp.Length, MemoryMarshal.Cast<char, ushort>(arr.AsSpan()), arr.Length, out int actual) == 0)
                return new string(arr, 0, actual);
        }
        return string.Empty;
    }

    private static string NormalizeMediaTitle(string? value, string? source)
    {
        ReadOnlySpan<ushort> v = MemoryMarshal.Cast<char, ushort>(value != null ? value.AsSpan() : ReadOnlySpan<char>.Empty);
        ReadOnlySpan<ushort> s = MemoryMarshal.Cast<char, ushort>(source != null ? source.AsSpan() : ReadOnlySpan<char>.Empty);
        Span<char> buf = stackalloc char[256];
        int rc = vn_mediaactivator_normalize_media_title(v, v.Length, s, s.Length, MemoryMarshal.Cast<char, ushort>(buf), buf.Length, out int outLen);
        if (rc == 0) return outLen > 0 ? new string(buf[..outLen]) : string.Empty;
        if (rc == -2 && outLen > 0)
        {
            char[] arr = new char[outLen];
            if (vn_mediaactivator_normalize_media_title(v, v.Length, s, s.Length, MemoryMarshal.Cast<char, ushort>(arr.AsSpan()), arr.Length, out int actual) == 0)
                return new string(arr, 0, actual);
        }
        return string.Empty;
    }
}
