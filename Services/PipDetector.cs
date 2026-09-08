using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

/// <summary>
/// Provides detection and lookup for Picture-in-Picture (PiP) video windows across
/// web browsers (Chromium, Firefox, WebKit forks) and desktop media players.
/// </summary>
public static class PipDetector
{
    private static readonly string[] MultilingualPipKeywords =
    {
        "picture in picture",
        "picture-in-picture",
        "hình trong hình",
        "hinh trong hinh",
        "bild-in-bild",
        "image dans l'image",
        "pantalla en pantalla",
        "cuadro en cuadro",
        "画中画",
        "畫中畫",
        "子母画面",
        "子母畫面",
        "ピクチャー イン ピクチャー",
        "ピクチャーインピクチャー",
        "картинка в картинке",
        "imagem na imagem",
        "imagem sobre imagem",
        "finestra mobile",
        "gambar dalam gambar",
        "resim içinde resim",
        "화면 속 화면"
    };

    private static readonly HashSet<string> KnownBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "zen",
        "arc", "coccoc", "sidekick", "thorium", "waterfox", "floorp", "librewolf",
        "chromium", "whale", "yandex", "wavebox", "helium", "supermium", "browser"
    };

    private static readonly HashSet<string> PipWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome_WidgetWin_1",
        "MozillaDialogClass",
        "MozillaWindowClass",
        "ApplicationFrameWindow",
        "Windows.UI.Core.CoreWindow"
    };

    /// <summary>
    /// Checks whether a window title represents a Picture-in-Picture window.
    /// </summary>
    public static bool IsPipTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        string normalized = title.Trim().ToLowerInvariant();

        if (MultilingualPipKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check for isolated "pip" token (e.g., "(PiP)", "[PiP]", "Video - PiP")
        if (Regex.IsMatch(normalized, @"(^|[\s\(\[\-_])pip([\s\)\]\-_]|$)", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether the given process name belongs to a known browser capable of PiP.
    /// </summary>
    public static bool IsKnownBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        string cleaned = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        return KnownBrowserProcesses.Contains(cleaned);
    }

    /// <summary>
    /// Determines whether an HWND represents an active, visible Picture-in-Picture window.
    /// </summary>
    public static bool IsPipWindow(IntPtr hWnd, string? targetProcessName = null, string? currentTrack = null)
    {
        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd) || IsIconic(hWnd))
            return false;

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOPMOST) == 0)
            return false;

        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0)
            return false;

        string? procName = TryGetProcessName((int)pid);
        if (string.IsNullOrEmpty(procName))
            return false;

        bool procMatches = !string.IsNullOrEmpty(targetProcessName) &&
                           (string.Equals(procName, targetProcessName, StringComparison.OrdinalIgnoreCase) ||
                            targetProcessName.Contains(procName, StringComparison.OrdinalIgnoreCase));

        if (!procMatches && !IsKnownBrowserProcess(procName))
            return false;

        string title = GetWindowTitle(hWnd);
        if (IsPipTitle(title))
            return true;

        // If current track is provided and title contains track title in a topmost browser window
        if (!string.IsNullOrWhiteSpace(currentTrack) &&
            !string.IsNullOrWhiteSpace(title) &&
            title.Contains(currentTrack.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Document PiP or custom PiP window fallback:
        // Topmost browser window with known PiP class and typical floating overlay dimensions
        string className = GetWindowClassName(hWnd);
        if (PipWindowClasses.Contains(className) &&
            !string.IsNullOrWhiteSpace(title) &&
            GetWindowRect(hWnd, out RECT rect))
        {
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            // Floating video PiP windows typically have dimensions between 100x60 and 1600x1200
            if (width is >= 100 and <= 1600 && height is >= 60 and <= 1200)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Searches active top-level windows for an open Picture-in-Picture window.
    /// </summary>
    public static bool TryFindPipWindow(
        out IntPtr pipHwnd,
        out string pipTitle,
        string? targetProcessName = null,
        string? currentTrack = null)
    {
        IntPtr foundHwnd = IntPtr.Zero;
        string foundTitle = string.Empty;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (IsPipWindow(hWnd, targetProcessName, currentTrack))
                {
                    foundHwnd = hWnd;
                    foundTitle = GetWindowTitle(hWnd);
                    return false; // Stop enumeration
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PIP-DETECTOR", $"EnumWindows failed: {ex.Message}");
        }

        pipHwnd = foundHwnd;
        pipTitle = foundTitle;
        return foundHwnd != IntPtr.Zero;
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) > 0)
            return sb.ToString();
        return string.Empty;
    }
}
