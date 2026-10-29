using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services;

public partial class MediaSourceCache
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_media_source_cache_resolve_capacity(
        int capacity,
        int defaultCapacity,
        out int outCapacity);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_source_cache_matches_source(
        ReadOnlySpan<char> cachedSource,
        int cachedLen,
        ReadOnlySpan<char> expectedSource,
        int expectedLen,
        out byte outMatches);

    [LibraryImport(NativeDll)]
    private static partial int vn_media_source_cache_should_evict(
        int currentCount,
        int capacity,
        byte hasLast,
        out byte outEvict);

    private const int DefaultCapacity = 500;

    private readonly string _cachePath;
    private readonly int _capacity;

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);

    private readonly object _lock = new();
    private bool _isDirty;

    public MediaSourceCache(int capacity = DefaultCapacity)
    {
        int fallbackCap = capacity > 0 ? capacity : DefaultCapacity;
        _capacity = vn_media_source_cache_resolve_capacity(capacity, DefaultCapacity, out int resolved) == 0 ? resolved : fallbackCap;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "V-Notch");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "source_cache.json");
    }

    public MediaSourceCache(string cachePath, int capacity = DefaultCapacity)
    {
        int fallbackCap = capacity > 0 ? capacity : DefaultCapacity;
        _capacity = vn_media_source_cache_resolve_capacity(capacity, DefaultCapacity, out int resolved) == 0 ? resolved : fallbackCap;
        _cachePath = cachePath;
        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    public void SetBoth(string fullIdentity, string trackOnlyIdentity, string source)
    {
        if (string.IsNullOrEmpty(source)) return;

        lock (_lock)
        {
            bool changed = false;

            if (!string.IsNullOrEmpty(fullIdentity))
                changed |= SetEntry(fullIdentity, source);

            if (!string.IsNullOrEmpty(trackOnlyIdentity))
                changed |= SetEntry(trackOnlyIdentity, source);

            if (changed)
            {
                _isDirty = true;
                Evict();
            }
        }
    }

    public bool HasSource(string fullIdentity, string trackOnlyIdentity, string expectedSource)
    {
        lock (_lock)
        {
            ReadOnlySpan<char> expectedSpan = expectedSource is not null ? expectedSource.AsSpan() : ReadOnlySpan<char>.Empty;
            if (!string.IsNullOrEmpty(fullIdentity) &&
                _cache.TryGetValue(fullIdentity, out var s1) &&
                vn_media_source_cache_matches_source(s1.AsSpan(), s1.Length, expectedSpan, expectedSpan.Length, out byte m1) == 0 &&
                m1 != 0)
            {
                Touch(fullIdentity);
                return true;
            }

            if (!string.IsNullOrEmpty(trackOnlyIdentity) &&
                _cache.TryGetValue(trackOnlyIdentity, out var s2) &&
                vn_media_source_cache_matches_source(s2.AsSpan(), s2.Length, expectedSpan, expectedSpan.Length, out byte m2) == 0 &&
                m2 != 0)
            {
                Touch(trackOnlyIdentity);
                return true;
            }

            return false;
        }
    }

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    public int Capacity => _capacity;

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lru.Clear();
            _nodes.Clear();
            _isDirty = true;
        }
    }

    public void Load()
    {
        if (!File.Exists(_cachePath)) return;
        try
        {
            var json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data != null)
            {
                lock (_lock)
                {
                    _cache.Clear();
                    _lru.Clear();
                    _nodes.Clear();
                    foreach (var kvp in data)
                        SetEntry(kvp.Key, kvp.Value);
                    Evict();
                    _isDirty = false;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MEDIA-SOURCE-CACHE", $"Failed to load cache from '{_cachePath}': {ex.Message}");
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (!_isDirty) return;
            string tempPath = _cachePath + ".tmp";
            try
            {
                // Serialize from least-recently to most-recently used so Load() restores exact LRU order.
                var ordered = new List<KeyValuePair<string, string>>(_lru.Count);
                for (var node = _lru.Last; node != null; node = node.Previous)
                {
                    if (_cache.TryGetValue(node.Value, out var val))
                        ordered.Add(new KeyValuePair<string, string>(node.Value, val));
                }
                var json = JsonSerializer.Serialize(new Dictionary<string, string>(ordered, StringComparer.Ordinal));
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _cachePath, overwrite: true);
                _isDirty = false;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { /* Ignore temp delete error */ }
                RuntimeLog.Warn("MEDIA-SOURCE-CACHE", $"Failed to save cache to '{_cachePath}': {ex.Message}");
            }
        }
    }

    public bool TryGet(string key, out string? value)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var v))
            {
                Touch(key);
                value = v;
                return true;
            }
            value = null;
            return false;
        }
    }

    private bool SetEntry(string key, string source)
    {
        bool changed = !_cache.TryGetValue(key, out var existing) ||
                       !string.Equals(existing, source, StringComparison.Ordinal);

        _cache[key] = source;
        Touch(key);
        return changed;
    }

    private void Touch(string key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            if (!ReferenceEquals(_lru.First, node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _isDirty = true;
            }
        }
        else
        {
            _nodes[key] = _lru.AddFirst(key);
        }
    }

    private void Evict()
    {
        while (vn_media_source_cache_should_evict(_cache.Count, _capacity, _lru.Last is not null ? (byte)1 : (byte)0, out byte evict) == 0 && evict != 0 && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _nodes.Remove(last.Value);
            _cache.Remove(last.Value);
            _isDirty = true;
        }
    }
}
