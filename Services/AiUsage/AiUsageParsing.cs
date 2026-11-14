using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.AiUsage;

internal static partial class AiUsageParsing
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct VnAiParsedWindow
    {
        public double used_percent;
        public long reset_unix_seconds;
        public int duration_minutes;
        public byte has_duration;
        public byte has_reset;
        public byte label_kind;
        public byte has_bucket;
        public byte bucket_len;
        public fixed byte reserved[3];
        public fixed byte bucket_ascii[64];
    }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_ai_usage_parse_codex(
        ReadOnlySpan<char> jsonPtr,
        int jsonLen,
        Span<VnAiParsedWindow> outWindows,
        int windowCap,
        out int outCount);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_ai_usage_parse_claude(
        ReadOnlySpan<char> jsonPtr,
        int jsonLen,
        Span<VnAiParsedWindow> outWindows,
        int windowCap,
        out int outCount);

    internal static AiAccountUsage Codex(JsonElement result, DateTimeOffset now, string? plan, string? account = null)
    {
        string raw = result.GetRawText();
        ReadOnlySpan<char> span = raw.AsSpan();
        Span<VnAiParsedWindow> stackBuf = stackalloc VnAiParsedWindow[32];
        int rc = vn_ai_usage_parse_codex(span, span.Length, stackBuf, stackBuf.Length, out int count);
        var windows = new List<AiQuotaWindow>(count > 0 ? count : 0);
        if (rc == 0 && count > 0)
        {
            AppendWindows(stackBuf[..count], windows);
        }
        else if (rc == -2 && count > stackBuf.Length)
        {
            var heapBuf = new VnAiParsedWindow[count];
            if (vn_ai_usage_parse_codex(span, span.Length, heapBuf, heapBuf.Length, out count) == 0 && count > 0)
                AppendWindows(heapBuf.AsSpan(0, count), windows);
        }
        return new("Codex", windows.Count > 0 ? AiUsageState.Ready : AiUsageState.Unavailable, now, windows, plan, account);
    }

    internal static AiAccountUsage Claude(JsonElement root, DateTimeOffset observed)
    {
        string raw = root.GetRawText();
        ReadOnlySpan<char> span = raw.AsSpan();
        Span<VnAiParsedWindow> buf = stackalloc VnAiParsedWindow[4];
        int rc = vn_ai_usage_parse_claude(span, span.Length, buf, buf.Length, out int count);
        var windows = new List<AiQuotaWindow>(count > 0 ? count : 0);
        if (rc == 0 && count > 0)
            AppendWindows(buf[..count], windows);
        return new("Claude", windows.Count > 0 ? AiUsageState.Ready : AiUsageState.Waiting, observed, windows);
    }

    private static unsafe void AppendWindows(ReadOnlySpan<VnAiParsedWindow> parsed, List<AiQuotaWindow> target)
    {
        for (int i = 0; i < parsed.Length; i++)
        {
            VnAiParsedWindow w = parsed[i];
            string label = w.label_kind switch
            {
                0 => "aiUsage.primary",
                1 => "aiUsage.secondary",
                2 => "aiUsage.fiveHour",
                _ => "aiUsage.sevenDay"
            };
            int? duration = w.has_duration != 0 ? w.duration_minutes : null;
            DateTimeOffset? reset = w.has_reset != 0 ? DateTimeOffset.FromUnixTimeSeconds(w.reset_unix_seconds) : null;
            string? bucket = w.has_bucket == 0
                ? null
                : (w.bucket_len == 0 ? string.Empty : Encoding.ASCII.GetString(w.bucket_ascii, w.bucket_len));
            target.Add(new AiQuotaWindow(label, w.used_percent, duration, reset, bucket));
        }
    }
}
