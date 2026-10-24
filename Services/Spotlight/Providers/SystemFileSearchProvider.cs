using System.IO;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed partial class SystemFileSearchProvider : ISpotlightProvider
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_system_file_search_is_launchable_ext(
        ReadOnlySpan<char> extPtr,
        int extLen,
        out byte outLaunchable);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_system_file_search_score(
        int kind,
        ReadOnlySpan<char> targetPtr,
        int targetLen,
        ReadOnlySpan<char> reqPtr,
        int reqLen,
        out double outScore);

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
        query = query.Trim().Trim('"').Trim();
        if (query.Length < 2 || limit <= 0)
            return Array.Empty<SpotlightSearchItem>();

        IReadOnlyList<SpotlightSearchItem> files =
            await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return FilterAndRank(files, query, limit);
    }

    private static IReadOnlyList<SpotlightSearchItem> FilterAndRank(
        IReadOnlyList<SpotlightSearchItem> files,
        string query,
        int limit)
    {
        ReadOnlySpan<char> requested = query.AsSpan();
        if (requested.IsEmpty) return Array.Empty<SpotlightSearchItem>();

        var matches = new List<SpotlightSearchItem>();
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            double score = ScoreSystemFile(file, requested);
            if (score > 0)
            {
                matches.Add(file with { Score = score });
            }
        }
        if (matches.Count == 0) return Array.Empty<SpotlightSearchItem>();

        return matches
            .OrderByDescending(file => file.Score)
            .ThenBy(file => file.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    internal Task WarmupAsync() => _index.Value;

    private static double ScoreSystemFile(SpotlightSearchItem file, ReadOnlySpan<char> requested)
    {
        if (file.Kind != SpotlightResultKind.Application) return 0;
        ReadOnlySpan<char> target = file.Target.AsSpan();
        _ = vn_system_file_search_score(0, target, target.Length, requested, requested.Length, out double score);
        return score;
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
                ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
                if (vn_system_file_search_is_launchable_ext(extension, extension.Length, out byte launchable) != 0 || launchable == 0) continue;

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

    private static IEnumerable<string> GetDefaultRoots()
    {
        // Ordering matters for duplicate names: native tools beat their x86
        // counterpart, followed by executables stored directly under Windows.
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }
}
