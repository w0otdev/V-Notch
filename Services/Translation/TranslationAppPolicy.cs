using VNotch.Models;

namespace VNotch.Services.Translation;

internal sealed class TranslationAppPolicy
{
    private sealed record Rules(bool IgnoreFullscreen, HashSet<string> ExcludedApps);
    private Rules _rules = new(true, new(StringComparer.OrdinalIgnoreCase));

    internal void Configure(NotchSettings settings) => Volatile.Write(ref _rules,
        new(settings.TranslationIgnoreFullscreen, ParseApps(settings.TranslationExcludedApps)));

    internal static HashSet<string> ParseApps(string? apps) => (apps ?? "")
        .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeName).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeName(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    internal static bool ShouldBlock(bool ignoreFullscreen, bool fullscreen, IEnumerable<string> apps, string processName) =>
        (ignoreFullscreen && fullscreen) || apps.Contains(NormalizeName(processName), StringComparer.OrdinalIgnoreCase);

    internal bool IsBlocked(IntPtr window)
    {
        if (window == IntPtr.Zero) return true;
        var rules = Volatile.Read(ref _rules);
        bool fullscreen = rules.IgnoreFullscreen && FullscreenDetector.IsForegroundWindowFullscreen(window, IntPtr.Zero);
        string name = !fullscreen && rules.ExcludedApps.Count > 0 ? FullscreenDetector.TryGetProcessName(window) : "";
        return ShouldBlock(rules.IgnoreFullscreen, fullscreen, rules.ExcludedApps, name);
    }
}
