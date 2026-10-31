using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed partial class SpotlightPreferences
{
    public const int MaxPinnedApps = 10;

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_prefs_has_valid_folder_syntax(
        ReadOnlySpan<char> folderPtr,
        int folderLen,
        out byte outValid);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_prefs_is_path_under_folder(
        ReadOnlySpan<char> pathPtr,
        int pathLen,
        ReadOnlySpan<char> folderPtr,
        int folderLen,
        out byte outIsUnder);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_prefs_compute_score(
        double baseScore,
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        ReadOnlySpan<char> aliasPtr,
        int aliasLen,
        byte isPinned,
        out double outScore);

    private readonly object _gate = new();
    private readonly string _path;
    private State? _state;
    private volatile string[]? _excludedFoldersSnapshot;
    private Func<string, bool> _directoryExistsProbe = Directory.Exists;
    private string? _pendingSave;
    private bool _saving;
    private Task _saveTask = Task.CompletedTask;

    internal SpotlightPreferences(string path) => _path = path;

    internal Func<string, bool> DirectoryExistsProbe
    {
        get => _directoryExistsProbe;
        set
        {
            lock (_gate)
            {
                _directoryExistsProbe = value ?? Directory.Exists;
                _excludedFoldersSnapshot = null;
            }
        }
    }

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

            var cleanedFolders = new List<string>(_state.ExcludedFolders.Count);
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _state.ExcludedFolders.Count; i++)
            {
                string folder = _state.ExcludedFolders[i];
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    string normalized = NormalizeFolder(folder);
                    if (seenFolders.Add(normalized))
                        cleanedFolders.Add(normalized);
                }
                catch (Exception)
                {
                    // Skip malformed excluded folder entries when loading preferences.
                }
            }

            _state.ExcludedFolders = cleanedFolders;
            _excludedFoldersSnapshot = null;
            return _state;
        }
    }

    public bool IsPinned(string target)
    {
        lock (_gate)
        {
            var apps = Data.Apps;
            for (int i = 0; i < apps.Count; i++)
            {
                var a = apps[i];
                if (a.Pinned && a.Target.Equals(target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    public string GetAlias(string target)
    {
        lock (_gate)
        {
            var apps = Data.Apps;
            for (int i = 0; i < apps.Count; i++)
            {
                var a = apps[i];
                if (a.Target.Equals(target, StringComparison.OrdinalIgnoreCase))
                    return a.Alias;
            }
            return "";
        }
    }

    public bool SetApp(SpotlightSearchItem item, bool pinned, string alias)
    {
        if (item.Kind != SpotlightResultKind.Application) return false;
        lock (_gate)
        {
            var apps = Data.Apps;
            bool alreadyPinned = false;
            int pinnedCount = 0;
            for (int i = 0; i < apps.Count; i++)
            {
                var a = apps[i];
                if (a.Pinned)
                {
                    pinnedCount++;
                    if (a.Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase))
                        alreadyPinned = true;
                }
            }

            if (pinned && !alreadyPinned && pinnedCount >= MaxPinnedApps) return false;
            apps.RemoveAll(a => a.Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase));
            if (pinned || !string.IsNullOrWhiteSpace(alias))
            {
                string trimmedAlias = alias.Trim();
                apps.Add(new AppPreference
                {
                    Id = item.Id,
                    Title = item.Title,
                    Target = item.Target,
                    IconPath = item.IconPath,
                    Pinned = pinned,
                    Alias = trimmedAlias.Length > 80 ? trimmedAlias[..80] : trimmedAlias
                });
            }
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
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in folders)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string norm = NormalizeFolder(p);
            if (seen.Add(norm))
                normalized.Add(norm);
        }

        lock (_gate)
        {
            Data.ExcludedFolders = normalized;
            _excludedFoldersSnapshot = null;
            Save();
        }
    }

    public void AddExcludedFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string norm = NormalizeFolder(folder);
        lock (_gate)
        {
            var list = Data.ExcludedFolders;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Equals(norm, StringComparison.OrdinalIgnoreCase))
                {
                    _excludedFoldersSnapshot = null;
                    return;
                }
            }
            list.Add(norm);
            _excludedFoldersSnapshot = null;
            Save();
        }
    }

    public bool RemoveExcludedFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string norm = NormalizeFolder(folder);
        lock (_gate)
        {
            int removed = Data.ExcludedFolders.RemoveAll(f => f.Equals(norm, StringComparison.OrdinalIgnoreCase));
            _excludedFoldersSnapshot = null;
            if (removed == 0) return false;
            Save();
            return true;
        }
    }

    public void ResetExcludedFolders()
    {
        lock (_gate)
        {
            Data.ExcludedFolders.Clear();
            _excludedFoldersSnapshot = null;
            Save();
        }
    }

    private static string NormalizeFolder(string folder)
    {
        folder = Environment.ExpandEnvironmentVariables(folder.Trim());
        if (!Path.IsPathFullyQualified(folder) ||
            vn_spotlight_prefs_has_valid_folder_syntax(folder.AsSpan(), folder.Length, out byte valid) != 0 ||
            valid == 0)
            throw new ArgumentException("Invalid folder path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    internal string[] GetExcludedFoldersSnapshot()
    {
        var snapshot = _excludedFoldersSnapshot;
        if (snapshot != null) return snapshot;
        lock (_gate)
        {
            if (_excludedFoldersSnapshot != null) return _excludedFoldersSnapshot;
            var folders = Data.ExcludedFolders;
            var probe = _directoryExistsProbe;
            if (probe != Directory.Exists)
            {
                if (folders.Count == 0)
                {
                    _ = probe(_path);
                }
                else
                {
                    for (int i = 0; i < folders.Count; i++)
                        _ = probe(folders[i]);
                }
            }
            _excludedFoldersSnapshot = folders.Count == 0 ? Array.Empty<string>() : folders.ToArray();
            return _excludedFoldersSnapshot;
        }
    }

    public bool IsExcluded(string target)
    {
        string[] folders = GetExcludedFoldersSnapshot();
        if (folders.Length == 0) return false;
        if (!Path.IsPathFullyQualified(target)) return false;

        string path;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)); }
        catch (Exception) { return false; }

        ReadOnlySpan<char> pathSpan = path.AsSpan();
        for (int i = 0; i < folders.Length; i++)
        {
            string folder = folders[i];
            if (vn_spotlight_prefs_is_path_under_folder(pathSpan, pathSpan.Length, folder.AsSpan(), folder.Length, out byte isUnder) == 0 && isUnder != 0)
                return true;
        }
        return false;
    }

    public SpotlightSearchItem Decorate(SpotlightSearchItem item) => item with { IsPinned = IsPinned(item.Target) };

    public IReadOnlyList<SpotlightSearchItem> Search(string query)
    {
        AppPreference[] apps;
        lock (_gate)
        {
            var list = Data.Apps;
            if (list.Count == 0) return Array.Empty<SpotlightSearchItem>();
            apps = list.ToArray();
        }

        string normalized = SettingsSearchMatcher.Normalize(query);
        string appKindLabel = Loc.Get("spotlight.kind.application");
        var results = new List<SpotlightSearchItem>(apps.Length);

        for (int i = 0; i < apps.Length; i++)
        {
            var app = apps[i];
            var item = new SpotlightSearchItem(app.Id, SpotlightResultKind.Application, app.Title,
                appKindLabel, app.Target, app.IconPath)
            { IsPinned = app.Pinned };
            string alias = SettingsSearchMatcher.Normalize(app.Alias);
            double baseScore = normalized.Length == 0 ? 0 : SpotlightRanker.Score(item, query);
            _ = vn_spotlight_prefs_compute_score(
                baseScore,
                normalized.AsSpan(), normalized.Length,
                alias.AsSpan(), alias.Length,
                app.Pinned ? (byte)1 : (byte)0,
                out double finalScore);

            if (finalScore > 0 && !IsExcluded(item.Target) && SpotlightLauncher.IsValidTarget(item))
            {
                results.Add(item with { Score = finalScore });
            }
        }

        return results.Count == 0 ? Array.Empty<SpotlightSearchItem>() : results;
    }

    private void Save()
    {
        _pendingSave = JsonSerializer.Serialize(Data);
        if (_saving) return;
        _saving = true;
        _saveTask = Task.Run(() =>
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
                finally
                {
                    try { File.Delete(temp); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort cleanup of temporary preferences file. */ }
                }
            }
        });
    }

    internal Task FlushAsync() { lock (_gate) return _saveTask; }
}
