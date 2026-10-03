namespace VNotch.Services;

internal static class StartupGreeting
{
    // Product policy: Vietnamese handwriting for vi, English for every other locale.
    internal static bool UsesVietnamese(string? language) =>
        string.Equals(language?.Trim(), "vi", StringComparison.OrdinalIgnoreCase);
}
