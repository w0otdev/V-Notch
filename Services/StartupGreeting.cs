namespace VNotch.Services;

internal static class StartupGreeting
{
    // Vietnamese uses Xin Chao; every other locale falls back to Hello.
    internal static bool UsesVietnamese(string? language) =>
        string.Equals(language?.Trim(), "vi", StringComparison.OrdinalIgnoreCase);

    internal static bool UsesEnglishHandwriting(string? language) => !UsesVietnamese(language);
}
