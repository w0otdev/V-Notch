using System.IO;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

/// <summary>
/// Persists which Spotlight results the user actually launches so that
/// (a) an empty query can show recent items and (b) habitual items get a
/// bounded ranking boost on top of the lexical score.
/// </summary>
internal sealed class SpotlightUsageStore
{
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
        _utcNow = utcNow;
        _persistSnapshot = persistSnapshot ?? PersistSnapshot;
    }

    public void RecordLaunch(SpotlightSearchItem item)
    {
        if (item.Kind == SpotlightResultKind.Calculation) return;

        lock (_gate)
        {
            var entries = LoadEntries();
            entries.TryGetValue(item.Id, out UsageEntry? existing);
            entries[item.Id] = new UsageEntry
            {
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

    // Captures the current worker so tests can deterministically wait until all
    // changes that were pending at this point have reached stable storage.
    internal Task WaitForPendingSavesAsync()
    {
        lock (_gate)
        {
            return _saveWorker;
        }
    }

    /// <summary>
    /// Bounded frecency bonus added to the lexical score. Capped well below a
    /// single ranking tier jump chain so exact/prefix matches stay dominant.
    /// </summary>
    public double GetBoost(string id)
    {
        UsageEntry? entry;
        lock (_gate)
        {
            LoadEntries().TryGetValue(id, out entry);
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
        return countBoost + recencyBoost;
    }

    /// <summary>
    /// Most recently launched items, newest first, with stale targets dropped.
    /// </summary>
    public IReadOnlyList<SpotlightSearchItem> GetRecentItems(int limit)
    {
        if (limit <= 0) return Array.Empty<SpotlightSearchItem>();

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
            .Take(limit)
            .Select(SpotlightSearchService.LoadIcon)
            .ToArray();
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
            RuntimeLog.Error("SPOTLIGHT-USAGE", ex, $"Failed to read {_path}");
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
                    "SPOTLIGHT-USAGE",
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
                RuntimeLog.Error("SPOTLIGHT-USAGE", ex, $"Failed to write {_path}");
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
        public SpotlightResultKind Kind { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public int Count { get; set; }
        public DateTime LastLaunchedUtc { get; set; }
    }
}
