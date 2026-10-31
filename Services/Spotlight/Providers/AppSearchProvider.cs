using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using VNotch.Controllers;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed partial class AppSearchProvider : ISpotlightProvider
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_search_provider_classify_target(
        ReadOnlySpan<char> pathPtr, int pathLen,
        ReadOnlySpan<char> appIdPtr, int appIdLen,
        byte isUnc,
        byte existsOnDisk,
        out int outMode);

    private readonly object _indexGate = new();
    private Task<IReadOnlyList<SpotlightSearchItem>>? _index;
    private DateTime _indexExpiresUtc;
    private readonly Func<IReadOnlyList<SpotlightSearchItem>> _buildIndex;
    private readonly Func<DateTime> _utcNow;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    public AppSearchProvider() : this(BuildIndex, () => DateTime.UtcNow)
    {
    }

    internal AppSearchProvider(Func<IReadOnlyList<SpotlightSearchItem>> buildIndex, Func<DateTime> utcNow)
    {
        _buildIndex = buildIndex;
        _utcNow = utcNow;
    }

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var apps = await WarmupAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string subtitle = Loc.Get("spotlight.kind.application");
        var matches = new List<SpotlightSearchItem>();
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            double score = SpotlightRanker.Score(app, query);
            if (score > 0)
            {
                matches.Add(app with { Subtitle = subtitle, Score = score });
            }
        }
        if (matches.Count == 0) return Array.Empty<SpotlightSearchItem>();
        return matches
            .OrderByDescending(app => app.Score)
            .ThenBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    internal Task<IReadOnlyList<SpotlightSearchItem>> WarmupAsync()
    {
        lock (_indexGate)
        {
            if (_index == null || _index.IsCompleted && (_utcNow() >= _indexExpiresUtc || _index.IsFaulted))
            {
                _index = Task.Run(_buildIndex);
                _indexExpiresUtc = _utcNow().AddMinutes(1);
            }
            return _index;
        }
    }

    private static IReadOnlyList<SpotlightSearchItem> BuildIndex()
    {
        var apps = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);
        AddStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        AddStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        AddAppsFolderItems(apps);

        // Decode icons lazily in Merge for displayed results only, avoiding
        // upfront decoding overhead for hundreds of unused app bitmaps.
        return apps.Values.ToArray();
    }

    private static void AddStartMenuShortcuts(
        IDictionary<string, SpotlightSearchItem> apps,
        string startMenu)
    {
        string programs = Path.Combine(startMenu, "Programs");
        if (!Directory.Exists(programs)) return;

        try
        {
            foreach (string shortcut in Directory.EnumerateFiles(programs, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }))
            {
                string title = Path.GetFileNameWithoutExtension(shortcut);
                AddIfMissing(apps, new SpotlightSearchItem(
                    $"app:{shortcut}", SpotlightResultKind.Application, title,
                    Loc.Get("spotlight.kind.application"), shortcut, shortcut));
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-APP-INDEX", ex, $"Failed to read Start Menu: {programs}");
        }
    }

    private static void AddAppsFolderItems(IDictionary<string, SpotlightSearchItem> apps)
    {
        object? shellObject = null;
        object? folderObject = null;
        object? itemsObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;

            shellObject = Activator.CreateInstance(shellType);
            if (shellObject == null) return;
            dynamic shell = shellObject;
            folderObject = shell.NameSpace("shell:AppsFolder");
            if (folderObject == null) return;
            dynamic folder = folderObject;
            itemsObject = folder.Items();
            if (itemsObject is not IEnumerable items) return;

            foreach (object itemObject in items)
            {
                try
                {
                    dynamic item = itemObject;
                    string name = ReadString(() => item.Name);
                    string path = ReadString(() => item.Path);
                    string appId = ReadString(() => item.ExtendedProperty("System.AppUserModel.ID"));
                    string target = ResolveTarget(path, appId);
                    if (name.Length == 0 || target.Length == 0) continue;

                    AddIfMissing(apps, new SpotlightSearchItem(
                        $"app:{target}", SpotlightResultKind.Application, name,
                        Loc.Get("spotlight.kind.application"), target,
                        File.Exists(path) ? path : null));
                }
                catch
                {
                    // One broken shell item must not discard the rest of the app index.
                }
                finally
                {
                    ReleaseComObject(itemObject);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-APP-INDEX", ex, "Failed to enumerate shell:AppsFolder");
        }
        finally
        {
            ReleaseComObject(itemsObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellObject);
        }
    }

    private static string ResolveTarget(string path, string appId)
    {
        bool isUnc = LocalPathPolicy.IsUncPath(path);
        bool exists = !isUnc && !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
                      (File.Exists(path) || Directory.Exists(path));
        _ = vn_app_search_provider_classify_target(
            path.AsSpan(), path.Length,
            appId.AsSpan(), appId.Length,
            isUnc ? (byte)1 : (byte)0,
            exists ? (byte)1 : (byte)0,
            out int mode);
        return mode switch
        {
            1 => path,
            2 => $"shell:AppsFolder\\{appId}",
            3 => $"shell:AppsFolder\\{path}",
            _ => string.Empty
        };
    }

    private static string ReadString(Func<object?> getter)
    {
        try { return Convert.ToString(getter())?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void AddIfMissing(
        IDictionary<string, SpotlightSearchItem> apps,
        SpotlightSearchItem item)
    {
        if (!apps.ContainsKey(item.Title)) apps[item.Title] = item;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch (Exception)
            {
                // Best-effort COM object cleanup, ignore if already released
            }
        }
    }
}
