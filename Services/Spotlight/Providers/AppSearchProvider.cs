using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed class AppSearchProvider : ISpotlightProvider
{
    private readonly Lazy<Task<IReadOnlyList<SpotlightSearchItem>>> _index;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    public AppSearchProvider()
    {
        _index = new(() => Task.Run(BuildIndex));
    }

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var apps = await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return apps
            .Select(app => app with
            {
                Subtitle = Loc.Get("spotlight.kind.application"),
                Score = SpotlightRanker.Score(app, query)
            })
            .Where(app => app.Score > 0)
            .OrderByDescending(app => app.Score)
            .ThenBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    internal Task WarmupAsync() => _index.Value;

    private static IReadOnlyList<SpotlightSearchItem> BuildIndex()
    {
        var apps = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);
        AddStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        AddStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        AddAppsFolderItems(apps);
        return apps.Values.Select(LoadIcon).ToArray();
    }

    private static SpotlightSearchItem LoadIcon(SpotlightSearchItem item) =>
        string.IsNullOrEmpty(item.IconPath)
            ? item
            : item with { Icon = FileIconProvider.GetFileIcon(item.IconPath) };

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
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return path;
        if (File.Exists(path) || Directory.Exists(path)) return path;
        string identity = appId.Length > 0 ? appId : path;
        return identity.Length > 0 ? $"shell:AppsFolder\\{identity}" : string.Empty;
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
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }
}
