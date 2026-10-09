using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using VNotch.Services.Translation;

namespace VNotch.Services;

/// <summary>Reclaims only disposable artifacts owned by this app, off the UI thread.</summary>
public sealed class AppFileCleanupService : IDisposable, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, int> ActivePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _worker;
    private bool _disposed;
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            // Let startup and initial disk reads finish first.
            await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var result = Clean(CreatePolicies(), DateTime.UtcNow, _stop.Token);
                    if (result.FilesDeleted > 0)
                        RuntimeLog.Info("FILE-CLEANUP", $"Removed {result.FilesDeleted} temporary files ({result.BytesDeleted} bytes).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { RuntimeLog.Warn("FILE-CLEANUP", $"Cleanup deferred: {ex.GetType().Name}"); }
                await Task.Delay(Interval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    // Acquire before creating the path; retain until its producer and consumers finish.
    internal static IDisposable Protect(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        ActivePaths.AddOrUpdate(path, 1, (_, count) => count + 1);
        return new PathLease(path);
    }

    private sealed class PathLease(string path) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            // Keep zero-valued slots out of the registry, without losing a concurrent lease.
            while (ActivePaths.TryGetValue(path, out int count))
            {
                if (count == 1)
                {
                    if (((ICollection<KeyValuePair<string, int>>)ActivePaths).Remove(new(path, count))) return;
                }
                else if (ActivePaths.TryUpdate(path, count - 1, count)) return;
            }
        }
    }

    internal sealed record Policy(string Root, bool Directories, Func<string, bool> Matches,
        TimeSpan Retention, long MaxBytes = long.MaxValue, TimeSpan? MinimumAge = null);
    internal readonly record struct Result(int FilesDeleted, long BytesDeleted);
    private sealed record Snapshot(string Path, List<FileInfo> Files, List<string> Directories,
        DateTime LastActivity, long Bytes);

    internal static Policy[] CreatePolicies(string? roaming = null, string? local = null, string? temp = null)
    {
        roaming ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        local ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        temp ??= Path.GetTempPath();
        var day = TimeSpan.FromDays(1);
        var policies = new List<Policy>
        {
            new(Path.Combine(temp, "V-Notch"), true, IsGuidName, TimeSpan.FromDays(7), 256L * 1024 * 1024, day),
            new(Path.Combine(local, "V-Notch", "SpotifyWebView2"), true, IsGuidName, TimeSpan.FromDays(3), 256L * 1024 * 1024, day),
            new(Path.Combine(local, "V-Notch", "Clipboard", "staging"), true, IsGuidName, TimeSpan.FromDays(3), 256L * 1024 * 1024, day),
            new(Path.Combine(local, "V-Notch", "Clipboard", "exports", "public"), true, IsGuidName, TimeSpan.FromDays(3), 256L * 1024 * 1024, day),
            new(Path.Combine(roaming, "V-Notch"), false,
                name => IsAtomicTemporary(name, "settings.json") || IsAtomicTemporary(name, "spotlight-usage.json") ||
                        name == "spotlight-usage.preferences.json.tmp", day),
            new(Path.Combine(local, "VNotch"), false, name => name == "spotlight-chats.enc.tmp", day),
        };
        foreach (var profile in TranslationModelCatalog.All)
        {
            var names = profile.Assets.Select(asset => asset.Name + ".install.part").ToHashSet(StringComparer.OrdinalIgnoreCase);
            policies.Add(new(Path.Combine(local, "VNotch", "translation-models", profile.Id), false, names.Contains, day));
        }
        // Resumable .part downloads, installed models, saved chats, clipboard history and
        // settings/backups are user data. They are deliberately absent from these policies.
        return policies.ToArray();
    }

    private static bool IsGuidName(string name) => Guid.TryParseExact(name, "N", out _);
    private static bool IsAtomicTemporary(string name, string destination) =>
        name.Length == destination.Length + 1 + 32 + 4 &&
        name.StartsWith(destination + ".", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
        IsGuidName(name[(destination.Length + 1)..^4]);

    internal static Result Clean(IEnumerable<Policy> policies, DateTime nowUtc, CancellationToken ct = default,
        int entryLimit = 10_000, TimeSpan? timeLimit = null)
    {
        var budget = new ScanBudget(entryLimit, timeLimit ?? TimeSpan.FromSeconds(5), ct);
        int deleted = 0;
        long reclaimed = 0;
        foreach (var policy in policies)
        {
            if (!budget.Available) break;
            string root = Path.GetFullPath(policy.Root);
            if (!IsSafePath(root, root) || !Directory.Exists(root)) continue;
            var candidates = new List<Snapshot>();
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(root))
                {
                    if (!budget.Visit()) break;
                    if (!policy.Matches(Path.GetFileName(path)) || IsProtected(path) || !IsSafePath(root, path)) continue;
                    bool directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                    if (directory != policy.Directories) continue;
                    var snapshot = ReadSnapshot(root, path, budget);
                    if (snapshot != null) candidates.Add(snapshot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            long total = candidates.Sum(candidate => candidate.Bytes);
            foreach (var candidate in candidates.OrderBy(candidate => candidate.LastActivity))
            {
                if (!budget.Available) break;
                var age = nowUtc - candidate.LastActivity;
                if (age < (policy.MinimumAge ?? policy.Retention) ||
                    (age < policy.Retention && total <= policy.MaxBytes) || IsProtected(candidate.Path)) continue;
                // Recheck before deleting: a producer may have touched an artifact since enumeration.
                if (candidate.Files.Any(file => !Unchanged(root, file)) ||
                    candidate.Directories.Any(dir => !IsSafePath(root, dir))) continue;
                foreach (var file in candidate.Files)
                {
                    if (!budget.Available || IsProtected(candidate.Path) || !Unchanged(root, file)) break;
                    try
                    {
                        // An exclusive open detects files still held by external consumers.
                        using (new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        File.Delete(file.FullName);
                        deleted++;
                        reclaimed += file.Length;
                        total -= file.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                foreach (string dir in candidate.Directories.AsEnumerable().Reverse())
                {
                    if (!budget.Available || IsProtected(candidate.Path)) break;
                    try
                    {
                        // Non-recursive deletion cannot follow links or remove new/locked contents.
                        if (IsSafePath(root, dir)) Directory.Delete(dir, recursive: false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return new(deleted, reclaimed);
    }

    private static Snapshot? ReadSnapshot(string root, string path, ScanBudget budget)
    {
        var files = new List<FileInfo>();
        var dirs = new List<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        DateTime activity = DateTime.MinValue;
        long bytes = 0;
        try
        {
            while (pending.TryPop(out string? current))
            {
                if (!budget.Visit() || !IsSafePath(root, current)) return null;
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var info = new DirectoryInfo(current);
                    activity = Max(activity, Max(info.CreationTimeUtc, info.LastWriteTimeUtc));
                    dirs.Add(current);
                    foreach (string child in Directory.EnumerateFileSystemEntries(current))
                    {
                        if (!budget.Visit()) return null;
                        pending.Push(child);
                    }
                }
                else
                {
                    var info = new FileInfo(current);
                    activity = Max(activity, Max(info.CreationTimeUtc, info.LastWriteTimeUtc));
                    bytes = checked(bytes + info.Length);
                    files.Add(info);
                }
            }
            return new(path, files, dirs, activity, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { return null; }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static bool Unchanged(string root, FileInfo previous)
    {
        try
        {
            if (!IsSafePath(root, previous.FullName)) return false;
            var current = new FileInfo(previous.FullName);
            return current.Exists && current.Length == previous.Length && current.LastWriteTimeUtc == previous.LastWriteTimeUtc &&
                   current.CreationTimeUtc == previous.CreationTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsProtected(string path) => ActivePaths.Keys.Any(active =>
        path.Equals(active, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(active + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        active.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSafePath(string root, string path)
    {
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(@"\\", StringComparison.Ordinal) ||
                (!path.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                 !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return false;
            // Check ancestors too: a junction at the app root must never redirect cleanup.
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private sealed class ScanBudget(int limit, TimeSpan duration, CancellationToken ct)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _visited;
        internal bool Available => !ct.IsCancellationRequested && _visited < limit && _clock.Elapsed < duration;
        internal bool Visit() => Available && ++_visited <= limit;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_worker != null) await _worker.ConfigureAwait(false);
        // Synchronous WPF shutdown only signals cancellation; it never blocks the UI on disk I/O.
        GC.SuppressFinalize(this);
    }
}
