using System.IO;
using System.Buffers;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

// Cancellable native filesystem fallback for files outside Windows/Everything indexes.
internal sealed partial class LocalFileSearchProvider : ISpotlightProvider
{
    private const string NativeDll = "VNotch.Native.dll";
    private readonly string _roots;
    private readonly string _hiddenRoots = string.Join('\0', SpotlightFileVisibility.HiddenRoots);
    private readonly int _budgetMilliseconds;
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    internal Func<string, bool>? IsExcluded { get; set; }
    internal Func<IReadOnlyList<string>>? ExcludedFolders { get; set; }
    public bool IsAvailable => true;
    public bool IsInstant => false;

    public LocalFileSearchProvider() : this(null, 1500) { }

    internal LocalFileSearchProvider(IEnumerable<string>? roots, int budgetMilliseconds = 1500)
    {
        _roots = roots == null ? string.Empty : string.Join('\0', roots) + '\0';
        _budgetMilliseconds = budgetMilliseconds;
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_local_file_search_create")]
    private static partial int Create(out long handle);
    [LibraryImport(NativeDll, EntryPoint = "vn_local_file_search_cancel")]
    private static partial int Cancel(long handle);
    [LibraryImport(NativeDll, EntryPoint = "vn_local_file_search_close")]
    private static partial int Close(long handle);
    [LibraryImport(NativeDll, EntryPoint = "vn_local_file_search_run", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Run(long handle, string query, int queryLength, string roots, int rootsLength,
        string hiddenRoots, int hiddenLength, int limit, int budgetMilliseconds, out int count);
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeResult
    {
        public int Offset, Length, Kind;
        public double Score;
    }
    [LibraryImport(NativeDll, EntryPoint = "vn_local_file_search_copy_results", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CopyResults(long handle, Span<NativeResult> results, int capacity,
        Span<char> text, int textCapacity, out int count, out int textLength);

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || !SpotlightFileVisibility.ShouldSearch(query)) return Array.Empty<SpotlightSearchItem>();
        await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Create(out long handle) != 0) return Array.Empty<SpotlightSearchItem>();
            try
            {
                using var registration = cancellationToken.Register(() => Cancel(handle));
                // Always await the worker, including on cancellation, before releasing its native handle.
                return await Task.Run(() => SearchNative(handle, query, limit, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            }
            finally { Close(handle); }
        }
        finally { _searchGate.Release(); }
    }

    private IReadOnlyList<SpotlightSearchItem> SearchNative(long handle, string query, int limit, CancellationToken token)
    {
        string hiddenRoots = _hiddenRoots + '\0' + string.Join('\0', ExcludedFolders?.Invoke() ?? Array.Empty<string>());
        int status = Run(handle, query, query.Length, _roots, _roots.Length, hiddenRoots, hiddenRoots.Length,
            limit, _budgetMilliseconds, out int count);
        token.ThrowIfCancellationRequested();
        if (status != 0) return Array.Empty<SpotlightSearchItem>();
        status = CopyResults(handle, [], 0, [], 0, out count, out int textLength);
        if (count == 0 || count > 200 || textLength < 0 || textLength > 200 * 32768 || status != -2)
            return Array.Empty<SpotlightSearchItem>();
        Span<NativeResult> records = stackalloc NativeResult[count];
        char[] arena = ArrayPool<char>.Shared.Rent(textLength);
        try
        {
            if (CopyResults(handle, records, records.Length, arena, arena.Length, out count, out _) != 0)
                return Array.Empty<SpotlightSearchItem>();
            var results = new List<SpotlightSearchItem>(count);
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var record = records[index];
                string path = new(arena, record.Offset, record.Length);
                int kind = record.Kind;
                if (IsExcluded?.Invoke(path) == true) continue;
                var resultKind = kind switch { 0 => SpotlightResultKind.Application, 2 => SpotlightResultKind.Folder, _ => SpotlightResultKind.File };
                string prefix = kind switch { 0 => "app", 2 => "folder", _ => "file" };
                results.Add(new SpotlightSearchItem($"{prefix}:{path}", resultKind, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, path, path) { Score = record.Score });
            }
            return results;
        }
        finally { ArrayPool<char>.Shared.Return(arena, clearArray: true); }
    }
}
