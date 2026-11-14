using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VNotch.Models;

namespace VNotch.Services.AiUsage;

internal sealed record AiQuotaAlert(string Provider, AiQuotaWindow Window, int ThresholdPercent);

// Marshalling facade; threshold decisions and notification history live in Native.
internal sealed partial class AiQuotaNotifications : IDisposable
{
    private TrackerHandle? _tracker;

    internal void Evaluate(AiAccountUsage usage, DateTimeOffset now, List<AiQuotaAlert> alerts)
    {
        if (usage.Windows == null) return;
        if (_tracker == null)
        {
            if (vn_ai_quota_notifications_create(out var handle) != 0) return;
            _tracker = new TrackerHandle(handle);
        }
        foreach (var window in usage.Windows)
        {
            string account = usage.AccountLabel ?? "";
            string bucket = window.Bucket ?? "";
            if (vn_ai_quota_notifications_evaluate(_tracker, usage.Provider == "Claude" ? 1 : 0, (int)usage.State,
                account.AsSpan(), account.Length, bucket.AsSpan(), bucket.Length,
                window.LabelKey.AsSpan(), window.LabelKey.Length, window.DurationMinutes ?? 0, window.UsedPercent,
                window.ResetsAt?.UtcTicks ?? 0, usage.UpdatedAt?.UtcTicks ?? 0, now.UtcTicks, out int threshold) == 0 && threshold != 0)
                alerts.Add(new(usage.Provider, window, threshold));
        }
    }

    public void Dispose() => _tracker?.Dispose();

    private sealed class TrackerHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TrackerHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => vn_ai_quota_notifications_destroy(handle) == 0;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_ai_quota_notifications_create(out IntPtr tracker);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_ai_quota_notifications_destroy(IntPtr tracker);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_ai_quota_notifications_evaluate(
        TrackerHandle tracker, int provider, int state,
        ReadOnlySpan<char> account, int accountLength,
        ReadOnlySpan<char> bucket, int bucketLength,
        ReadOnlySpan<char> label, int labelLength,
        int durationMinutes, double usedPercent, long resetUtcTicks, long updatedUtcTicks, long nowUtcTicks,
        out int threshold);
}
