namespace VNotch.Services;

internal static class LocalPathPolicy
{
    internal static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\") || path.StartsWith("//")) return true;
        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc) return true;
        }
        catch (Exception)
        {
            // Malformed path strings that fail URI creation cannot be valid UNC paths.
        }
        return false;
    }
}
