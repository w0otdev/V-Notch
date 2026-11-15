using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.AiUsage;

// The protocol/bridge already owns a JsonDocument. Read that tree directly:
// no UTF-16 serialization, second JSON parser, or native output-buffer retries.
internal static class AiUsageParsing
{
    internal static AiAccountUsage Codex(JsonElement result, DateTimeOffset now, string? plan, string? account = null)
    {
        var windows = new List<AiQuotaWindow>(4);
        if (Property(result, "rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            // Bound provider-controlled bucket count and retain the established
            // ordering: codex first, then the other buckets in protocol order.
            var entries = buckets.EnumerateObject().Take(16).ToArray();
            foreach (var entry in entries)
                if (entry.NameEquals("codex")) AddBucket(entry.Value, entry.Name, windows);
            foreach (var entry in entries)
                if (!entry.NameEquals("codex")) AddBucket(entry.Value, entry.Name, windows);
        }
        if (windows.Count == 0 && Property(result, "rateLimits", out var legacy))
            AddBucket(legacy, "codex", windows);
        return new("Codex", windows.Count > 0 ? AiUsageState.Ready : AiUsageState.Unavailable, now, windows, plan, account);
    }

    internal static AiAccountUsage Claude(JsonElement root, DateTimeOffset observed)
    {
        var windows = new List<AiQuotaWindow>(2);
        if (Property(root, "rate_limits", out var limits))
        {
            if (Property(limits, "five_hour", out var five))
                AddWindow(five, "used_percentage", "resets_at", "aiUsage.fiveHour", 300, null, windows);
            if (Property(limits, "seven_day", out var seven))
                AddWindow(seven, "used_percentage", "resets_at", "aiUsage.sevenDay", 10080, null, windows);
        }
        return new("Claude", windows.Count > 0 ? AiUsageState.Ready : AiUsageState.Waiting, observed, windows);
    }

    private static void AddBucket(JsonElement bucket, string id, List<AiQuotaWindow> windows)
    {
        string? safeId = id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? id : null;
        AddSlot("primary", "aiUsage.primary");
        AddSlot("secondary", "aiUsage.secondary");

        void AddSlot(string slot, string label)
        {
            if (!Property(bucket, slot, out var value)) return;
            int? duration = Property(value, "windowDurationMins", out var d) && d.ValueKind == JsonValueKind.Number &&
                d.TryGetInt32(out int minutes) && minutes > 0 ? minutes : null;
            AddWindow(value, "usedPercent", "resetsAt", label, duration, safeId, windows);
        }
    }

    private static void AddWindow(JsonElement value, string percentKey, string resetKey, string label,
        int? duration, string? bucket, List<AiQuotaWindow> windows)
    {
        if (!Property(value, percentKey, out var p) || p.ValueKind != JsonValueKind.Number ||
            !p.TryGetDouble(out double percent) || !double.IsFinite(percent) || percent is < 0 or > 100) return;
        DateTimeOffset? reset = null;
        if (Property(value, resetKey, out var r) && r.ValueKind != JsonValueKind.Null)
        {
            if (r.ValueKind != JsonValueKind.Number || !r.TryGetInt64(out long seconds) ||
                seconds is < -62135596800L or > 253402300799L) return;
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        windows.Add(new(label, percent, duration, reset, bucket));
    }

    private static bool Property(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out result);
    }
}
