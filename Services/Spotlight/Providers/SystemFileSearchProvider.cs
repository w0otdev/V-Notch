using System.IO;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed class SystemFileSearchProvider : ISpotlightProvider
{
    private static readonly HashSet<string> LaunchableExtensions = new(
        [".cpl", ".msc", ".exe", ".com", ".cmd", ".bat"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<string> _roots;
    private readonly Lazy<Task<IReadOnlyList<SpotlightSearchItem>>> _index;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    public SystemFileSearchProvider()
        : this(GetDefaultRoots())
    {
    }

    internal SystemFileSearchProvider(IEnumerable<string> roots)
    {
        _roots = roots.Where(root => !string.IsNullOrWhiteSpace(root)).ToArray();
        _index = new(() => Task.Run(BuildIndex));
    }

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2 || limit <= 0)
            return Array.Empty<SpotlightSearchItem>();

        IReadOnlyList<SpotlightSearchItem> files =
            await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return files
            .Select(file => file with { Score = ScoreSystemFile(file, query) })
            .Where(file => file.Score > 0)
            .OrderByDescending(file => file.Score)
            .ThenBy(file => file.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    internal Task WarmupAsync() => _index.Value;

    private static double ScoreSystemFile(SpotlightSearchItem file, string query)
    {
        string name = Path.GetFileName(file.Target);
        string stem = Path.GetFileNameWithoutExtension(name);
        string requested = query.Trim().Trim('"');
        // Exact command names remain discoverable without Everything or WSearch.
        // Generic partial matches stay below ordinary apps and documents.
        if (name.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || (file.Kind == SpotlightResultKind.Application && stem.Equals(requested, StringComparison.OrdinalIgnoreCase))
            || file.Target.Equals(requested, StringComparison.OrdinalIgnoreCase)) return 1200;
        if (file.Kind != SpotlightResultKind.Application) return 0;
        if (stem.StartsWith(requested, StringComparison.OrdinalIgnoreCase)) return 140;
        return requested.Length >= 3 && stem.Contains(requested, StringComparison.OrdinalIgnoreCase) ? 80 : 0;
    }

    private IReadOnlyList<SpotlightSearchItem> BuildIndex()
    {
        // Prefer the native System32 copy when the same command also exists in
        // SysWOW64. A file name maps to one result so searches never show twins.
        var files = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawRoot in _roots)
        {
            IndexDirectory(rawRoot, files);
        }

        return files.Values.ToArray();
    }

    private static void IndexDirectory(string rawRoot, IDictionary<string, SpotlightSearchItem> files)
    {
        string root;
        try
        {
            root = Path.GetFullPath(rawRoot);
        }
        catch
        {
            return;
        }

        if (!Directory.Exists(root)) return;

        try
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            }))
            {
                string extension = Path.GetExtension(path);
                bool launchable = LaunchableExtensions.Contains(extension);

                string title = Path.GetFileName(path);
                if (title.Length == 0 || files.ContainsKey(title)) continue;

                files[title] = new SpotlightSearchItem(
                    $"system:{path}",
                    launchable ? SpotlightResultKind.Application : SpotlightResultKind.File,
                    title,
                    root,
                    path,
                    path);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-SYSTEM-INDEX", ex, $"Failed to read system directory: {root}");
        }
    }

    private static IEnumerable<string> GetDefaultRoots()
    {
        // Ordering matters for duplicate names: native tools beat their x86
        // counterpart, followed by executables stored directly under Windows.
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }
}
