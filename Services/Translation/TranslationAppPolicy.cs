using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Models;

namespace VNotch.Services.Translation;

internal sealed partial class TranslationAppPolicy
{
    private const int ErrBufferTooSmall = -2;
    private sealed record Rules(bool IgnoreFullscreen, HashSet<string> ExcludedApps);
    private Rules _rules = new(true, new(StringComparer.OrdinalIgnoreCase));

    internal void Configure(NotchSettings settings) => Volatile.Write(ref _rules,
        new(settings.TranslationIgnoreFullscreen, ParseApps(settings.TranslationExcludedApps)));

    internal static unsafe HashSet<string> ParseApps(string? apps)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(apps)) return result;

        Span<char> stackBuf = stackalloc char[512];
        int outLen;
        int rc;
        fixed (char* pApps = apps)
        fixed (char* pOut = stackBuf)
        {
            rc = vn_translation_app_parse_joined(pApps, apps.Length, pOut, stackBuf.Length, out outLen);
        }

        if (rc == 0)
        {
            AddJoinedLines(stackBuf[..outLen], result);
            return result;
        }

        if (rc != ErrBufferTooSmall || outLen <= 0) return result;

        char[] rented = ArrayPool<char>.Shared.Rent(outLen);
        try
        {
            fixed (char* pApps = apps)
            fixed (char* pOut = rented)
            {
                rc = vn_translation_app_parse_joined(pApps, apps.Length, pOut, rented.Length, out outLen);
            }
            if (rc == 0 && outLen > 0)
            {
                AddJoinedLines(rented.AsSpan(0, outLen), result);
            }
            return result;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static void AddJoinedLines(ReadOnlySpan<char> joined, HashSet<string> target)
    {
        while (!joined.IsEmpty)
        {
            int nl = joined.IndexOf('\n');
            if (nl < 0)
            {
                target.Add(new string(joined));
                break;
            }
            if (nl > 0)
                target.Add(new string(joined[..nl]));
            joined = joined[(nl + 1)..];
        }
    }

    private static unsafe string NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        fixed (char* ptr = name)
        {
            vn_translation_app_normalize_name(ptr, name.Length, out int trimmedLen);
            return trimmedLen == name.Length ? name : name[..trimmedLen];
        }
    }

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

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_translation_app_normalize_name(
        char* name_ptr, int name_len, out int out_trimmed_len);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_translation_app_parse_joined(
        char* apps_ptr, int apps_len,
        char* out_buf, int out_cap, out int out_len);
}
