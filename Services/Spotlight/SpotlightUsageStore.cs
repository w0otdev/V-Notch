using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed partial class SpotlightUsageStore
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_usage_compute_boost(
        int count,
        long lastLaunchedUtcTicks,
        long nowUtcTicks,
        byte remembered,
        out double outBoost);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_usage_is_allowed_app_history_target(
        ReadOnlySpan<char> targetPtr, int targetLen,
        ReadOnlySpan<char> startMenuPtr, int startMenuLen,
        ReadOnlySpan<char> commonStartMenuPtr, int commonStartMenuLen,
        out byte outAllowed);

    private static readonly string s_startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
    private static readonly string s_commonStartMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    private static readonly Comparison<KeyValuePair<string, UsageEntry>> s_recentDescComparison =
        static (a, b) => b.Value.LastLaunchedUtc.CompareTo(a.Value.LastLaunchedUtc);
    private static readonly Comparison<KeyValuePair<string, UsageEntry>> s_oldestAscComparison =
        static (a, b) => a.Value.LastLaunchedUtc.CompareTo(b.Value.LastLaunchedUtc);

    public SpotlightPreferences Preferences { get; }
    private volatile bool _enabled = true;
    public bool Enabled { get => _enabled; set => _enabled = value; }

    private const string LogTag = "SPOTLIGHT-USAGE";
    private const int MaxEntries = 100;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _persistSnapshot;
    private Dictionary<string, UsageEntry>? _entries;
    private long _changeVersion;
    private bool _saveWorkerRunning;
    private Task _saveWorker = Task.CompletedTask;

    public SpotlightUsageStore()
        : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "V-Notch", "spotlight-usage.json"),
            () => DateTime.UtcNow)
    {
    }

    // Test seam: production callers always use the APPDATA location and wall clock.
    internal SpotlightUsageStore(string path, Func<DateTime> utcNow)
        : this(path, utcNow, persistSnapshot: null)
    {
    }

    // Test seam: allows save interleavings to be controlled without touching
    // the production APPDATA file.
    internal SpotlightUsageStore(
        string path,
        Func<DateTime> utcNow,
        Action<string>? persistSnapshot)
    {
        _path = path;
        Preferences = new SpotlightPreferences(Path.ChangeExtension(path, ".preferences.json"));
        _utcNow = utcNow;
        _persistSnapshot = persistSnapshot ?? PersistSnapshot;
    }

    public void RecordLaunch(SpotlightSearchItem item, string query = "")
    {
        if (!Enabled || item.Kind is SpotlightResultKind.Calculation or SpotlightResultKind.SystemAction) return;

        lock (_gate)
        {
            var entries = LoadEntries();
            entries.TryGetValue(item.Id, out UsageEntry? existing);
            string normalizedQuery = SettingsSearchMatcher.Normalize(query);
            var queries = existing?.Queries != null ? new List<string>(existing.Queries) : new List<string>();
            if (normalizedQuery.Length > 0)
            {
                foreach (var entry in entries.Values) entry.Queries?.Remove(normalizedQuery);
                queries.Remove(normalizedQuery);
                queries.Insert(0, normalizedQuery.Length > 256 ? normalizedQuery[..256] : normalizedQuery);
                if (queries.Count > 20) queries.RemoveRange(20, queries.Count - 20);
            }
            entries[item.Id] = new UsageEntry
            {
                Queries = queries,
                Kind = item.Kind,
                Title = item.Title,
                Subtitle = item.Subtitle,
                Target = item.Target,
                IconPath = item.IconPath,
                Count = (existing?.Count ?? 0) + 1,
                LastLaunchedUtc = _utcNow()
            };

            if (entries.Count > MaxEntries)
            {
                var allPairs = new KeyValuePair<string, UsageEntry>[entries.Count];
                int idx = 0;
                foreach (var pair in entries)
                    allPairs[idx++] = pair;

                Array.Sort(allPairs, s_oldestAscComparison);
                int toRemove = entries.Count - MaxEntries;
                for (int i = 0; i < toRemove; i++)
                {
                    entries.Remove(allPairs[i].Key);
                }
            }

            _changeVersion++;
            if (!_saveWorkerRunning)
            {
                _saveWorkerRunning = true;
                _saveWorker = Task.Run(SaveLoop);
            }
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _entries = new Dictionary<string, UsageEntry>(StringComparer.Ordinal);
            _changeVersion++;
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception ex)
            {
                // Best-effort file deletion; in-memory state is already cleared
                RuntimeLog.Warn(LogTag, $"Failed to delete history file: {ex.Message}");
            }
        }
    }

    // Captures the current worker so tests can deterministically wait until all
    // changes that were pending at this point have reached stable storage.
    internal Task WaitForPendingSavesAsync()
    {
        lock (_gate)
        {
            return _saveWorker;
        }
    }

    public double GetBoost(string id, string query = "")
    {
        if (!Enabled) return 0;
        return GetBoostNormalized(id, SettingsSearchMatcher.Normalize(query));
    }

    internal double GetBoostNormalized(string id, string normalizedQuery)
    {
        if (!Enabled) return 0;
        UsageEntry? entry;
        bool remembered;
        lock (_gate)
        {
            LoadEntries().TryGetValue(id, out entry);
            remembered = entry?.Queries != null && normalizedQuery.Length > 0 && entry.Queries.Contains(normalizedQuery);
        }
        if (entry == null) return 0;

        _ = vn_spotlight_usage_compute_boost(
            entry.Count,
            entry.LastLaunchedUtc.Ticks,
            _utcNow().Ticks,
            remembered ? (byte)1 : (byte)0,
            out double boost);
        return boost;
    }

    public IReadOnlyList<SpotlightSearchItem> GetRememberedItems(string query)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(query)) return Array.Empty<SpotlightSearchItem>();
        string normalized = SettingsSearchMatcher.Normalize(query);
        if (normalized.Length == 0) return Array.Empty<SpotlightSearchItem>();

        List<KeyValuePair<string, UsageEntry>>? matched = null;
        lock (_gate)
        {
            var entries = LoadEntries();
            foreach (var pair in entries)
            {
                if (pair.Value.Queries?.Contains(normalized) == true)
                {
                    matched ??= new List<KeyValuePair<string, UsageEntry>>(4);
                    matched.Add(pair);
                }
            }
        }

        if (matched == null || matched.Count == 0)
            return Array.Empty<SpotlightSearchItem>();

        var output = new List<SpotlightSearchItem>(matched.Count);
        for (int i = 0; i < matched.Count; i++)
        {
            var pair = matched[i];
            var item = new SpotlightSearchItem(pair.Key, pair.Value.Kind, pair.Value.Title,
                pair.Value.Subtitle, pair.Value.Target, pair.Value.IconPath);
            item = item with { Score = Math.Max(1500, SpotlightRanker.Score(item, query)) };
            if (SpotlightLauncher.IsValidTarget(item) && ShouldShowHistoryItem(item, query))
            {
                output.Add(item);
            }
        }

        return output.Count == 0 ? Array.Empty<SpotlightSearchItem>() : output;
    }

    public IReadOnlyList<SpotlightSearchItem> GetRecentItems(int limit)
    {
        if (!Enabled || limit <= 0) return Array.Empty<SpotlightSearchItem>();

        KeyValuePair<string, UsageEntry>[] snapshot;
        lock (_gate)
        {
            var entries = LoadEntries();
            if (entries.Count == 0) return Array.Empty<SpotlightSearchItem>();
            snapshot = new KeyValuePair<string, UsageEntry>[entries.Count];
            int idx = 0;
            foreach (var pair in entries)
                snapshot[idx++] = pair;
        }

        Array.Sort(snapshot, s_recentDescComparison);

        var output = new List<SpotlightSearchItem>(Math.Min(limit, snapshot.Length));
        for (int i = 0; i < snapshot.Length && output.Count < limit; i++)
        {
            var pair = snapshot[i];
            var item = new SpotlightSearchItem(
                pair.Key, pair.Value.Kind, pair.Value.Title, pair.Value.Subtitle,
                pair.Value.Target, pair.Value.IconPath)
            {
                IsRecent = true
            };

            if (!SpotlightLauncher.IsValidTarget(item)) continue;
            if (Preferences.IsExcluded(item.Target)) continue;
            if (!ShouldShowHistoryItem(item, string.Empty)) continue;

            output.Add(SpotlightSearchService.LoadIcon(item));
        }

        return output.Count == 0 ? Array.Empty<SpotlightSearchItem>() : output;
    }

    private bool ShouldShowHistoryItem(SpotlightSearchItem item, string query)
    {
        if (item.Kind == SpotlightResultKind.SystemAction) return false;
        // A document explicitly opened by the user is useful history even if
        // it came from a temporary/download cache. Discovery filters apply to
        // unsolicited results, not to that deliberate choice.
        if (item.Kind == SpotlightResultKind.File
            && !SpotlightFileVisibility.HiddenExtensions.Contains(Path.GetExtension(item.Target)))
            return true;
        if (SpotlightSystemCatalog.IsKnownTarget(item) || Preferences.IsPinned(item.Target)
            || !string.IsNullOrWhiteSpace(Preferences.GetAlias(item.Target))) return true;
        if (item.Kind == SpotlightResultKind.Application
            && vn_spotlight_usage_is_allowed_app_history_target(
                item.Target.AsSpan(), item.Target.Length,
                s_startMenuPath.AsSpan(), s_startMenuPath.Length,
                s_commonStartMenuPath.AsSpan(), s_commonStartMenuPath.Length,
                out byte allowed) == 0 && allowed != 0)
        {
            return true;
        }
        return SpotlightFileVisibility.ShouldInclude(item, query);
    }

    private Dictionary<string, UsageEntry> LoadEntries()
    {
        if (_entries != null) return _entries;

        try
        {
            if (File.Exists(_path))
            {
                _entries = JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(
                    File.ReadAllText(_path)) ?? new Dictionary<string, UsageEntry>();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, $"Failed to read {_path}");
        }
        return _entries ??= new Dictionary<string, UsageEntry>();
    }

    private void SaveLoop()
    {
        while (true)
        {
            long version;
            int entryCount;
            string json = string.Empty;
            Exception? serializationError = null;

            lock (_gate)
            {
                version = _changeVersion;
                var entries = LoadEntries();
                entryCount = entries.Count;
                if (entryCount > 0)
                {
                    try
                    {
                        json = JsonSerializer.Serialize(entries);
                    }
                    catch (Exception ex)
                    {
                        // Clear this while holding the same gate RecordLaunch uses,
                        // so a later launch cannot miss scheduling a replacement.
                        _saveWorkerRunning = false;
                        serializationError = ex;
                    }
                }
            }

            if (serializationError != null)
            {
                RuntimeLog.Error(
                    LogTag,
                    serializationError,
                    $"Failed to serialize {_path}");
                return;
            }

            if (entryCount > 0)
            {
                try
                {
                    if (_persistSnapshot == PersistSnapshot)
                        PersistSnapshotVersioned(json, version);
                    else
                        _persistSnapshot(json);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error(LogTag, ex, $"Failed to write {_path}");
                }
            }

            lock (_gate)
            {
                if (_changeVersion == version)
                {
                    _saveWorkerRunning = false;
                    return;
                }
            }
        }
    }

    private void PersistSnapshot(string json) => PersistSnapshotVersioned(json, Interlocked.Read(ref _changeVersion));

    private void PersistSnapshotVersioned(string json, long version)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // A unique temporary name also avoids collisions if a second store
        // instance briefly targets the same file (for example during reload).
        string tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            lock (_gate)
            {
                if (_changeVersion != version || _entries is { Count: 0 }) return;
                File.Move(tempPath, _path, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup; the write/move exception is the useful
                // failure for the caller to log.
            }
        }
    }

    internal sealed class UsageEntry
    {
        public List<string> Queries { get; set; } = new();
        public SpotlightResultKind Kind { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public int Count { get; set; }
        public DateTime LastLaunchedUtc { get; set; }
    }
}
