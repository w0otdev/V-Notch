using System.IO;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightPreferences
{
    public const int MaxPinnedApps = 10;

    private readonly object _gate = new();
    private readonly string _path;
    private State? _state;
    private string? _pendingSave;
    private bool _saving;

    internal SpotlightPreferences(string path) => _path = path;

    internal sealed class AppPreference
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Target { get; set; } = "";
        public string? IconPath { get; set; }
        public string Alias { get; set; } = "";
        public bool Pinned { get; set; }
    }

    internal sealed class State
    {
        public List<AppPreference> Apps { get; set; } = new();
        public List<string> ExcludedFolders { get; set; } = new();
    }

    private State Data
    {
        get
        {
            if (_state != null) return _state;
            try
            {
                if (File.Exists(_path)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            }
            catch (Exception ex) { RuntimeLog.Error("SPOTLIGHT-PREFERENCES", ex, "Failed to load preferences"); }
            _state ??= new State();
            _state.Apps ??= new();
            _state.ExcludedFolders ??= new();
            _state.Apps.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Target));
            int pinnedCount = 0;
            foreach (var app in _state.Apps)
            {
                app.Alias ??= "";
                if (app.Pinned && ++pinnedCount > MaxPinnedApps) app.Pinned = false;
            }
            _state.ExcludedFolders = _state.ExcludedFolders.Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Select(folder =>
                {
                    try { return NormalizeFolder(folder); }
                    catch (Exception) { return ""; }
                }).Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return _state;
        }
    }

    public bool IsPinned(string target)
    {
        lock (_gate) return Data.Apps.Any(a => a.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && a.Pinned);
    }

    public string GetAlias(string target)
    {
        lock (_gate) return Data.Apps.FirstOrDefault(a => a.Target.Equals(target, StringComparison.OrdinalIgnoreCase))?.Alias ?? "";
    }

    public bool SetApp(SpotlightSearchItem item, bool pinned, string alias)
    {
        if (item.Kind != SpotlightResultKind.Application) return false;
        lock (_gate)
        {
            bool alreadyPinned = Data.Apps.Any(a => a.Pinned && a.Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase));
            if (pinned && !alreadyPinned && Data.Apps.Count(a => a.Pinned) >= MaxPinnedApps) return false;
            Data.Apps.RemoveAll(a => a.Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase));
            if (pinned || !string.IsNullOrWhiteSpace(alias))
                Data.Apps.Add(new AppPreference
                {
                    Id = item.Id,
                    Title = item.Title,
                    Target = item.Target,
                    IconPath = item.IconPath,
                    Pinned = pinned,
                    Alias = alias.Trim()[..Math.Min(alias.Trim().Length, 80)]
                });
            Save();
            return true;
        }
    }

    public string[] GetExcludedFolders()
    {
        lock (_gate) return Data.ExcludedFolders.ToArray();
    }

    public void SetExcludedFolders(IEnumerable<string> folders)
    {
        // Validate the whole edit before changing state; offline drives are valid.
        var normalized = folders.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizeFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
        {
            Data.ExcludedFolders = normalized;
            Save();
        }
    }

    private static string NormalizeFolder(string folder)
    {
        folder = Environment.ExpandEnvironmentVariables(folder.Trim());
        if (!Path.IsPathFullyQualified(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || folder.Contains('*') || folder.Contains('?')) throw new ArgumentException("Invalid folder path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    public bool IsExcluded(string target)
    {
        if (!Path.IsPathFullyQualified(target)) return false;
        string path;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)); }
        catch (Exception) { return false; }
        lock (_gate) return Data.ExcludedFolders.Any(folder =>
            path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    public SpotlightSearchItem Decorate(SpotlightSearchItem item) => item with { IsPinned = IsPinned(item.Target) };

    public IReadOnlyList<SpotlightSearchItem> Search(string query)
    {
        AppPreference[] apps;
        lock (_gate) apps = Data.Apps.ToArray();
        string normalized = SettingsSearchMatcher.Normalize(query);
        return apps.Select(app =>
        {
            var item = new SpotlightSearchItem(app.Id, SpotlightResultKind.Application, app.Title,
                Loc.Get("spotlight.kind.application"), app.Target, app.IconPath)
            { IsPinned = app.Pinned };
            string alias = SettingsSearchMatcher.Normalize(app.Alias);
            double score = normalized.Length == 0 ? (app.Pinned ? 1000 : 0) : SpotlightRanker.Score(item, query);
            if (normalized.Length > 0 && alias.Length > 0 && alias.StartsWith(normalized, StringComparison.Ordinal))
                score = Math.Max(score, alias == normalized ? 2000 : 1100);
            return item with { Score = score > 0 && app.Pinned ? score + 150 : score };
        }).Where(item => item.Score > 0 && !IsExcluded(item.Target) && SpotlightLauncher.IsValidTarget(item)).ToArray();
    }

    private void Save()
    {
        _pendingSave = JsonSerializer.Serialize(Data);
        if (_saving) return;
        _saving = true;
        _ = Task.Run(() =>
        {
            while (true)
            {
                string json;
                lock (_gate)
                {
                    if (_pendingSave == null) { _saving = false; return; }
                    json = _pendingSave;
                    _pendingSave = null;
                }
                string temp = _path + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllText(temp, json);
                    File.Move(temp, _path, true);
                }
                catch (Exception ex) { RuntimeLog.Error("SPOTLIGHT-PREFERENCES", ex, "Failed to save preferences"); }
            }
        });
    }
}
