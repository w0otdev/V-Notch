using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class YouTubeQuotaCooldown
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _blockedKeys = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;

    internal YouTubeQuotaCooldown(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    internal bool IsActive(string apiKey)
    {
        string hash = Hash(apiKey);
        lock (_gate)
            return _blockedKeys.TryGetValue(hash, out var until) && until > _utcNow();
    }

    internal void Trip(string apiKey)
    {
        string hash = Hash(apiKey);
        lock (_gate)
        {
            DateTime now = _utcNow();
            foreach (var expired in _blockedKeys.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                _blockedKeys.Remove(expired);
            if (_blockedKeys.Count >= 32 && !_blockedKeys.ContainsKey(hash))
                _blockedKeys.Remove(_blockedKeys.Keys.First());
            vn_youtube_quota_compute_until_ticks(now.Ticks, out long untilTicks);
            _blockedKeys[hash] = new DateTime(untilTicks, now.Kind);
        }
    }

    private static unsafe string Hash(string key)
    {
        string safeKey = key ?? string.Empty;
        Span<char> hex = stackalloc char[64];
        fixed (char* pKey = safeKey)
        fixed (char* pHex = hex)
        {
            vn_youtube_quota_hash_key(pKey, safeKey.Length, pHex);
        }
        return new string(hex);
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_youtube_quota_hash_key(
        char* key_ptr, int key_len, char* out_hex64);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_youtube_quota_compute_until_ticks(
        long now_ticks, out long out_until_ticks);
}
