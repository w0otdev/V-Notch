using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class VideoIdCache
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_video_id_cache_should_evict_oldest(
        int currentCount,
        int capacity,
        int orderCount,
        out byte outEvict);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_video_id_cache_should_evict_entry(
        ReadOnlySpan<char> cachedVideoId,
        int cachedLen,
        ReadOnlySpan<char> staleVideoId,
        int staleLen,
        out byte outEvict);

    private const int Capacity = 50;
    private const int MismatchCapacity = Capacity * 2;

    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly object _lock = new();

    private readonly HashSet<string> _mismatch = new(StringComparer.Ordinal);
    private readonly Queue<string> _mismatchOrder = new();

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    public void Set(string? trackIdentity, string videoId)
    {
        if (string.IsNullOrEmpty(trackIdentity)) return;
        lock (_lock)
        {
            if (!_cache.ContainsKey(trackIdentity))
            {
                while (vn_video_id_cache_should_evict_oldest(_cache.Count, Capacity, _order.Count, out byte evict) == 0 && evict != 0)
                {
                    string oldest = _order.Dequeue();
                    if (_cache.Remove(oldest))
                        break;
                }
                _order.Enqueue(trackIdentity);
            }
            _cache[trackIdentity] = videoId;
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
                _order.Clear();
                return;
            }

            var doomed = _cache.Keys
                .Where(key => !string.Equals(key, currentTrackIdentity, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in doomed)
            {
                _cache.Remove(key);
            }
            _order.Clear();
            foreach (var key in _cache.Keys)
            {
                _order.Enqueue(key);
            }
        }
    }

    public void Evict(string? track, string staleVideoId)
    {
        if (string.IsNullOrEmpty(track) || string.IsNullOrEmpty(staleVideoId)) return;
        lock (_lock)
        {
            string trackIdentity = MediaHeuristics.BuildTrackIdentity(track, "");
            ReadOnlySpan<char> staleSpan = staleVideoId.AsSpan();
            if (_cache.TryGetValue(track, out var v1))
            {
                ReadOnlySpan<char> v1Span = v1.AsSpan();
                if (vn_video_id_cache_should_evict_entry(v1Span, v1Span.Length, staleSpan, staleSpan.Length, out byte evict1) == 0 && evict1 != 0)
                {
                    _cache.Remove(track);
                }
            }
            if (_cache.TryGetValue(trackIdentity, out var v2))
            {
                ReadOnlySpan<char> v2Span = v2.AsSpan();
                if (vn_video_id_cache_should_evict_entry(v2Span, v2Span.Length, staleSpan, staleSpan.Length, out byte evict2) == 0 && evict2 != 0)
                {
                    _cache.Remove(trackIdentity);
                }
            }
        }
    }

    public bool IsMismatch(string videoId)
    {
        lock (_mismatch) return _mismatch.Contains(videoId);
    }

    public void MarkMismatch(string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return;
        lock (_mismatch)
        {
            if (_mismatch.Contains(videoId)) return;
            while (vn_video_id_cache_should_evict_oldest(_mismatch.Count, MismatchCapacity, _mismatchOrder.Count, out byte evict) == 0 && evict != 0)
            {
                string oldest = _mismatchOrder.Dequeue();
                if (_mismatch.Remove(oldest))
                    break;
            }
            if (_mismatch.Add(videoId))
            {
                _mismatchOrder.Enqueue(videoId);
            }
        }
    }

    public void ClearMismatches()
    {
        lock (_mismatch)
        {
            _mismatch.Clear();
            _mismatchOrder.Clear();
        }
    }
}
