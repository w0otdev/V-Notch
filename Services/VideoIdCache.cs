namespace VNotch.Services;

internal sealed class VideoIdCache
{
    private const int Capacity = 50;

    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private readonly HashSet<string> _mismatch = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    public void Set(string? trackIdentity, string videoId)
    {
        if (string.IsNullOrEmpty(trackIdentity)) return;
        lock (_lock)
        {
            _cache[trackIdentity] = videoId;
            if (_cache.Count > Capacity)
            {
                var firstKey = _cache.Keys.First();
                _cache.Remove(firstKey);
            }
        }
    }

    public string? Get(string? track)
    {
        if (string.IsNullOrEmpty(track)) return null;
        lock (_lock)
        {
            if (_cache.TryGetValue(track, out var id))
                return id;
            string trackIdentity = MediaHeuristics.BuildTrackIdentity(track, "");
            if (_cache.TryGetValue(trackIdentity, out id))
                return id;
            return null;
        }
    }

    public void ForgetExcept(string? currentTrackIdentity)
    {
        lock (_lock)
        {
            if (_cache.Count == 0) return;
            if (string.IsNullOrEmpty(currentTrackIdentity))
            {
                _cache.Clear();
                return;
            }

            var doomed = _cache.Keys
                .Where(key => !string.Equals(key, currentTrackIdentity, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in doomed)
            {
                _cache.Remove(key);
            }
        }
    }

    public void Evict(string? track, string staleVideoId)
    {
        if (string.IsNullOrEmpty(track) || string.IsNullOrEmpty(staleVideoId)) return;
        lock (_lock)
        {
            string trackIdentity = MediaHeuristics.BuildTrackIdentity(track, "");
            if (_cache.TryGetValue(track, out var v1) &&
                string.Equals(v1, staleVideoId, StringComparison.Ordinal))
            {
                _cache.Remove(track);
            }
            if (_cache.TryGetValue(trackIdentity, out var v2) &&
                string.Equals(v2, staleVideoId, StringComparison.Ordinal))
            {
                _cache.Remove(trackIdentity);
            }
        }
    }

    public bool IsMismatch(string videoId)
    {
        lock (_mismatch) return _mismatch.Contains(videoId);
    }

    public void MarkMismatch(string videoId)
    {
        lock (_mismatch) _mismatch.Add(videoId);
    }

    public void ClearMismatches()
    {
        lock (_mismatch) _mismatch.Clear();
    }
}
