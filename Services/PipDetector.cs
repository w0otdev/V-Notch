using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

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

    public static bool IsPipTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        string normalized = title.Trim();

        if (MultilingualPipKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Accept explicit window labels, not a word inside a video/page title
        // such as "How to install packages with pip on Windows".
        return normalized.Equals("PiP", StringComparison.OrdinalIgnoreCase)
            || HasEdgeLabel(normalized, "(PiP)")
            || HasEdgeLabel(normalized, "[PiP]")
            || normalized.EndsWith(" - PiP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasEdgeLabel(string title, string label)
    {
        if (title.Equals(label, StringComparison.OrdinalIgnoreCase)) return true;

        return (title.StartsWith(label, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(title[label.Length]))
            || (title.EndsWith(label, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(title[title.Length - label.Length - 1]));
    }

    public static bool IsKnownBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        string cleaned = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        return KnownBrowserProcesses.Contains(cleaned);
    }

    public static bool IsPipWindow(IntPtr hWnd, string? targetProcessName = null, string? currentTrack = null) =>
        IsPipWindow(hWnd, out _, targetProcessName, currentTrack);

    private static bool IsPipWindow(IntPtr hWnd, out string windowTitle, string? targetProcessName = null, string? currentTrack = null)
    {
        windowTitle = string.Empty;
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
        {
            windowTitle = title;
            return true;
        }

        // If current track is provided and title contains track title in a topmost browser window
        if (!string.IsNullOrWhiteSpace(currentTrack) &&
            !string.IsNullOrWhiteSpace(title) &&
            title.Contains(currentTrack.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            windowTitle = title;
            return true;
        }

        // Browser window classes and dimensions also match ordinary topmost windows.
        // Require a PiP title or the active media title instead of guessing from size.
        return false;
    }

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
                if (IsPipWindow(hWnd, out string title, targetProcessName, currentTrack))
                {
                    foundHwnd = hWnd;
                    foundTitle = title;
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

    private static string? TryGetProcessName(int pid) =>
        pid > 0 ? ProcessNameResolver.TryGetName((uint)pid)?.ToLowerInvariant() : null;

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
