using System.IO;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightUsageStore
{
    public SpotlightPreferences Preferences { get; }
    private volatile bool _enabled = true;
    public bool Enabled { get => _enabled; set => _enabled = value; }

    private const string LogTag = "SPOTLIGHT-USAGE";
    private const int MaxEntries = 100;
    private const double CountBoostCap = 90;
    private const double CountBoostFactor = 22;

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
            var queries = new List<string>(existing?.Queries ?? new());
            if (normalizedQuery.Length > 0)
            {
                foreach (var entry in entries.Values) entry.Queries?.Remove(normalizedQuery);
                queries.Remove(normalizedQuery);
                queries.Insert(0, normalizedQuery[..Math.Min(256, normalizedQuery.Length)]);
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
                foreach (string stale in entries
                             .OrderBy(pair => pair.Value.LastLaunchedUtc)
                             .Take(entries.Count - MaxEntries)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    entries.Remove(stale);
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
        UsageEntry? entry;
        bool remembered;
        lock (_gate)
        {
            LoadEntries().TryGetValue(id, out entry);
            remembered = entry?.Queries?.Contains(SettingsSearchMatcher.Normalize(query)) == true;
        }
        if (entry == null) return 0;

        double countBoost = Math.Min(CountBoostCap, CountBoostFactor * Math.Log2(1 + entry.Count));
        double ageDays = Math.Max(0, (_utcNow() - entry.LastLaunchedUtc).TotalDays);
        double recencyBoost = ageDays switch
        {
            < 1 => 35,
            < 3 => 25,
            < 7 => 15,
            < 30 => 5,
            _ => 0
        };
        return countBoost + recencyBoost + (remembered ? 350 : 0);
    }

    public IReadOnlyList<SpotlightSearchItem> GetRememberedItems(string query)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(query)) return Array.Empty<SpotlightSearchItem>();
        string normalized = SettingsSearchMatcher.Normalize(query);
        KeyValuePair<string, UsageEntry>[] entries;
        lock (_gate) entries = LoadEntries().Where(pair => pair.Value.Queries?.Contains(normalized) == true).ToArray();
        return entries.Select(pair => new SpotlightSearchItem(pair.Key, pair.Value.Kind, pair.Value.Title,
            pair.Value.Subtitle, pair.Value.Target, pair.Value.IconPath)
        {
            Score = Math.Max(1500, SpotlightRanker.Score(new SpotlightSearchItem(pair.Key, pair.Value.Kind,
                pair.Value.Title, pair.Value.Subtitle, pair.Value.Target), query))
        }).Where(SpotlightLauncher.IsValidTarget)
            .Where(item => ShouldShowHistoryItem(item, query)).ToArray();
    }

    public IReadOnlyList<SpotlightSearchItem> GetRecentItems(int limit)
    {
        if (!Enabled || limit <= 0) return Array.Empty<SpotlightSearchItem>();

        List<KeyValuePair<string, UsageEntry>> snapshot;
        lock (_gate)
        {
            snapshot = LoadEntries().ToList();
        }

        return snapshot
            .OrderByDescending(pair => pair.Value.LastLaunchedUtc)
            .Select(pair => new SpotlightSearchItem(
                pair.Key, pair.Value.Kind, pair.Value.Title, pair.Value.Subtitle,
                pair.Value.Target, pair.Value.IconPath)
            {
                IsRecent = true
            })
            .Where(SpotlightLauncher.IsValidTarget)
            .Where(item => !Preferences.IsExcluded(item.Target))
            .Where(item => ShouldShowHistoryItem(item, string.Empty))
            .Take(limit)
            .Select(SpotlightSearchService.LoadIcon)
            .ToArray();
    }

    private bool ShouldShowHistoryItem(SpotlightSearchItem item, string query)
    {
        if (item.Kind == SpotlightResultKind.SystemAction) return false;
        // A document explicitly opened by the user is useful history even if
        // it came from a temporary/download cache. Discovery filters apply to
        // unsolicited results, not to that deliberate choice.
        if (item.Kind == SpotlightResultKind.File
            && !SpotlightFileVisibility.HiddenExtensions.Contains(Path.GetExtension(item.Target), StringComparer.OrdinalIgnoreCase))
            return true;
        if (SpotlightSystemCatalog.IsKnownTarget(item) || Preferences.IsPinned(item.Target)
            || !string.IsNullOrWhiteSpace(Preferences.GetAlias(item.Target))) return true;
        if (item.Kind == SpotlightResultKind.Application)
        {
            if (item.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var location in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
            {
                string root = Environment.GetFolderPath(location);
                if (root.Length > 0 && item.Target.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return true;
            }
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
            string json = string.Empty;
            Exception? serializationError = null;

            lock (_gate)
            {
                version = _changeVersion;
                try
                {
                    json = JsonSerializer.Serialize(LoadEntries());
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

    private void PersistSnapshot(string json)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // A unique temporary name also avoids collisions if a second store
        // instance briefly targets the same file (for example during reload).
        string tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
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
