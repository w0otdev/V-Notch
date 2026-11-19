using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed partial class SpotlightUsageStore : IAsyncDisposable, IDisposable
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
    // Launches made before the on-disk baseline becomes readable. Bounded by
    // the same retention policy as normal history and never persisted on their own.
    private Dictionary<string, UsageEntry>? _deferredEntries;
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
            if (normalizedQuery.Length > 256) normalizedQuery = normalizedQuery[..256];
            var queries = existing?.Queries != null ? new List<string>(existing.Queries) : new List<string>();
            if (normalizedQuery.Length > 0)
            {
                foreach (var entry in entries.Values) entry.Queries?.Remove(normalizedQuery);
                queries.Remove(normalizedQuery);
                queries.Insert(0, normalizedQuery);
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

            TrimEntries(entries);
            _changeVersion++;
            StartSaveWorker();
        }
    }

    private static void TrimEntries(Dictionary<string, UsageEntry> entries)
    {
        if (entries.Count <= MaxEntries) return;
        var oldest = entries.ToArray();
        Array.Sort(oldest, s_oldestAscComparison);
        for (int i = 0; i < oldest.Length - MaxEntries; i++)
            entries.Remove(oldest[i].Key);
    }

    private void StartSaveWorker()
    {
        if (_saveWorkerRunning) return;
        _saveWorkerRunning = true;
        _saveWorker = Task.Run(SaveLoop);
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (DirectoryNotFoundException) { /* No history directory yet. */ }
            catch (Exception ex)
            {
                RuntimeLog.Warn(LogTag, $"Failed to delete history file: {ex.Message}");
                throw;
            }
            _entries = new(StringComparer.Ordinal);
            _deferredEntries = null;
            _changeVersion++;
        }
    }

    // Captures the current worker so tests can deterministically wait until all
    // changes that were pending at this point have reached stable storage.
    internal async Task WaitForPendingSavesAsync()
    {
        Task pending;
        lock (_gate)
        {
            if (_deferredEntries is { Count: > 0 }) StartSaveWorker();
            pending = _saveWorker;
        }
        await pending.ConfigureAwait(false);
        lock (_gate)
        {
            if (_deferredEntries is { Count: > 0 })
                throw new IOException("Spotlight launch history is still unavailable. Pending launches can be retried.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await WaitForPendingSavesAsync().ConfigureAwait(false); }
        finally { await Preferences.FlushAsync().ConfigureAwait(false); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

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
            if (!Preferences.IsExcluded(item.Target) && SpotlightLauncher.IsValidTarget(item) && ShouldShowHistoryItem(item, query))
            {
                output.Add(item);
            }
        }

        return output.Count == 0 ? Array.Empty<SpotlightSearchItem>() : output;
    }

    public IReadOnlyList<SpotlightSearchItem> GetRecentItems(int limit, bool loadIconsSynchronously = false)
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

            if (Preferences.IsExcluded(item.Target) || !SpotlightLauncher.IsValidTarget(item)) continue;

            if (!ShouldShowHistoryItem(item, string.Empty)) continue;

            output.Add(loadIconsSynchronously ? SpotlightSearchService.LoadIcon(item) : item);
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
            _entries = JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            _entries = new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_deferredEntries == null) RuntimeLog.Error(LogTag, ex, $"Could not read {_path}; deferring saves");
            return _deferredEntries ??= new(StringComparer.Ordinal);
        }

        if (_deferredEntries is { Count: > 0 } pending)
        {
            MergeDeferredLaunches(_entries, pending);
            StartSaveWorker();
        }
        _deferredEntries = null;
        return _entries;
    }

    private static void MergeDeferredLaunches(Dictionary<string, UsageEntry> history, Dictionary<string, UsageEntry> pending)
    {
        // A remembered query belongs to the most recently launched item that used it.
        var reassigned = pending.Values.SelectMany(entry => entry.Queries).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in history.Values) entry?.Queries?.RemoveAll(reassigned.Contains);

        foreach (var (id, recent) in pending)
        {
            if (history.TryGetValue(id, out var previous) && previous != null)
            {
                recent.Count += previous.Count;
                if (previous.Queries != null)
                    recent.Queries.AddRange(previous.Queries.Take(20 - recent.Queries.Count));
            }
            history[id] = recent;
        }
        TrimEntries(history);
    }

    private async Task SaveLoop()
    {
        // Give short sharing violations time to clear even if Spotlight is idle.
        // A longer outage retains the bounded overlay for the next lookup, launch or flush.
        for (int attempt = 0; ; attempt++)
        {
            lock (_gate)
            {
                LoadEntries();
                if (_entries != null) break;
                if (attempt == 3)
                {
                    _saveWorkerRunning = false;
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100 << attempt)).ConfigureAwait(false);
        }

        while (true)
        {
            long version;
            string json = string.Empty;
            Exception? serializationError = null;

            lock (_gate)
            {
                version = _changeVersion;
                try
                {
                    json = JsonSerializer.Serialize(_entries);
                }
                catch (Exception ex)
                {
                    // Clear this while holding the same gate RecordLaunch uses,
                    // so a later launch cannot miss scheduling a replacement.
                    _saveWorkerRunning = false;
                    serializationError = ex;
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
                if (_changeVersion != version) return;
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
