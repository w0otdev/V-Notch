using System.IO;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.AiUsage;

internal sealed partial class ClaudeUsageBridge
{
    internal static string DataPath => Path.Combine(AppPaths.LocalAppDataRoot, "ai-usage", "claude.json");

    internal static AiAccountUsage Read(string? path = null, DateTimeOffset? now = null)
    {
        path ??= DataPath;
        if (!File.Exists(path)) return new("Claude", AiUsageState.Waiting);
        using var json = JsonDocument.Parse(AiUsageFiles.Read(path, 8192));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("observed_at", out var date) || date.ValueKind != JsonValueKind.String ||
            !date.TryGetDateTimeOffset(out var observed) || observed > (now ?? DateTimeOffset.UtcNow).AddMinutes(1))
            return new("Claude", AiUsageState.Waiting);
        return AiUsageParsing.Claude(root, observed);
    }
}
