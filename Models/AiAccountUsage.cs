namespace VNotch.Models;

internal enum AiUsageState { Disabled, PrivacyBlocked, Loading, Ready, NotInstalled, SignedOut, Unsupported, Waiting, Unavailable }

internal sealed record AiQuotaWindow(string LabelKey, double UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt, string? Bucket = null)
{
    // A passed reset is not evidence that a provider has replenished an allowance.
    internal bool IsExpired(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
    internal double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record AiAccountUsage(string Provider, AiUsageState State, DateTimeOffset? UpdatedAt = null,
    IReadOnlyList<AiQuotaWindow>? Windows = null, string? Plan = null, string? AccountLabel = null)
{
    internal bool IsStale(DateTimeOffset now) => UpdatedAt is not { } updated ||
        now - updated > TimeSpan.FromMinutes(10) || updated > now.AddMinutes(1);
}
