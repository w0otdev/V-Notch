using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

/// <summary>Owns immutable local copies, retention, deduplication and the Personal vault.
/// Disk work is serialized off the UI thread; full-text search uses a bounded memory cache.</summary>
public sealed partial class ClipboardHistoryStore : IDisposable, IAsyncDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_matches_category_core(
        byte isPersonal,
        byte isArchived,
        byte isPinned,
        int kind,
        byte includeArchived,
        ReadOnlySpan<char> category,
        int categoryLen,
        out int outResult);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_is_screenshot_file_name(
        ReadOnlySpan<char> name,
        int nameLen,
        out byte outIsScreenshot);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_is_screenshot_source_app(
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        out byte outIsScreenshotApp);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_store_is_sharing_violation_hresult(
        int hresult,
        out byte outIsSharingViolation);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnClipboardEntryCategoryMeta
    {
        public byte IsPersonal;
        public byte IsArchived;
        public byte IsPinned;
        public byte Kind;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnClipboardCategoryCountsOut
    {
        public int AllCount;
        public int ArchiveCount;
        public int PinCount;
        public int TextCount;
        public int CodeCount;
        public int LinksCount;
        public int ImagesCount;
        public int ColorsCount;
        public int FilesCount;
    }

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_store_accumulate_category_counts(
        ReadOnlySpan<VnClipboardEntryCategoryMeta> entries,
        int entryCount,
        out VnClipboardCategoryCountsOut outCounts);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnClipboardPruneCandidate
    {
        public int EntryIndex;
        public byte IsPersonal;
        public byte IsPinned;
        public byte IsArchived;
        public long CopiedUtcTicks;
        public long ByteSize;
    }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_equals_icase(
        ReadOnlySpan<char> a,
        int aLen,
        ReadOnlySpan<char> b,
        int bLen,
        out byte outEquals);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_store_plan_prune(
        ReadOnlySpan<VnClipboardPruneCandidate> candidates,
        int candidateCount,
        long cutoffUtcTicks,
        int maxUnpinnedEntries,
        long maxUnpinnedBytes,
        Span<int> outOrderedIndices,
        int maxIndices,
        out int outExpiredCount,
        out int outEvictCandidateCount,
        out long outTotalActiveBytes);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_store_evaluate_sensitive_expiry(
        byte hasEntry,
        byte isPinned,
        byte isArchived,
        byte isPersonal,
        byte hasPersonalDir,
        byte ticksValid,
        long markerTicks,
        long expiryCutoffTicks,
        out int outAction);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_store_is_vault_stream_magic(
        ReadOnlySpan<byte> magic,
        int magicLen,
        out byte outIsMagic);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_format_prefixed_sha256_hex(
        ReadOnlySpan<char> prefix,
        int prefixLen,
        ReadOnlySpan<byte> hashBytes,
        int hashLen,
        Span<char> outBuf,
        int outCap,
        out int outWritten);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_are_signatures_equal(
        ReadOnlySpan<char> sigA,
        int lenA,
        ReadOnlySpan<char> sigB,
        int lenB,
        out byte outEqual);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_store_is_probable_duplicate_image_candidate(
        int kind,
        byte isPersonal,
        long captureTicks,
        long entryTicks,
        long bestTicks,
        byte hasBest,
        out byte outIsBetter);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_is_export_path_protected(
        ReadOnlySpan<char> directory,
        int directoryLen,
        ReadOnlySpan<char> activeLease,
        int activeLeaseLen,
        ushort dirSep,
        out byte outProtected);

    private static readonly Comparison<ClipboardEntry> s_copiedUtcDescComparison =
        static (a, b) => b.CopiedUtc.CompareTo(a.CopiedUtc);

    public const long ApprovalThreshold = 50L * 1024 * 1024;
    public const int DefaultMaxUnpinnedEntries = 500;
    public const long DefaultMaxUnpinnedBytes = 2L * 1024 * 1024 * 1024;
    public int MaxUnpinnedEntries { get; set; } = DefaultMaxUnpinnedEntries;
    public long MaxUnpinnedBytes { get; set; } = DefaultMaxUnpinnedBytes;
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_validate_archive_extract_step(
        int entryIndex,
        int maxEntries,
        long declaredLength,
        long accumulatedBytes,
        long maxTotalBytes,
        ReadOnlySpan<char> rootPrefix,
        int rootPrefixLen,
        ReadOnlySpan<char> resolvedPath,
        int resolvedPathLen,
        out byte outIsValid);

    public const int MaxImportItems = 512;
    public const int MaxImportContents = 100_000;
    public const long MaxArchiveExtractBytes = 512L * 1024 * 1024;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly object _entriesLock = new();
    private readonly NativeClipboardEntryIndex _entries = new();
    private readonly ClipboardVault _vault;
    private readonly object _publicBlobKeyLock = new();
    private byte[]? _publicBlobKey;
    private readonly ClipboardSearchTextCache _searchText = new();
    private int _entriesMutationVersion = 1;
    private int _cachedCategoryVersion = -1;
    private int _cachedCategoryEntriesCount = -1;
    private Dictionary<string, int>? _cachedCategoryCounts;
    private readonly VNotch.ApplicationServices.BoundedSerialQueue _writes = new(64);
    private readonly VNotch.ApplicationServices.LatestQuery<ClipboardSearchPage> _queries = new();
    private readonly object _vaultReadLock = new();
    private int _personalEpoch;
    private readonly SortedSet<ClipboardEntry> _searchOrder = new(Comparer<ClipboardEntry>.Create((a, b) =>
    {
        int time = b.CopiedUtc.CompareTo(a.CopiedUtc);
        return time != 0 ? time : a.Id.CompareTo(b.Id);
    }));
    private ClipboardEntry[]? _searchSnapshot;
    private Task? _disposeTask;
    private volatile bool _personalUnlocked;
    private bool _disposed;
    private bool _initialized;
    private readonly Func<long> _availableSpace;
    private readonly Dictionary<string, ClipboardPersonalExport> _personalExports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDisposable> _exportLeases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _exportEntries = new(StringComparer.OrdinalIgnoreCase);
    public bool AutoDeleteSensitive { get; set; }
    public event Action? Changed;
    public bool IsPersonalUnlocked => _personalUnlocked;
    private void EnsurePersonalUnlocked() { if (!IsPersonalUnlocked) throw new InvalidOperationException("Personal is locked."); }
    public bool HasPersonalPasscode => _vault.IsConfigured;

    public ClipboardHistoryStore(string? root = null)
    {
        _root = root ?? Path.Combine(AppPaths.LocalAppDataRoot, "Clipboard");
        _root = Path.GetFullPath(_root);
        _vault = new ClipboardVault(_root);
        _availableSpace = () => new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
    }

    internal ClipboardHistoryStore(string root, Func<long> availableSpace) : this(root) => _availableSpace = availableSpace;

    internal Task KeepClipboardExportsAsync(IEnumerable<string> paths) => Run(() =>
    {
        string publicRoot = Path.Combine(_root, "exports", "public");
        foreach (string path in paths)
        {
            string? directory;
            try { directory = Path.GetDirectoryName(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (directory != null && string.Equals(Path.GetDirectoryName(directory), publicRoot, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && Directory.Exists(directory) && !_exportLeases.ContainsKey(directory))
                _exportLeases.Add(directory, AppFileCleanupService.Protect(directory));
        }
        return true;
    });

    public IReadOnlyList<ClipboardEntry> Snapshot(bool personal = false)
    {
        if (personal && !IsPersonalUnlocked)
            return Array.Empty<ClipboardEntry>();

        List<ClipboardEntry> list;
        lock (_entriesLock)
        {
            list = new List<ClipboardEntry>(_entries.Count);
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (entry.IsPersonal == personal)
                    list.Add(entry);
            }
        }

        if (list.Count == 0)
            return Array.Empty<ClipboardEntry>();

        list.Sort(s_copiedUtcDescComparison);
        return list;
    }

    public ClipboardEntry? GetEntry(Guid id)
    {
        lock (_entriesLock)
        {
            return _entries.TryGetValue(id, out var entry) &&
                (!entry.IsPersonal || IsPersonalUnlocked) ? entry : null;
        }
    }

    public IReadOnlyDictionary<string, int> GetCategoryCounts()
    {
        lock (_entriesLock)
        {
            int currentVersion = Volatile.Read(ref _entriesMutationVersion);
            int currentCount = _entries.Count;
            var cached = _cachedCategoryCounts;
            if (cached != null && _cachedCategoryVersion == currentVersion && _cachedCategoryEntriesCount == currentCount)
                return cached;

            var counts = new Dictionary<string, int>(16, StringComparer.OrdinalIgnoreCase);
            static void Increment(Dictionary<string, int> dict, string name) =>
                dict[name] = dict.GetValueOrDefault(name) + 1;

            int capacity = Math.Max(1, currentCount);
            VnClipboardEntryCategoryMeta[]? rented = capacity > 512
                ? ArrayPool<VnClipboardEntryCategoryMeta>.Shared.Rent(capacity)
                : null;
            Span<VnClipboardEntryCategoryMeta> metaSpan = rented != null
                ? rented.AsSpan(0, capacity)
                : stackalloc VnClipboardEntryCategoryMeta[capacity];

            try
            {
                int idx = 0;
                foreach (var pair in _entries)
                {
                    var entry = pair.Value;
                    if (entry.IsPersonal) continue;
                    if (idx < metaSpan.Length)
                    {
                        metaSpan[idx++] = new VnClipboardEntryCategoryMeta
                        {
                            IsPersonal = 0,
                            IsArchived = entry.IsArchived ? (byte)1 : (byte)0,
                            IsPinned = entry.IsPinned ? (byte)1 : (byte)0,
                            Kind = (byte)entry.Kind
                        };
                    }
                    else
                    {
                        Increment(counts, entry.IsArchived ? "Archive" : "All");
                        if (entry.IsPinned) Increment(counts, "Pin");
                        if (!entry.IsArchived)
                        {
                            Increment(counts, entry.Kind switch
                            {
                                ClipboardKind.Text => "Text",
                                ClipboardKind.Link => "Links",
                                ClipboardKind.Image => "Images",
                                ClipboardKind.Color => "Colors",
                                ClipboardKind.File => "Files",
                                _ => entry.Kind.ToString()
                            });
                        }
                    }
                    if (!entry.IsArchived)
                    {
                        var groups = entry.Groups;
                        for (int i = 0; i < groups.Length; i++)
                            Increment(counts, groups[i]);
                    }
                }

                if (idx > 0 && vn_clipboard_store_accumulate_category_counts(metaSpan[..idx], idx, out var nativeCounts) == 0)
                {
                    static void AddCount(Dictionary<string, int> dict, string name, int add)
                    {
                        if (add > 0) dict[name] = dict.GetValueOrDefault(name) + add;
                    }
                    AddCount(counts, "All", nativeCounts.AllCount);
                    AddCount(counts, "Archive", nativeCounts.ArchiveCount);
                    AddCount(counts, "Pin", nativeCounts.PinCount);
                    AddCount(counts, "Text", nativeCounts.TextCount);
                    AddCount(counts, "Code", nativeCounts.CodeCount);
                    AddCount(counts, "Links", nativeCounts.LinksCount);
                    AddCount(counts, "Images", nativeCounts.ImagesCount);
                    AddCount(counts, "Colors", nativeCounts.ColorsCount);
                    AddCount(counts, "Files", nativeCounts.FilesCount);
                }
            }
            finally
            {
                if (rented != null)
                    ArrayPool<VnClipboardEntryCategoryMeta>.Shared.Return(rented);
            }

            _cachedCategoryCounts = counts;
            _cachedCategoryEntriesCount = _entries.Count;
            _cachedCategoryVersion = currentVersion;
            return counts;
        }
    }

    public async Task<IReadOnlyList<ClipboardEntry>> SearchAsync(string query, string category, CancellationToken cancellation = default)
        => (await SearchPageAsync(query, category, 0, int.MaxValue, cancellation).ConfigureAwait(false)).Entries;

    public Task<ClipboardSearchPage> SearchPageAsync(string query, string category, int offset = 0, int count = 100, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _queries.Submit(token => SearchCore(query, category, offset, count, token), cancellation);
        }
    }

    public static bool MatchesCategory(ClipboardEntry entry, string category, bool includeArchived = false) =>
        MatchesCategory(entry, (category ?? string.Empty).AsSpan(), includeArchived);

    private static bool MatchesCategory(ClipboardEntry entry, ReadOnlySpan<char> category, bool includeArchived = false)
    {
        _ = vn_clipboard_store_matches_category_core(
            entry.IsPersonal ? (byte)1 : (byte)0,
            entry.IsArchived ? (byte)1 : (byte)0,
            entry.IsPinned ? (byte)1 : (byte)0,
            (int)entry.Kind,
            includeArchived ? (byte)1 : (byte)0,
            category,
            category.Length,
            out int res);
        if (res == 1) return true;
        if (res == 0) return false;

        var groups = entry.Groups;
        for (int i = 0; i < groups.Length; i++)
        {
            ReadOnlySpan<char> grp = groups[i].AsSpan();
            _ = vn_clipboard_store_equals_icase(grp, grp.Length, category, category.Length, out byte eq);
            if (eq != 0)
                return true;
        }
        return false;
    }

    public Task<ClipboardContent> GetContentAsync(Guid id) => Run(() => ReadContent(Find(id)));

    public Task<byte[]?> GetImageAsync(Guid id, CancellationToken cancellation = default) => Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var entry = Find(id);
        string path = Path.Combine(EntryDirectory(entry), "image.png" + (entry.IsPersonal ? ".enc" : ""));
        if (!File.Exists(path)) return null;
        if (entry.IsPersonal) return _vault.ReadImage(path, cancellation);
        return ReadPublicBlobBytes(path, cancellation);
    });

    private static Guid[] DeduplicateIds(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return Array.Empty<Guid>();
        var seen = new HashSet<Guid>(ids.Count);
        var list = new List<Guid>(ids.Count);
        foreach (Guid id in ids)
        {
            if (seen.Add(id))
                list.Add(id);
        }
        return list.ToArray();
    }

    public Task UpdateAsync(Guid id, bool? pinned = null, string[]? groups = null, bool? archived = null) => Run(() =>
    {
        var entry = Find(id);
        string[] updatedGroups = FilterValidGroups(groups, entry.Groups);
        entry = entry with
        {
            IsPinned = pinned ?? entry.IsPinned,
            Groups = updatedGroups,
            IsArchived = archived ?? entry.IsArchived
        };
        WriteEntry(entry);
        PutEntry(entry);
        Changed?.Invoke();
        return true;
    });

    public Task UpdateManyAsync(IReadOnlyCollection<Guid> ids, bool? pinned = null, string[]? groups = null, bool? archived = null, string? addGroup = null)
    {
        if (ids.Count == 0) return Task.CompletedTask;
        var targetIds = DeduplicateIds(ids);
        return Run(() =>
        {
            string[]? prefilteredGroups = groups != null ? FilterValidGroups(groups, Array.Empty<string>()) : null;
            bool canAddGroup = prefilteredGroups == null && !string.IsNullOrEmpty(addGroup) && IsLabel(addGroup);
            bool changed = false;
            for (int i = 0; i < targetIds.Length; i++)
            {
                ClipboardEntry? entry;
                lock (_entriesLock)
                {
                    _entries.TryGetValue(targetIds[i], out entry);
                }
                if (entry == null || (entry.IsPersonal && !IsPersonalUnlocked))
                    continue;

                string[] updatedGroups = entry.Groups;
                if (prefilteredGroups != null)
                {
                    updatedGroups = prefilteredGroups;
                }
                else if (canAddGroup && !ContainsGroupOrdinalIgnoreCase(entry.Groups, addGroup!))
                {
                    updatedGroups = AppendValidGroup(entry.Groups, addGroup!);
                }

                bool nextPinned = pinned ?? entry.IsPinned;
                bool nextArchived = archived ?? entry.IsArchived;
                if (nextPinned == entry.IsPinned && nextArchived == entry.IsArchived && ReferenceEquals(updatedGroups, entry.Groups))
                    continue;

                var updated = entry with
                {
                    IsPinned = nextPinned,
                    Groups = updatedGroups,
                    IsArchived = nextArchived
                };
                WriteEntry(updated);
                PutEntry(updated);
                changed = true;
            }
            if (changed) Changed?.Invoke();
            return true;
        });
    }

    private static bool ContainsGroupOrdinalIgnoreCase(string[] groups, string target)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            if (string.Equals(groups[i], target, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string[] AppendValidGroup(string[] existingGroups, string groupToAdd)
    {
        var filtered = new List<string>(existingGroups.Length + 1);
        var seen = new HashSet<string>(existingGroups.Length + 1, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < existingGroups.Length; i++)
        {
            string g = existingGroups[i];
            if (IsLabel(g) && seen.Add(g))
                filtered.Add(g);
        }
        if (seen.Add(groupToAdd))
            filtered.Add(groupToAdd);
        return filtered.Count == 0 ? Array.Empty<string>() : filtered.ToArray();
    }

    private string[] FilterValidGroups(string[]? candidateGroups, string[] fallbackGroups)
    {
        if (candidateGroups == null) return fallbackGroups;
        var filtered = new List<string>(candidateGroups.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < candidateGroups.Length; i++)
        {
            string g = candidateGroups[i];
            if (IsLabel(g) && seen.Add(g))
                filtered.Add(g);
        }
        return filtered.Count == 0 ? Array.Empty<string>() : filtered.ToArray();
    }

    private ClipboardEntry RestoreFileMetadata(ClipboardEntry entry)
    {
        var original = entry;
        ClipboardContent? content = null;
        if (!entry.IsSensitive && entry.SensitivityVersion < ClipboardSensitiveData.DetectorVersion)
        {
            content = ReadContent(entry);
            entry = entry with
            {
                IsSensitive = HasSensitiveFiles(content.Files)
                || ClipboardSensitiveData.IsSensitive(content.Text, entry.SourceApp, entry.SourceExecutable)
                || ClipboardSensitiveData.IsSensitive(content.Html, entry.SourceApp, entry.SourceExecutable)
                || ClipboardSensitiveData.IsSensitive(content.Rtf, entry.SourceApp, entry.SourceExecutable)
            };
        }
        if (entry.IsSensitive)
        {
            entry = entry with { Title = ClipboardSensitiveData.Mask, Preview = ClipboardSensitiveData.Mask, MaskedPreview = ClipboardSensitiveData.Mask };
        }
        entry = entry with { SensitivityVersion = ClipboardSensitiveData.DetectorVersion };
        if (entry != original) WriteEntry(entry);
        if (entry.FileCount != 0 || entry.Kind != ClipboardKind.File) return entry;
        try
        {
            var files = ReadContent(entry).Files;
            entry = entry with
            {
                FileCount = files.Length,
                PrimaryExtension = files.Length == 1 && !files[0].IsDirectory
                    ? Path.GetExtension(files[0].Name).ToLowerInvariant() : ""
            };
            WriteEntry(entry);
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            RuntimeLog.Warn("CLIPBOARD", "Could not restore file badge metadata.");
            return entry;
        }
    }

    private void DeleteSensitiveExpiryMarker(Guid id)
    {
        string markerPath = Path.Combine(_root, "sensitive-expiry", id.ToString("N"));
        if (File.Exists(markerPath))
        {
            try { SafeFileDeletion.DeleteFile(markerPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void PutEntry(ClipboardEntry entry, bool persistExpiryMarker = true)
    {
        if (entry.IsPersonal || entry.IsSensitive) RemoveImagePreview(entry.Id);
        lock (_entriesLock)
        {
            if (_entries.TryGetValue(entry.Id, out var old)) _searchOrder.Remove(old);
            _entries[entry.Id] = entry;
            _searchOrder.Add(entry);
            _searchSnapshot = null;
            Interlocked.Increment(ref _entriesMutationVersion);
            _cachedCategoryCounts = null;
        }
        if (entry.IsSensitive)
        {
            _searchText.Remove(entry.Id);
        }
        if (entry.IsSensitive && !entry.IsPersonal && !entry.IsPinned && !entry.IsArchived)
        {
            string expiryRoot = Path.Combine(_root, "sensitive-expiry");
            string markerPath = Path.Combine(expiryRoot, entry.Id.ToString("N"));
            if (persistExpiryMarker || !File.Exists(markerPath))
            {
                Directory.CreateDirectory(expiryRoot);
                ClipboardVault.AtomicWrite(markerPath,
                    Encoding.UTF8.GetBytes(entry.CopiedUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        else
        {
            DeleteSensitiveExpiryMarker(entry.Id);
        }
    }

    private ClipboardEntry? FindDuplicate(string fingerprint, bool personal)
    {
        lock (_entriesLock) return _entries.FindDuplicate(fingerprint, personal);
    }

    private void RemoveEntry(Guid id)
    {
        RemoveImagePreview(id);
        ReleaseEntryExports(id);
        DeleteSensitiveExpiryMarker(id);
        lock (_entriesLock)
        {
            if (_entries.TryRemove(id, out var entry))
            {
                _searchOrder.Remove(entry);
                _searchSnapshot = null;
                Interlocked.Increment(ref _entriesMutationVersion);
                _cachedCategoryCounts = null;
            }
            // Search admits cached text under the same lock; deletion must win that race.
            _searchText.Remove(id);
        }
    }

    private ClipboardEntry Find(Guid id)
    {
        lock (_entriesLock)
        {
            return _entries.TryGetValue(id, out var entry) && (!entry.IsPersonal || IsPersonalUnlocked)
                ? entry : throw new KeyNotFoundException("Clipboard item is unavailable.");
        }
    }
    private static string ImagePixelFingerprint(byte[] image)
    {
        Span<byte> digest = stackalloc byte[32];
        return NativeClipboardHash.TryHashImage(image, null, digest)
            ? FormatPrefixedHashBytes("image-v2:", digest) : HashRawImageBytes(image);
    }

    private static string HashRawImageBytes(byte[] image)
    {
        Span<byte> digest = stackalloc byte[32];
        NativeClipboardHash.HashData(image, digest);
        return FormatPrefixedHashBytes("image-raw:", digest);
    }

    internal static string FingerprintPixels(System.Windows.Media.Imaging.BitmapSource source)
    {
        var converted = source.Format == System.Windows.Media.PixelFormats.Pbgra32 ? source :
            new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        var pixels = new System.Windows.Media.Imaging.WriteableBitmap(converted);
        Span<byte> digest = stackalloc byte[32];
        pixels.Lock();
        try { NativeClipboardHash.HashPixels(pixels.BackBuffer, pixels.PixelWidth, pixels.PixelHeight, pixels.BackBufferStride, digest); }
        finally { pixels.Unlock(); }
        return FormatPrefixedHashBytes("image-v2:", digest);
    }

    private static void AppendCaptureFingerprint(NativeClipboardHash hash, ClipboardCapture capture)
    {
        AppendUtf8String(hash, capture.Text, includeLengthPrefix: true);
        AppendUtf8String(hash, capture.Html, includeLengthPrefix: true);
        AppendUtf8String(hash, capture.Rtf, includeLengthPrefix: true);
        if (capture.ImagePng != null) hash.AppendData(capture.ImagePng);
    }

    private static void AppendUtf8String(NativeClipboardHash hash, string? value, bool includeLengthPrefix)
        => hash.AppendUtf16(value, includeLengthPrefix);

    private void EnsureDiskSpace(long bytes)
    {
        if (_availableSpace() < checked(bytes + 1024 * 1024))
            throw new IOException("Not enough free space to save this copy.");
    }
    private Task<T> Run<T>(Func<T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _writes.Enqueue(action);
        }
    }
    private static Stream OpenReadFileWithRetry(string path)
    {
        int retries = 3;
        while (true)
        {
            try
            {
                return ClipboardImportPaths.OpenValidatedRead(path);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && --retries >= 0)
            {
                Thread.Sleep(50 * (3 - retries));
            }
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        return vn_clipboard_store_is_sharing_violation_hresult(ex.HResult, out byte isSv) == 0 && isSv != 0;
    }

    // UI owners use DisposeAsync to drain queued work. Synchronous disposal waits a
    // bounded time for the same cleanup so queued writes are not lost on exit. Queued
    // work runs on TaskScheduler.Default and never needs the UI dispatcher.
    public void Dispose()
    {
        try
        {
            if (!DisposeAsync().AsTask().Wait(DisposeDrainTimeout))
                RuntimeLog.Log("CLIPBOARD-STORE-DISPOSE", $"Pending work did not finish within {DisposeDrainTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("CLIPBOARD-STORE-DISPOSE", ex, "Cleanup failed while draining");
        }
    }

    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(3);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            _personalUnlocked = false;
            var pending = Task.WhenAll(_writes.CompleteAsync(), _queries.CompleteAsync());
            _disposeTask = DisposeCoreAsync(pending);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); } catch { /* Failed operations still release their resources. */ }
        LockPersonalCore();
        _searchText.Clear();
        _imagePreviews.Clear();
        _imagePreviewBytes = 0;
        _vault.Dispose();
        lock (_publicBlobKeyLock)
        {
            if (_publicBlobKey != null)
            {
                CryptographicOperations.ZeroMemory(_publicBlobKey);
                _publicBlobKey = null;
            }
        }
        _publicRecords?.Dispose();
        lock (_entriesLock) { _entries.Dispose(); _searchOrder.Clear(); _searchSnapshot = null; }
        foreach (var lease in _exportLeases.Values) lease.Dispose();
        _exportLeases.Clear();
        _exportEntries.Clear();
    }
}
