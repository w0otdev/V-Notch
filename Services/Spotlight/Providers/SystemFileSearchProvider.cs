using System.IO;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

/// <summary>
/// Indexes launchable files that Windows Search does not expose when its query
/// root is the user's profile, such as ncpa.cpl and services.msc.
/// </summary>
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
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return Array.Empty<SpotlightSearchItem>();

        IReadOnlyList<SpotlightSearchItem> files =
            await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return files
            .Select(file => file with { Score = SpotlightRanker.Score(file, query) })
            .Where(file => file.Score > 0)
            .OrderByDescending(file => file.Score)
            .ThenBy(file => file.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    internal Task WarmupAsync() => _index.Value;

    private IReadOnlyList<SpotlightSearchItem> BuildIndex()
    {
        // Prefer the native System32 copy when the same command also exists in
        // SysWOW64. A file name maps to one result so searches never show twins.
        var files = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawRoot in _roots)
        {
            string root;
            try
            {
                root = Path.GetFullPath(rawRoot);
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(root)) continue;

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
                    if (!LaunchableExtensions.Contains(extension)) continue;

                    string title = Path.GetFileName(path);
                    if (title.Length == 0 || files.ContainsKey(title)) continue;

                    files[title] = new SpotlightSearchItem(
                        $"system:{path}",
                        SpotlightResultKind.Application,
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

        return files.Values.ToArray();
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
