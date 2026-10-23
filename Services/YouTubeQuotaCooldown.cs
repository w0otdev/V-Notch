using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

// Quota belongs to the credential that received the error, including late responses
// from requests that were started before the user replaced that credential.
internal sealed class YouTubeQuotaCooldown
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
            _blockedKeys[hash] = now.Date.AddDays(1).AddHours(8);
        }
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));
}
