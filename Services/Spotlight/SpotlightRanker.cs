using System.IO;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal static class SpotlightRanker
{
    private static readonly HashSet<string> PopularExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows Core Commands & Shells
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "wt", "wt.exe", "bash", "bash.exe", "wsl", "wsl.exe",
        "calc", "calc.exe", "notepad", "notepad.exe",
        "explorer", "explorer.exe", "taskmgr", "taskmgr.exe",
        "regedit", "regedit.exe", "control", "control.exe",
        "mstsc", "mstsc.exe", "mspaint", "mspaint.exe", "paint", "paint.exe",
        "snippingtool", "snippingtool.exe", "cleanmgr", "cleanmgr.exe",
        "dxdiag", "dxdiag.exe", "msconfig", "msconfig.exe",
        "resmon", "resmon.exe", "perfmon", "perfmon.exe",
        "cmdkey", "cmdkey.exe", "ping", "ping.exe", "ipconfig", "ipconfig.exe",
        "systeminfo", "systeminfo.exe", "taskkill", "taskkill.exe",
        "services", "services.msc", "ncpa", "ncpa.cpl",
        "sysdm", "sysdm.cpl", "appwiz", "appwiz.cpl",
        "devmgmt", "devmgmt.msc", "diskmgmt", "diskmgmt.msc",
        "compmgmt", "compmgmt.msc", "eventvwr", "eventvwr.msc",
        "main.cpl", "powercfg.cpl", "firewall.cpl",

        // Common Browsers, Editors & Tools
        "chrome", "chrome.exe", "msedge", "msedge.exe",
        "firefox", "firefox.exe", "brave", "brave.exe", "opera", "opera.exe",
        "code", "code.exe", "devenv", "devenv.exe",
        "rider", "rider64", "rider64.exe", "datagrip", "datagrip64", "datagrip64.exe",
        "pycharm", "pycharm64", "pycharm64.exe", "idea", "idea64", "idea64.exe",
        "discord", "discord.exe", "spotify", "spotify.exe",
        "vlc", "vlc.exe", "steam", "steam.exe",
        "everything", "everything.exe", "putty", "putty.exe",
        "winscp", "winscp.exe", "7z", "7z.exe", "7zfm", "7zfm.exe",
        "python", "python.exe", "node", "node.exe", "git", "git.exe",
        "docker", "docker.exe", "word", "winword", "winword.exe",
        "excel", "excel.exe", "powerpnt", "powerpnt.exe"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".cmd", ".bat", ".msc", ".cpl", ".com", ".ps1", ".appref-ms", ".lnk"
    };

    private static readonly HashSet<string> AssetJunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images & 3D Textures
        ".dds", ".bmp", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".tga", ".tif", ".tiff", ".psd", ".ai",
        // Binaries & Libraries
        ".dll", ".sys", ".drv", ".ocx", ".ax", ".so", ".dylib", ".lib", ".a", ".o", ".obj", ".exp", ".ilk", ".pdb",
        // Data, Cache & Logs
        ".dat", ".tmp", ".temp", ".log", ".cache", ".bak", ".chk", ".dmp", ".swp",
        // Game/Engine Assets & Metadata
        ".meta", ".asset", ".mat", ".prefab", ".unity", ".nfo", ".manifest"
    };

    public static double Score(SpotlightSearchItem item, string query)
    {
        string normalizedQuery = SettingsSearchMatcher.Normalize(query);
        if (normalizedQuery.Length == 0) return 0;

        string title = SettingsSearchMatcher.Normalize(item.Title);
        string subtitle = SettingsSearchMatcher.Normalize(item.Subtitle);

        string titleWithoutExtRaw = GetTitleWithoutAnyExtension(item.Title);
        string titleWithoutExt = SettingsSearchMatcher.Normalize(titleWithoutExtRaw);

        double baseScore = CalculateLexicalScore(title, subtitle, normalizedQuery);
        if (titleWithoutExt.Length > 0 && titleWithoutExt != title)
        {
            double extScore = CalculateLexicalScore(titleWithoutExt, subtitle, normalizedQuery);
            baseScore = Math.Max(baseScore, extScore);
        }

        if (baseScore <= 0) return 0;

        bool isExecutableOrApp = IsExecutableOrApp(item);
        bool isAssetJunk = IsAssetJunk(item);

        if (isExecutableOrApp)
        {
            baseScore += 300;
            if (IsPopularExecutable(item))
            {
                baseScore += 100;
            }
        }
        else if (item.Kind == SpotlightResultKind.Folder)
        {
            baseScore += 50;
        }
        else if (isAssetJunk)
        {
            baseScore = Math.Max(10, baseScore - 300);
        }

        return baseScore;
    }

    private static double CalculateLexicalScore(string title, string subtitle, string normalizedQuery)
    {
        if (title.Length == 0) return 0;

        if (title == normalizedQuery) return 1000;
        if (title.StartsWith(normalizedQuery, StringComparison.Ordinal))
            return 900 - Math.Min(100, title.Length - normalizedQuery.Length);

        string[] words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word == normalizedQuery)) return 850;
        if (words.Any(word => word.StartsWith(normalizedQuery, StringComparison.Ordinal))) return 800;
        if (title.Contains(normalizedQuery, StringComparison.Ordinal)) return 700;
        if (SettingsSearchMatcher.IsNormalizedMatch(title, normalizedQuery)) return 500;
        if (subtitle.Contains(normalizedQuery, StringComparison.Ordinal)) return 350;
        if (SettingsSearchMatcher.IsNormalizedMatch(subtitle, normalizedQuery)) return 250;
        return 0;
    }

    private static string GetTitleWithoutAnyExtension(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        int lastDot = title.LastIndexOf('.');
        if (lastDot <= 0) return title;
        return title[..lastDot];
    }

    private static bool IsExecutableOrApp(SpotlightSearchItem item)
    {
        if (item.Kind == SpotlightResultKind.Application) return true;
        string ext = Path.GetExtension(item.Title);
        if (string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(item.Target))
        {
            ext = Path.GetExtension(item.Target);
        }
        return ExecutableExtensions.Contains(ext);
    }

    private static bool IsAssetJunk(SpotlightSearchItem item)
    {
        string ext = Path.GetExtension(item.Title);
        if (string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(item.Target))
        {
            ext = Path.GetExtension(item.Target);
        }
        return AssetJunkExtensions.Contains(ext);
    }

    private static bool IsPopularExecutable(SpotlightSearchItem item)
    {
        if (PopularExecutables.Contains(item.Title)) return true;

        string titleWithoutExt = GetTitleWithoutAnyExtension(item.Title);
        if (PopularExecutables.Contains(titleWithoutExt)) return true;

        if (!string.IsNullOrEmpty(item.Target))
        {
            string targetName = Path.GetFileName(item.Target);
            if (PopularExecutables.Contains(targetName)) return true;

            string targetWithoutExt = GetTitleWithoutAnyExtension(targetName);
            if (PopularExecutables.Contains(targetWithoutExt)) return true;
        }

        return false;
    }
}
