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

    private static readonly Comparison<ClipboardEntry> s_copiedUtcDescComparison =
        static (a, b) => b.CopiedUtc.CompareTo(a.CopiedUtc);
    private static readonly Comparison<ClipboardEntry> s_copiedUtcAscComparison =
        static (a, b) => a.CopiedUtc.CompareTo(b.CopiedUtc);

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
    private readonly ConcurrentDictionary<Guid, ClipboardEntry> _entries = new();
    private readonly ClipboardVault _vault;
    private readonly Dictionary<(string Fingerprint, bool Personal), HashSet<Guid>> _fingerprints = new();
    private readonly ClipboardSearchTextCache _searchText = new();
    private int _entriesMutationVersion = 1;
    private int _cachedCategoryVersion = -1;
    private int _cachedCategoryEntriesCount = -1;
    private Dictionary<string, int>? _cachedCategoryCounts;
    private Task _pending = Task.CompletedTask;
    private Task? _disposeTask;
    private volatile bool _personalUnlocked;
    private bool _disposed;
    private bool _initialized;
    private readonly Func<long> _availableSpace;
    private readonly Dictionary<string, ClipboardPersonalExport> _personalExports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDisposable> _exportLeases = new(StringComparer.OrdinalIgnoreCase);
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

    public Task InitializeAsync() => Run(() =>
    {
        if (_initialized) return true;
        LoadLabels();
        LoadCategoryOrder();
        Directory.CreateDirectory(Path.Combine(_root, "items"));
        Directory.CreateDirectory(Path.Combine(_root, "personal"));
        LockPersonalCore();
        string staging = Path.Combine(_root, "staging");
        TryDeleteDirectory(staging);
        ReconcileInterruptedMoves();
        foreach (string directory in Directory.EnumerateDirectories(Path.Combine(_root, "items")))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ClipboardEntry>(File.ReadAllBytes(Path.Combine(directory, "entry.json")));
                if (entry != null && entry.Id == id && !entry.IsPersonal) PutEntry(RestoreFileMetadata(entry), persistExpiryMarker: false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { RuntimeLog.Warn("CLIPBOARD", "An unreadable clipboard item was preserved."); }
        }
        PruneCore(DateTime.UtcNow);
        DeleteExportsCore();
        _initialized = true;
        Changed?.Invoke();
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

    public Task<ClipboardImportResult> ImportAsync(ClipboardCapture capture, bool approved = false, IProgress<double>? progress = null)
    {
        // Reject oversized drops before they enter the serialized disk-work queue.
        if (capture.FilePaths.Length > MaxImportItems)
            return Task.FromException<ClipboardImportResult>(new ClipboardImportLimitException());
        for (int i = 0; i < capture.FilePaths.Length; i++)
        {
            if (!ClipboardImportPaths.IsLocal(capture.FilePaths[i]))
                return Task.FromException<ClipboardImportResult>(new IOException("Only local paths can be saved in history."));
        }
        return Run(() => ImportCore(capture, approved, progress));
    }

    private ClipboardImportResult ImportCore(ClipboardCapture capture, bool approved, IProgress<double>? progress = null, bool batchChild = false)
    {
        bool personal = capture.TargetCategory == "Personal";
        if (personal && !IsPersonalUnlocked) throw new InvalidOperationException("Unlock Personal before importing.");
        long size = capture.ImagePng?.LongLength
            ?? ((long)Encoding.UTF8.GetByteCount(capture.Text)
                + Encoding.UTF8.GetByteCount(capture.Html)
                + Encoding.UTF8.GetByteCount(capture.Rtf));
        int measuredItems = 0;
        if (capture.FilePaths.Length > 0)
        {
            size = 0;
            var normalizedPaths = new string[capture.FilePaths.Length];
            for (int i = 0; i < capture.FilePaths.Length; i++)
            {
                string norm = ClipboardImportPaths.Normalize(capture.FilePaths[i]);
                normalizedPaths[i] = norm;
                size = checked(size + MeasurePath(norm, ref measuredItems));
            }
            capture = capture with { FilePaths = normalizedPaths };
        }
        if (!approved && (capture.FilePaths.Length > 0 || size > ApprovalThreshold) && size > ApprovalThreshold)
            return new ClipboardImportResult(null, true, size);
        if (size == 0 && capture.FilePaths.Length == 0) return new ClipboardImportResult(null);
        EnsureDiskSpace(checked(size * (personal ? 2 : 1) + (capture.ImagePng?.LongLength ?? 0) + measuredItems * 1024L));

        if (capture.SeparateFiles && capture.FilePaths.Length > 1)
        {
            // Preflight the whole drop above before splitting it, preserving the
            // aggregate approval, disk-space and directory-content limits.
            var entries = new List<ClipboardEntry>(capture.FilePaths.Length);
            int imported = 0, failed = 0;
            bool needsApproval = false;
            long approvalSize = 0;
            for (int i = 0; i < capture.FilePaths.Length; i++)
            {
                var result = ImportCore(capture with { FilePaths = [capture.FilePaths[i]], SeparateFiles = false }, approved, batchChild: true);
                if (result.Entry != null) entries.Add(result.Entry);
                imported += result.ImportedCount;
                failed += result.FailedCount;
                needsApproval |= result.NeedsApproval;
                approvalSize = checked(approvalSize + (result.NeedsApproval ? result.ByteSize : result.Entry?.ByteSize ?? 0));
                progress?.Report((double)(i + 1) / capture.FilePaths.Length);
            }
            if (entries.Count > 0)
            {
                PruneCore(DateTime.UtcNow);
                Changed?.Invoke();
            }
            return new ClipboardImportResult(entries.Count > 0 ? entries[0] : null, needsApproval, approvalSize, imported, failed)
            { Entries = entries };
        }

        Guid id = Guid.NewGuid();
        string staging = Path.Combine(_root, "staging", id.ToString("N"));
        using var cleanupLease = AppFileCleanupService.Protect(staging);
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<ClipboardFile>(capture.FilePaths.Length);
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendCaptureFingerprint(fileHash, capture);
            int copiedItems = 0;
            long copiedSize = 0;
            int failedCount = 0;
            var successfulPaths = new List<string>(capture.FilePaths.Length);
            for (int i = 0; i < capture.FilePaths.Length; i++)
            {
                string path = capture.FilePaths[i];
                string? storedPath = null;
                long previousSize = copiedSize;
                try
                {
                    CountImportItem(ref copiedItems);
                    string name = ClipboardImportPaths.DisplayName(path);
                    bool directory = (ClipboardImportPaths.ValidateEntry(path) & FileAttributes.Directory) != 0;
                    string stored = $"{files.Count}." + (directory ? "zip" : "bin");
                    AppendUtf8String(fileHash, (directory ? "directory:" : "file:") + name, includeLengthPrefix: true);
                    using var attachmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long maxAllowedBytes = approved ? long.MaxValue : Math.Max(0L, ApprovalThreshold - copiedSize);
                    using var inputFile = directory ? null : OpenReadFileWithRetry(path);
                    if (!approved && inputFile is { CanSeek: true } && inputFile.Length > maxAllowedBytes)
                        throw new ClipboardImportLimitException();
                    long WriteCopy(Stream output)
                    {
                        using var hashing = new ClipboardHashingStream(output, attachmentHash);
                        if (directory)
                        {
                            copiedSize = checked(copiedSize + CopyDirectory(path, hashing, ref copiedItems, maxAllowedBytes, copiedSize, approved));
                        }
                        else
                        {
                            long written = CopyStreamBounded(inputFile!, hashing, maxAllowedBytes, copiedSize, approved);
                            copiedSize = checked(copiedSize + written);
                        }
                        return hashing.BytesWritten;
                    }
                    storedPath = Path.Combine(staging, stored + (personal ? ".enc" : ""));
                    if (personal) _vault.WriteEncryptedFile(storedPath, s => WriteCopy(s));
                    else
                    {
                        using var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        WriteCopy(output);
                    }
                    File.SetAttributes(storedPath, FileAttributes.Normal);
                    fileHash.AppendData(attachmentHash.GetHashAndReset());
                    files.Add(new ClipboardFile(name, stored, directory));
                    successfulPaths.Add(path);
                }
                catch (Exception ex) when (ex is not ClipboardImportLimitException && (ex is IOException or UnauthorizedAccessException))
                {
                    RuntimeLog.Warn("CLIPBOARD", $"Could not copy file '{path}': {ex.Message}");
                    failedCount++;
                    copiedSize = previousSize;
                    if (storedPath != null) SafeFileDeletion.DeleteFile(storedPath);
                }
                progress?.Report((double)(i + 1) / capture.FilePaths.Length);
            }
            if (capture.FilePaths.Length > 0 && files.Count == 0)
                return new ClipboardImportResult(null, FailedCount: failedCount);
            capture = capture with { FilePaths = successfulPaths.ToArray() };
            if (files.Count > 0)
            {
                size = copiedSize;
                // Source files may have changed between measurement and copying.
                if (!approved && size > ApprovalThreshold) return new ClipboardImportResult(null, true, size);
            }
            if (capture.ImagePng != null)
                File.WriteAllBytes(Path.Combine(staging, "image.png" + (personal ? ".enc" : "")), personal ? _vault.Seal(capture.ImagePng) : capture.ImagePng);
            var kind = ClipboardClassifier.Detect(capture);
            if (kind == ClipboardKind.Image && files.Count > 0)
            {
                bool hasPreview = false;
                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    if (!file.IsDirectory && TryCreateImagePreview(Path.Combine(staging, file.StoredName + (personal ? ".enc" : "")), staging, personal))
                    {
                        hasPreview = true;
                        break;
                    }
                }
                if (!hasPreview)
                    kind = ClipboardKind.File;
            }
            string title;
            if (files.Count != 0)
            {
                if (files.Count == 1 && IsScreenshotFileName(files[0].Name))
                {
                    title = "Screenshot";
                }
                else
                {
                    var names = new string[files.Count];
                    for (int i = 0; i < files.Count; i++) names[i] = files[i].Name;
                    title = string.Join(", ", names);
                }
            }
            else
            {
                title = kind == ClipboardKind.Image ? "Image" : capture.Text.Trim();
            }
            bool sensitive = ClipboardSensitiveData.IsSensitive(capture.Text, capture.SourceApp, capture.SourceExecutable)
                || ClipboardSensitiveData.IsSensitive(capture.Html, capture.SourceApp, capture.SourceExecutable)
                || ClipboardSensitiveData.IsSensitive(capture.Rtf, capture.SourceApp, capture.SourceExecutable);
            string preview = title.Length > 1024 ? title[..1024] : title;
            if (kind == ClipboardKind.Color && ClipboardClassifier.TryExtractHexColor(capture.Text, out string hex))
                preview = hex;
            // A failed copy may already have fed bytes into the streaming hash.
            // Replay only committed attachments using the framed v2 format.
            if (failedCount > 0)
            {
                fileHash.GetHashAndReset();
                AppendCaptureFingerprint(fileHash, capture);
                foreach (var file in files)
                {
                    AppendUtf8String(fileHash, (file.IsDirectory ? "directory:" : "file:") + file.Name, includeLengthPrefix: true);
                    using var input = File.OpenRead(Path.Combine(staging, file.StoredName + (personal ? ".enc" : "")));
                    using var attachmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var hashing = new ClipboardHashingStream(Stream.Null, attachmentHash);
                    if (personal) _vault.TransformFile(input, hashing, encrypt: false);
                    else input.CopyTo(hashing);
                    fileHash.AppendData(attachmentHash.GetHashAndReset());
                }
            }
            // shortcut: legacy attachment hashes stay unchanged, rehash stored files if cross-version deduplication is required.
            string fingerprint = files.Count == 0 ? Fingerprint(capture)
                : "files-v2:" + Convert.ToHexString(fileHash.GetHashAndReset());
            var duplicate = FindDuplicate(fingerprint, personal);
            if (duplicate == null && kind == ClipboardKind.Image && !personal)
            {
                duplicate = FindProbableDuplicateImage(staging, capture);
            }
            if (duplicate != null)
            {
                if (duplicate.IsPersonal && !IsPersonalUnlocked) return new ClipboardImportResult(null);
                bool duplicateSensitive = sensitive || duplicate.IsSensitive;
                string duplicatePreview = kind == ClipboardKind.Color ? preview : duplicate.Preview;
                var updated = duplicate with
                {
                    IsSensitive = duplicateSensitive,
                    Title = duplicateSensitive ? ClipboardSensitiveData.Mask : duplicate.Title,
                    FileCount = duplicate.FileCount,
                    PrimaryExtension = duplicate.PrimaryExtension,
                    CopiedUtc = capture.CopiedUtc,
                    Kind = kind == ClipboardKind.Color ? kind : duplicate.Kind,
                    Preview = duplicateSensitive ? ClipboardSensitiveData.Mask : duplicatePreview,
                    MaskedPreview = duplicateSensitive ? ClipboardSensitiveData.Mask : "",
                    SensitivityVersion = ClipboardSensitiveData.DetectorVersion,
                    SourceApp = capture.SourceApp.Length == 0 ? duplicate.SourceApp : capture.SourceApp,
                    SourceExecutable = capture.SourceExecutable.Length == 0 ? duplicate.SourceExecutable : capture.SourceExecutable
                };
                WriteEntry(updated);
                PutEntry(updated);
                if (!batchChild)
                {
                    PruneCore(DateTime.UtcNow);
                    Changed?.Invoke();
                }
                return new ClipboardImportResult(updated, ImportedCount: files.Count, FailedCount: failedCount);
            }
            string displayTitle = title.Length > 120 ? title[..120] : title;
            var entry = new ClipboardEntry
            {
                IsSensitive = sensitive,
                SensitivityVersion = ClipboardSensitiveData.DetectorVersion,
                Id = id,
                Kind = kind,
                Title = sensitive ? ClipboardSensitiveData.Mask : displayTitle,
                Preview = sensitive ? ClipboardSensitiveData.Mask : preview,
                MaskedPreview = sensitive ? ClipboardSensitiveData.CreateLayoutMask(preview) : "",
                FileCount = files.Count,
                PrimaryExtension = files.Count == 1 && !files[0].IsDirectory ? Path.GetExtension(files[0].Name).ToLowerInvariant() : "",
                Fingerprint = fingerprint,
                SourceApp = capture.SourceApp,
                SourceExecutable = capture.SourceExecutable,
                CopiedUtc = capture.CopiedUtc,
                ByteSize = size,
                IsPersonal = personal,
                Groups = ClipboardClassifier.Classify(capture.Text + " " + title, kind)
            };
            var content = new ClipboardContent { Entry = entry, Text = capture.Text, Html = capture.Html, Rtf = capture.Rtf, Files = files.ToArray() };
            if (personal)
            {
                EnsurePersonalUnlocked();
            }
            WriteDocument(Path.Combine(staging, "content.json"), content, personal);
            WriteDocument(Path.Combine(staging, "entry.json"), entry, personal);
            Directory.Move(staging, EntryDirectory(entry));
            PutEntry(entry);
            if (!entry.IsSensitive) _searchText.Set(entry.Id, content.Text);
            if (!batchChild)
            {
                PruneCore(DateTime.UtcNow);
                Changed?.Invoke();
            }
            return new ClipboardImportResult(entry, ImportedCount: files.Count, FailedCount: failedCount);
        }
        finally { TryDeleteDirectory(staging); }
    }

    public Task<IReadOnlyList<ClipboardEntry>> SearchAsync(string query, string category, CancellationToken cancellation = default) => Run<IReadOnlyList<ClipboardEntry>>(() =>
    {
        bool personal = category == "Personal";
        if (personal && !IsPersonalUnlocked)
        {
            cancellation.ThrowIfCancellationRequested();
            return Array.Empty<ClipboardEntry>();
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var labelNames = tokens.Length == 0 ? null : _labels.ToDictionary(label => label.Id, label => label.Name, StringComparer.OrdinalIgnoreCase);
        List<ClipboardEntry> candidates;
        lock (_entriesLock)
        {
            candidates = new List<ClipboardEntry>(_entries.Count);
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (entry.IsPersonal == personal && MatchesCategory(entry, category))
                    candidates.Add(entry);
            }
        }

        var result = new List<ClipboardEntry>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = candidates[i];

            if (tokens.Length > 0)
            {
                string? fullText = null;
                bool matches = true;
                for (int t = 0; t < tokens.Length; t++)
                {
                    if (!EntryMatchesToken(entry, tokens[t], labelNames!, ref fullText, cancellation))
                    {
                        matches = false;
                        break;
                    }
                }
                if (!matches) continue;
            }

            result.Add(entry);
        }

        result.Sort(s_copiedUtcDescComparison);
        return result;
    });

    private bool EntryMatchesToken(ClipboardEntry entry, string token, IReadOnlyDictionary<string, string> labelNames, ref string? fullText, CancellationToken cancellation)
    {
        if (token.StartsWith('@'))
            return MatchesCategory(entry, token[1..], includeArchived: true);

        if (!entry.IsSensitive)
        {
            if (entry.Title.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
            if (entry.Preview.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (entry.SourceApp.Contains(token, StringComparison.OrdinalIgnoreCase))
            return true;

        var groups = entry.Groups;
        for (int i = 0; i < groups.Length; i++)
        {
            if (groups[i].Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
            if (labelNames.TryGetValue(groups[i], out var labelName) && labelName.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (entry.IsSensitive)
            return false;

        if (fullText == null)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (!_searchText.TryGet(entry.Id, out fullText))
                {
                    fullText = ReadContent(entry).Text;
                    _searchText.Set(entry.Id, fullText);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
            {
                RuntimeLog.Warn("CLIPBOARD", "An unreadable clipboard item was skipped during search.");
                fullText = string.Empty;
            }
        }
        return fullText.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesCategory(ClipboardEntry entry, string category, bool includeArchived = false)
    {
        category ??= "";
        _ = vn_clipboard_store_matches_category_core(
            entry.IsPersonal ? (byte)1 : (byte)0,
            entry.IsArchived ? (byte)1 : (byte)0,
            entry.IsPinned ? (byte)1 : (byte)0,
            (int)entry.Kind,
            includeArchived ? (byte)1 : (byte)0,
            category.AsSpan(),
            category.Length,
            out int res);
        if (res == 1) return true;
        if (res == 0) return false;

        var groups = entry.Groups;
        for (int i = 0; i < groups.Length; i++)
        {
            if (string.Equals(groups[i], category, StringComparison.OrdinalIgnoreCase))
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
        byte[]? bytes = null;
        try
        {
            using var stream = File.OpenRead(path);
            bytes = new byte[checked((int)stream.Length)];
            for (int offset = 0; offset < bytes.Length;)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            cancellation.ThrowIfCancellationRequested();
            if (entry.IsPersonal) bytes = _vault.Open(bytes);
            cancellation.ThrowIfCancellationRequested();
            return bytes;
        }
        catch
        {
            if (bytes != null && entry.IsPersonal) CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    });

    public Task<string[]> ExportFilesAsync(Guid id) => Run(() =>
    {
        var entry = Find(id);
        var content = ReadContent(entry);
        bool hasDir = false;
        for (int i = 0; i < content.Files.Length; i++)
        {
            if (content.Files[i].IsDirectory) { hasDir = true; break; }
        }
        EnsureDiskSpace(checked(entry.ByteSize * (hasDir ? 2 : 1)));
        string export = Path.Combine(_root, "exports", entry.IsPersonal ? "personal" : "public", Guid.NewGuid().ToString("N"));
        using var cleanupLease = AppFileCleanupService.Protect(export);
        Directory.CreateDirectory(export);
        var paths = new List<string>(Math.Max(1, content.Files.Length));
        var personalExport = entry.IsPersonal ? new ClipboardPersonalExport() : null;
        try
        {
            foreach (var file in content.Files)
            {
                string baseName = Path.GetFileName(file.Name);
                string stem = Path.GetFileNameWithoutExtension(baseName);
                string ext = Path.GetExtension(baseName);
                string destination = Path.Combine(export, baseName);
                int collisionIndex = 1;
                while (File.Exists(destination) || Directory.Exists(destination) || (file.IsDirectory && File.Exists(destination + ".zip")))
                {
                    destination = Path.Combine(export, $"{collisionIndex}-{stem}{ext}");
                    collisionIndex++;
                }
                string source = Path.Combine(EntryDirectory(entry), Path.GetFileName(file.StoredName) + (entry.IsPersonal ? ".enc" : ""));
                string materialized = file.IsDirectory ? Path.Combine(export, "." + Guid.NewGuid().ToString("N") + ".zip") : destination;
                if (personalExport != null)
                {
                    if (file.IsDirectory)
                    {
                        using var temporaryArchive = new ClipboardPersonalExport();
                        var stream = temporaryArchive.WriteFile(materialized, output =>
                        { using var input = File.OpenRead(source); _vault.TransformFile(input, output, false); });
                        ExtractArchiveDirectory(stream, destination, personalExport);
                    }
                    else personalExport.WriteFile(destination, output =>
                    { using var input = File.OpenRead(source); _vault.TransformFile(input, output, false); });
                }
                else if (file.IsDirectory)
                {
                    using var input = File.OpenRead(source);
                    ExtractArchiveDirectory(input, destination, null);
                }
                else File.Copy(source, materialized);
                paths.Add(destination);
            }
            if (entry.Kind == ClipboardKind.Image && content.Files.Length == 0)
            {
                string destination = Path.Combine(export, "Image.png");
                byte[] bytes = File.ReadAllBytes(Path.Combine(EntryDirectory(entry), "image.png" + (entry.IsPersonal ? ".enc" : "")));
                if (entry.IsPersonal) bytes = _vault.Open(bytes);
                try
                {
                    if (personalExport != null) personalExport.WriteFile(destination, output => output.Write(bytes));
                    else File.WriteAllBytes(destination, bytes);
                }
                finally { if (entry.IsPersonal) CryptographicOperations.ZeroMemory(bytes); }
                paths.Add(destination);
            }
            if (personalExport != null)
            {
                EnsurePersonalUnlocked();
                _personalExports.Add(export, personalExport);
            }
            _exportLeases.Add(export, AppFileCleanupService.Protect(export));
            return paths.ToArray();
        }
        catch { personalExport?.Dispose(); TryDeleteDirectory(export); throw; }
    });

    internal static void ExtractArchiveDirectory(Stream input, string destination, ClipboardPersonalExport? lease, long maxTotalBytes = MaxArchiveExtractBytes, int maxEntries = MaxImportContents)
    {
        Directory.CreateDirectory(destination);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        long totalBytes = 0;
        int entryIndex = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Contains(':') || entry.FullName.Contains('\0'))
                throw new InvalidDataException("Archive contains an unsafe path.");
            string path;
            try
            {
                path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("Archive contains an unsafe path.", ex);
            }
            _ = vn_clipboard_store_validate_archive_extract_step(
                entryIndex++, maxEntries, entry.Length, totalBytes, maxTotalBytes,
                root.AsSpan(), root.Length, path.AsSpan(), path.Length, out byte isValid);
            if (isValid == 0 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid archive path or extraction quota exceeded.");
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Action<Stream> copyBounded = output =>
                {
                    using var source = entry.Open();
                    byte[] buf = ArrayPool<byte>.Shared.Rent(65536);
                    try
                    {
                        int read;
                        while ((read = source.Read(buf, 0, buf.Length)) > 0)
                        {
                            totalBytes += read;
                            if (totalBytes > maxTotalBytes) throw new IOException("Archive extraction quota exceeded.");
                            output.Write(buf, 0, read);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                };
                if (lease != null) lease.WriteFile(path, copyBounded);
                else { using (var output = File.Create(path)) copyBounded(output); if (entry.LastWriteTime.Year is >= 1980 and <= 2107) File.SetLastWriteTime(path, entry.LastWriteTime.DateTime); }
            }
        }
    }

    public Task ReleasePersonalExportsAsync(IEnumerable<string>? paths = null) => Run(() =>
    {
        if (paths == null)
        {
            var allPersonal = _personalExports.Keys.ToArray();
            for (int i = 0; i < allPersonal.Length; i++)
                ReleasePersonalExport(allPersonal[i]);
            return true;
        }

        var roots = new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in paths)
            roots.Add(Path.GetDirectoryName(p));

        var toRelease = new List<string>();
        foreach (string key in _personalExports.Keys)
        {
            if (roots.Contains(key))
                toRelease.Add(key);
        }
        for (int i = 0; i < toRelease.Count; i++)
            ReleasePersonalExport(toRelease[i]);
        return true;
    });

    private void ReleasePersonalExport(string root)
    {
        bool personal = _personalExports.Remove(root, out var lease);
        lease?.Dispose();
        if (_exportLeases.Remove(root, out var cleanupLease)) cleanupLease.Dispose();
        if (personal) TryDeleteDirectory(root);
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
        var targetIds = ids.Distinct().ToArray();
        return Run(() =>
        {
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
                if (groups != null)
                {
                    updatedGroups = FilterValidGroups(groups, entry.Groups);
                }
                else if (!string.IsNullOrEmpty(addGroup) && IsLabel(addGroup))
                {
                    bool alreadyHas = false;
                    for (int g = 0; g < entry.Groups.Length; g++)
                    {
                        if (string.Equals(entry.Groups[g], addGroup, StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyHas = true;
                            break;
                        }
                    }
                    if (!alreadyHas)
                    {
                        var nextGroups = new string[entry.Groups.Length + 1];
                        Array.Copy(entry.Groups, nextGroups, entry.Groups.Length);
                        nextGroups[^1] = addGroup;
                        updatedGroups = FilterValidGroups(nextGroups, entry.Groups);
                    }
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

    public Task MovePersonalAsync(Guid id, bool personal) => Run(() =>
    {
        if (!IsPersonalUnlocked) throw new InvalidOperationException("Unlock Personal first.");
        var entry = Find(id);
        if (entry.IsPersonal == personal) return true;
        var content = ReadContent(entry);
        var moved = entry with { IsPersonal = personal };
        EnsureDiskSpace(checked(entry.ByteSize + (entry.ByteSize / (1024 * 1024) + 1) * 28 + 64 * 1024));
        string staging = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
        using var cleanupLease = AppFileCleanupService.Protect(staging);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string source in Directory.EnumerateFiles(EntryDirectory(entry)))
            {
                string name = Path.GetFileName(source);
                if (name.StartsWith("entry.", StringComparison.Ordinal) || name.StartsWith("content.", StringComparison.Ordinal)) continue;
                if (name is "image.png" or "image.png.enc")
                {
                    byte[] bytes = File.ReadAllBytes(source);
                    if (!personal) bytes = _vault.Open(bytes);
                    try { File.WriteAllBytes(Path.Combine(staging, personal ? name + ".enc" : name[..^4]), personal ? _vault.Seal(bytes) : bytes); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                else if (personal) _vault.TransformFile(source, Path.Combine(staging, name + ".enc"), true);
                else _vault.TransformFile(source, Path.Combine(staging, name[..^4]), false);
            }
            WriteDocument(Path.Combine(staging, "content.json"), content with { Entry = moved }, personal);
            WriteDocument(Path.Combine(staging, "entry.json"), moved, personal);
            string sourceDir = EntryDirectory(entry);
            string destination = EntryDirectory(moved);
            string backup = sourceDir + ".moving";
            if (Directory.Exists(backup)) TryDeleteDirectory(backup);
            string previous = staging + ".previous";
            bool hadDestination = Directory.Exists(destination);
            if (hadDestination) Directory.Move(destination, previous);
            try
            {
                Directory.Move(sourceDir, backup);
            }
            catch
            {
                if (hadDestination && !Directory.Exists(destination) && Directory.Exists(previous))
                    Directory.Move(previous, destination);
                throw;
            }
            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                try { if (!Directory.Exists(sourceDir) && Directory.Exists(backup)) Directory.Move(backup, sourceDir); } catch { }
                if (hadDestination && !Directory.Exists(destination) && Directory.Exists(previous))
                    Directory.Move(previous, destination);
                throw;
            }
            TryDeleteDirectory(backup);
            if (hadDestination) TryDeleteDirectory(previous);
            PutEntry(moved);
            Changed?.Invoke();
            return true;
        }
        finally { if (Directory.Exists(staging)) SafeFileDeletion.DeleteDirectory(staging); }
    });

    private void ReconcileInterruptedMoves()
    {
        string itemsRoot = Path.Combine(_root, "items");
        string personalRoot = Path.Combine(_root, "personal");
        ReconcileMovingDirectories(itemsRoot, personalRoot);
        ReconcileMovingDirectories(personalRoot, itemsRoot);
        if (Directory.Exists(itemsRoot) && Directory.Exists(personalRoot))
        {
            foreach (string personalDir in Directory.EnumerateDirectories(personalRoot))
            {
                string name = Path.GetFileName(personalDir);
                if (!Guid.TryParseExact(name, "N", out var id)) continue;
                string publicDir = Path.Combine(itemsRoot, name);
                if (Directory.Exists(publicDir) && TryDeleteDirectory(publicDir))
                {
                    ClipboardEntry? existing;
                    lock (_entriesLock)
                    {
                        _entries.TryGetValue(id, out existing);
                    }
                    if (existing is { IsPersonal: false })
                        RemoveEntry(id);
                }
            }
        }
    }

    private static void ReconcileMovingDirectories(string sourceRoot, string counterpartRoot)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (string movingDir in Directory.EnumerateDirectories(sourceRoot, "*.moving"))
        {
            string fileName = Path.GetFileName(movingDir);
            if (fileName.Length <= ".moving".Length) continue;
            string idPart = fileName[..^".moving".Length];
            if (!Guid.TryParseExact(idPart, "N", out _)) continue;
            string originalDir = Path.Combine(sourceRoot, idPart);
            string counterpartDir = Path.Combine(counterpartRoot, idPart);
            if (Directory.Exists(counterpartDir) || Directory.Exists(originalDir))
            {
                TryDeleteDirectory(movingDir);
            }
            else
            {
                try { Directory.Move(movingDir, originalDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public async Task<bool> UnlockPersonalAsync(System.Security.SecureString pin, bool create = false)
        => (await UnlockPersonalWithResultAsync(pin, create).ConfigureAwait(false)).Succeeded;

    public async Task<ClipboardUnlockResult> UnlockPersonalWithResultAsync(System.Security.SecureString pin, bool create = false)
    {
        // Own a snapshot while waiting for serialized disk work. The caller may clear its input immediately.
        using var snapshot = pin.Copy();
        snapshot.MakeReadOnly();
        return await Run(() =>
        {
            var result = _vault.UnlockWithResult(snapshot, create);
            if (!result.Succeeded) return result;
            try
            {
                ReconcileInterruptedMoves();
                var loaded = new List<ClipboardEntry>();
                foreach (string directory in Directory.EnumerateDirectories(Path.Combine(_root, "personal")))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
                    try
                    {
                        var entry = ReadDocument<ClipboardEntry>(Path.Combine(directory, "entry.json"), true);
                        if (entry.Id == id && entry.IsPersonal) loaded.Add(RestoreFileMetadata(entry));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
                    { RuntimeLog.Warn("CLIPBOARD", "An unreadable Personal item was preserved; other items remain available."); }
                }
                RemovePersonalEntriesCore();
                for (int i = 0; i < loaded.Count; i++) PutEntry(loaded[i]);
                _personalUnlocked = true;
                PruneSensitiveCore(DateTime.UtcNow);
                Changed?.Invoke();
                return result;
            }
            catch { LockPersonalCore(); throw; }
        }).ConfigureAwait(false);
    }

    public Task LockPersonalAsync()
    {
        _personalUnlocked = false;
        return Run(() => { LockPersonalCore(); Changed?.Invoke(); return true; });
    }

    /// <summary>Destructive recovery: the UI must confirm before calling this method.</summary>
    public Task ResetPersonalAsync() => Run(() =>
    {
        LockPersonalCore();
        string directory = Path.Combine(_root, "personal");
        SafeFileDeletion.DeleteDirectory(directory);
        TryDeleteDirectory(Path.Combine(_root, "sensitive-expiry"));
        _vault.Reset();
        Directory.CreateDirectory(directory);
        Changed?.Invoke();
        return true;
    });

    private void LockPersonalCore()
    {
        _personalUnlocked = false;
        _vault.Lock();
        if (_personalExports.Count > 0)
        {
            var roots = new string[_personalExports.Count];
            _personalExports.Keys.CopyTo(roots, 0);
            for (int i = 0; i < roots.Length; i++) ReleasePersonalExport(roots[i]);
        }
        RemovePersonalEntriesCore();
        string exports = Path.Combine(_root, "exports", "personal");
        if (Directory.Exists(exports))
            TryDeleteDirectory(exports);
    }

    private void RemovePersonalEntriesCore()
    {
        List<Guid>? personalIds = null;
        lock (_entriesLock)
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.IsPersonal)
                {
                    personalIds ??= new List<Guid>();
                    personalIds.Add(pair.Key);
                }
            }
        }
        if (personalIds != null)
        {
            for (int i = 0; i < personalIds.Count; i++)
                RemoveEntry(personalIds[i]);
        }
    }

    public Task DeleteAsync(Guid id) => Run(() =>
    {
        var entry = Find(id);
        SafeFileDeletion.DeleteDirectory(EntryDirectory(entry));
        RemoveEntry(id);
        Changed?.Invoke();
        return true;
    });

    /// <summary>Deletes a selection in one queued operation and publishes one refresh.</summary>
    public Task DeleteManyAsync(IReadOnlyCollection<Guid> ids)
    {
        var selectedIds = ids.Distinct().ToArray();
        return Run(() =>
        {
            bool changed = false;
            try
            {
                foreach (var id in selectedIds)
                {
                    ClipboardEntry? entry;
                    lock (_entriesLock)
                    {
                        _entries.TryGetValue(id, out entry);
                    }
                    if (entry == null || (entry.IsPersonal && !IsPersonalUnlocked)) continue;
                    SafeFileDeletion.DeleteDirectory(EntryDirectory(entry));
                    RemoveEntry(id);
                    changed = true;
                }
                return true;
            }
            finally { if (changed) Changed?.Invoke(); }
        });
    }

    /// <summary>Deletes only the confirmed items, rechecking protection in the serialized work queue.</summary>
    public Task EmptyTrayAsync(IReadOnlyCollection<Guid> ids)
    {
        var confirmedIds = new Guid[ids.Count];
        if (ids is ICollection<Guid> collection)
        {
            collection.CopyTo(confirmedIds, 0);
        }
        else
        {
            int idx = 0;
            foreach (Guid id in ids)
                confirmedIds[idx++] = id;
        }
        return Run(() =>
        {
            bool changed = false;
            try
            {
                for (int i = 0; i < confirmedIds.Length; i++)
                {
                    Guid id = confirmedIds[i];
                    ClipboardEntry? entry;
                    lock (_entriesLock)
                    {
                        _entries.TryGetValue(id, out entry);
                    }
                    if (entry == null || entry.IsPinned || entry.IsPersonal || entry.IsArchived) continue;
                    SafeFileDeletion.DeleteDirectory(EntryDirectory(entry));
                    RemoveEntry(id);
                    changed = true;
                }
                return true;
            }
            finally
            {
                // Refresh even if a later item could not be removed.
                if (changed) Changed?.Invoke();
            }
        });
    }

    public Task PruneAsync(DateTime nowUtc) => Run(() => { PruneCore(nowUtc); DeleteExportsCore(); Changed?.Invoke(); return true; });
    private void PruneCore(DateTime nowUtc)
    {
        PruneSensitiveCore(nowUtc);
        DateTime cutoffUtc = nowUtc.AddDays(-30);
        List<ClipboardEntry>? expired = null;
        lock (_entriesLock)
        {
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (!entry.IsPersonal && !entry.IsPinned && !entry.IsArchived && entry.CopiedUtc < cutoffUtc)
                {
                    expired ??= new List<ClipboardEntry>();
                    expired.Add(entry);
                }
            }
        }
        if (expired != null)
        {
            for (int i = 0; i < expired.Count; i++)
            {
                var entry = expired[i];
                if (TryDeleteDirectory(EntryDirectory(entry))) RemoveEntry(entry.Id);
            }
        }

        int maxEntries = Math.Max(0, MaxUnpinnedEntries);
        long maxBytes = Math.Max(0L, MaxUnpinnedBytes);
        List<ClipboardEntry>? evictable = null;
        long totalBytes = 0;
        lock (_entriesLock)
        {
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (!entry.IsPersonal && !entry.IsPinned && !entry.IsArchived)
                {
                    evictable ??= new List<ClipboardEntry>();
                    evictable.Add(entry);
                    totalBytes = checked(totalBytes + Math.Max(0L, entry.ByteSize));
                }
            }
        }
        if (evictable != null && (evictable.Count > maxEntries || totalBytes > maxBytes))
        {
            evictable.Sort(s_copiedUtcAscComparison);
            int remainingCount = evictable.Count;
            for (int i = 0; i < evictable.Count && (remainingCount > maxEntries || totalBytes > maxBytes); i++)
            {
                var entry = evictable[i];
                if (TryDeleteDirectory(EntryDirectory(entry)))
                {
                    RemoveEntry(entry.Id);
                    remainingCount--;
                    totalBytes -= Math.Max(0L, entry.ByteSize);
                }
            }
        }
    }
    public Task ExpireSensitiveAsync(DateTime nowUtc) => Run(() => { PruneSensitiveCore(nowUtc); return true; });

    private void PruneSensitiveCore(DateTime nowUtc)
    {
        if (!AutoDeleteSensitive) return;
        string root = Path.Combine(_root, "sensitive-expiry");
        if (!Directory.Exists(root)) return;
        bool changed = false;
        long expiryTicks = nowUtc.AddSeconds(-60).Ticks;
        foreach (string marker in Directory.EnumerateFiles(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(marker), "N", out var id)) continue;
            string personalDir = Path.Combine(_root, "personal", id.ToString("N"));
            string publicDir = Path.Combine(_root, "items", id.ToString("N"));
            ClipboardEntry? existing;
            lock (_entriesLock)
            {
                _entries.TryGetValue(id, out existing);
            }
            if (existing == null && Directory.Exists(publicDir))
            {
                try
                {
                    existing = JsonSerializer.Deserialize<ClipboardEntry>(File.ReadAllBytes(Path.Combine(publicDir, "entry.json")));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            if ((existing != null && (existing.IsPinned || existing.IsArchived || existing.IsPersonal)) ||
                Directory.Exists(personalDir))
            {
                try { SafeFileDeletion.DeleteFile(marker); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                continue;
            }
            if (!long.TryParse(File.ReadAllText(marker), out long ticks) || ticks > expiryTicks) continue;
            if (TryDeleteDirectory(publicDir))
            {
                try { SafeFileDeletion.DeleteFile(marker); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                RemoveEntry(id);
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void DeleteExportsCore()
    {
        string root = Path.Combine(_root, "exports");
        if (!Directory.Exists(root)) return;
        SafeFileDeletion.ValidatePath(root);
        string[] dirs = new[] { "public", "personal" }
            .Select(category => Path.Combine(root, category)).Where(Directory.Exists)
            .SelectMany(category =>
            {
                SafeFileDeletion.ValidatePath(category);
                return Directory.GetDirectories(category);
            })
            .Where(directory => Guid.TryParseExact(Path.GetFileName(directory), "N", out _)).ToArray();
        DateTime cutoff = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < dirs.Length; i++)
        {
            string directory = dirs[i];
            if (_exportLeases.Keys.Any(active => directory.Equals(active, StringComparison.OrdinalIgnoreCase) ||
                active.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                directory.StartsWith(active + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                TryDeleteDirectory(directory);
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            SafeFileDeletion.DeleteDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Warn("CLIPBOARD", "Clipboard cleanup is pending; a file is still in use.");
            return false;
        }
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
                IsSensitive = ClipboardSensitiveData.IsSensitive(content.Text, entry.SourceApp, entry.SourceExecutable)
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
        lock (_entriesLock)
        {
            if (_entries.TryGetValue(entry.Id, out var previous)) RemoveFingerprint(previous);
            _entries[entry.Id] = entry;
            Interlocked.Increment(ref _entriesMutationVersion);
            _cachedCategoryCounts = null;
            var key = (entry.Fingerprint, entry.IsPersonal);
            if (!_fingerprints.TryGetValue(key, out var ids)) _fingerprints[key] = ids = new HashSet<Guid>();
            ids.Add(entry.Id);
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
        lock (_entriesLock)
        {
            if (!_fingerprints.TryGetValue((fingerprint, personal), out var ids)) return null;

            ClipboardEntry? latest = null;
            foreach (Guid id in ids)
            {
                if (_entries.TryGetValue(id, out var entry) &&
                    entry.IsPersonal == personal &&
                    (latest == null || entry.CopiedUtc > latest.CopiedUtc))
                {
                    latest = entry;
                }
            }
            return latest;
        }
    }

    private void RemoveFingerprint(ClipboardEntry entry)
    {
        var key = (entry.Fingerprint, entry.IsPersonal);
        if (!_fingerprints.TryGetValue(key, out var ids)) return;
        ids.Remove(entry.Id);
        if (ids.Count == 0) _fingerprints.Remove(key);
    }

    private void RemoveEntry(Guid id)
    {
        _searchText.Remove(id);
        DeleteSensitiveExpiryMarker(id);
        lock (_entriesLock)
        {
            if (_entries.TryRemove(id, out var entry))
            {
                Interlocked.Increment(ref _entriesMutationVersion);
                _cachedCategoryCounts = null;
                RemoveFingerprint(entry);
            }
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
    private string EntryDirectory(ClipboardEntry entry) => Path.Combine(_root, entry.IsPersonal ? "personal" : "items", entry.Id.ToString("N"));
    private ClipboardContent ReadContent(ClipboardEntry entry) =>
        ReadDocument<ClipboardContent>(Path.Combine(EntryDirectory(entry), "content.json"), entry.IsPersonal) with { Entry = entry };
    private T ReadDocument<T>(string path, bool personal)
    {
        string target = path + (personal ? ".enc" : "");
        if (!personal)
        {
            using var stream = File.OpenRead(target);
            return JsonSerializer.Deserialize<T>(stream) ?? throw new JsonException("Invalid clipboard record.");
        }
        using (var input = File.OpenRead(target))
        {
            if (input.Length >= 44)
            {
                Span<byte> magic = stackalloc byte[4];
                if (input.Read(magic) == 4 && (magic.SequenceEqual("VNC1"u8) || magic.SequenceEqual("VCF1"u8)))
                {
                    input.Position = 0;
                    using var plainStream = new PooledChunkStream();
                    _vault.TransformFile(input, plainStream, encrypt: false);
                    plainStream.Position = 0;
                    return JsonSerializer.Deserialize<T>(plainStream) ?? throw new JsonException("Invalid clipboard record.");
                }
            }
        }
        byte[] bytes = File.ReadAllBytes(target);
        bytes = _vault.Open(bytes);
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new JsonException("Invalid clipboard record."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void WriteDocument<T>(string path, T value, bool personal)
    {
        string target = path + (personal ? ".enc" : "");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (personal)
            {
                using var plainStream = new PooledChunkStream();
                JsonSerializer.Serialize(plainStream, value);
                plainStream.Position = 0;
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    _vault.TransformFile(plainStream, output, encrypt: true);
                    output.Flush(true);
                }
            }
            else
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, value);
                    output.Flush(true);
                }
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }
    private void WriteEntry(ClipboardEntry entry) => WriteDocument(Path.Combine(EntryDirectory(entry), "entry.json"), entry, entry.IsPersonal);

    private sealed class PooledChunkStream : Stream
    {
        private const int ChunkSize = 65536;
        private readonly List<byte[]> _chunks = new();
        private long _length;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = _length - _position;
            if (remaining <= 0 || buffer.IsEmpty) return 0;
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int totalRead = 0;
            while (totalRead < toRead)
            {
                int chunkIndex = (int)(_position / ChunkSize);
                int chunkOffset = (int)(_position % ChunkSize);
                int copy = Math.Min(toRead - totalRead, ChunkSize - chunkOffset);
                _chunks[chunkIndex].AsSpan(chunkOffset, copy).CopyTo(buffer.Slice(totalRead, copy));
                _position += copy;
                totalRead += copy;
            }
            return totalRead;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int totalWritten = 0;
            while (totalWritten < buffer.Length)
            {
                int chunkIndex = (int)(_position / ChunkSize);
                int chunkOffset = (int)(_position % ChunkSize);
                while (_chunks.Count <= chunkIndex)
                    _chunks.Add(ArrayPool<byte>.Shared.Rent(ChunkSize));
                int copy = Math.Min(buffer.Length - totalWritten, ChunkSize - chunkOffset);
                buffer.Slice(totalWritten, copy).CopyTo(_chunks[chunkIndex].AsSpan(chunkOffset, copy));
                _position += copy;
                totalWritten += copy;
                if (_position > _length) _length = _position;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return _position;
        }

        public override void SetLength(long value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _length = value;
            if (_position > _length) _position = _length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                for (int i = 0; i < _chunks.Count; i++)
                {
                    CryptographicOperations.ZeroMemory(_chunks[i].AsSpan(0, ChunkSize));
                    ArrayPool<byte>.Shared.Return(_chunks[i]);
                }
                _chunks.Clear();
            }
            base.Dispose(disposing);
        }
    }

    private static long CopyStreamBounded(Stream input, Stream destination, long maxAllowedBytes, long copiedSizeSoFar, bool approved)
    {
        if (!approved && input.CanSeek && input.Length > maxAllowedBytes)
            throw new ClipboardImportLimitException();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written = checked(written + read);
                if (!approved && written > maxAllowedBytes)
                    throw new ClipboardImportLimitException();
                destination.Write(buffer, 0, read);
            }
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private long CopyDirectory(string source, Stream destination, ref int copiedItems, long maxAllowedBytes, long copiedSizeSoFar, bool approved)
    {
        long size = 0;
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (string path in EnumerateDirectory(source))
        {
            CountImportItem(ref copiedItems);
            string name = Path.GetRelativePath(source, path).Replace('\\', '/');
            var attributes = ClipboardImportPaths.ValidateEntry(path);
            var entry = archive.CreateEntry(name + ((attributes & FileAttributes.Directory) != 0 ? "/" : ""), CompressionLevel.NoCompression);
            // Preserve source dates, including empty folders, instead of using the
            // import time (which would make identical folder copies hash differently).
            var modified = new DateTimeOffset(File.GetLastWriteTime(path));
            entry.LastWriteTime = modified.Year is >= 1980 and <= 2107 ? modified
                : new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                using var input = OpenReadFileWithRetry(path);
                long remaining = approved ? long.MaxValue : Math.Max(0L, maxAllowedBytes - size);
                if (!approved && input.CanSeek && input.Length > remaining)
                    throw new ClipboardImportLimitException();
                using var output = entry.Open();
                long written = CopyStreamBounded(input, output, remaining, checked(copiedSizeSoFar + size), approved);
                size = checked(size + written);
            }
        }
        return size;
    }

    private bool TryCreateImagePreview(string source, string directory, bool personal)
    {
        using var decrypted = personal ? new MemoryStream() : null;
        try
        {
            using var input = File.OpenRead(source);
            if (decrypted != null) { _vault.TransformFile(input, decrypted, false); decrypted.Position = 0; }
            Stream stream = decrypted ?? (Stream)input;
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            stream.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, 840);
            else bitmap.DecodePixelHeight = Math.Min(height, 840);
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            try
            {
                var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
                File.WriteAllBytes(Path.Combine(directory, "image.png" + (personal ? ".enc" : "")), personal ? _vault.Seal(bytes) : bytes.ToArray());
            }
            finally { if (personal) CryptographicOperations.ZeroMemory(output.GetBuffer()); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            SafeFileDeletion.DeleteFile(Path.Combine(directory, "image.png" + (personal ? ".enc" : "")));
            return false;
        }
        finally { if (decrypted != null) CryptographicOperations.ZeroMemory(decrypted.GetBuffer()); }
    }

    private static bool IsScreenshotFileName(string name) =>
        !string.IsNullOrEmpty(name) &&
        vn_clipboard_store_is_screenshot_file_name(name.AsSpan(), name.Length, out byte isSs) == 0 &&
        isSs != 0;

    private ClipboardEntry? FindProbableDuplicateImage(string staging, ClipboardCapture capture)
    {
        if (capture.FilePaths.Length > 1) return null;
        string stagingImg = Path.Combine(staging, "image.png");
        if (!File.Exists(stagingImg)) return null;

        ClipboardEntry? recent = null;
        lock (_entriesLock)
        {
            foreach (var pair in _entries)
            {
                var e = pair.Value;
                if (e.Kind == ClipboardKind.Image && !e.IsPersonal && Math.Abs((capture.CopiedUtc - e.CopiedUtc).TotalSeconds) < 2.0 && (recent == null || e.CopiedUtc > recent.CopiedUtc))
                {
                    recent = e;
                }
            }
        }

        if (recent == null || ReadContent(recent).Files.Length > 1) return null;

        bool isScreenshotRelated = (capture.FilePaths.Length == 1 && IsScreenshotFileName(Path.GetFileName(capture.FilePaths[0])))
            || recent.Title == "Screenshot"
            || (recent.SourceApp.Length > 0 && recent.SourceApp == capture.SourceApp &&
                vn_clipboard_store_is_screenshot_source_app(recent.SourceApp.AsSpan(), recent.SourceApp.Length, out byte isSsApp) == 0 &&
                isSsApp != 0);

        if (!isScreenshotRelated) return null;

        string existingImg = Path.Combine(EntryDirectory(recent), "image.png");
        if (!File.Exists(existingImg)) return null;

        try
        {
            if (ImagePixelFingerprint(File.ReadAllBytes(existingImg)) == ImagePixelFingerprint(File.ReadAllBytes(stagingImg)))
                return recent;
        }
        catch
        {
            // Ignore decoder errors
        }

        return null;
    }

    private IEnumerable<string> EnumerateDirectory(string root)
    {
        var pending = new Stack<string>();
        var children = new List<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            children.Clear();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                children.Add(entry);
                if (children.Count >= MaxImportContents + 1) break;
            }
            children.Sort(StringComparer.Ordinal);
            for (int i = 0; i < children.Count; i++)
            {
                string path = children[i];
                // A drive/root-folder drop must never archive its own growing staging area.
                if (path.Equals(_root, StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = ClipboardImportPaths.ValidateEntry(path);
                yield return path;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    private static void CountImportItem(ref int count)
    {
        if (++count > MaxImportContents) throw new ClipboardImportLimitException();
    }

    private long MeasurePath(string path, ref int measuredItems)
    {
        CountImportItem(ref measuredItems);
        if ((ClipboardImportPaths.ValidateEntry(path) & FileAttributes.Directory) == 0) return new FileInfo(path).Length;
        long size = 0;
        foreach (string child in EnumerateDirectory(path))
        {
            CountImportItem(ref measuredItems);
            if ((ClipboardImportPaths.ValidateEntry(child) & FileAttributes.Directory) == 0)
                size = checked(size + new FileInfo(child).Length);
        }
        return size;
    }
    private static string Fingerprint(ClipboardCapture capture)
    {
        if (capture.ImagePng != null) return ImagePixelFingerprint(capture.ImagePng);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCaptureFingerprint(hash, capture);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ImagePixelFingerprint(byte[] image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Clipboard owners can publish the same bitmap several times with different
        // auxiliary formats or PNG metadata. Image identity is its pixels, not its encoding.
        byte[]? rentedRow = null;
        try
        {
            using var input = new MemoryStream(image, writable: false);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var pixels = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],
                System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            int width = pixels.PixelWidth;
            int height = pixels.PixelHeight;
            Span<byte> dims = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(dims[..4], width);
            BinaryPrimitives.WriteInt32LittleEndian(dims[4..8], height);
            hash.AppendData(dims);
            int stride = checked(width * 4);
            rentedRow = ArrayPool<byte>.Shared.Rent(stride);
            Span<byte> rowSpan = rentedRow.AsSpan(0, stride);
            for (int y = 0; y < height; y++)
            {
                pixels.CopyPixels(new System.Windows.Int32Rect(0, y, width, 1), rentedRow, stride, 0);
                hash.AppendData(rowSpan);
            }
            return "image-v2:" + Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException
            or System.Runtime.InteropServices.COMException or InvalidOperationException or OverflowException)
        {
            // Decoding can fail after rows have been hashed; start with a fresh hash.
            return "image-raw:" + Convert.ToHexString(SHA256.HashData(image));
        }
        finally
        {
            if (rentedRow != null)
                ArrayPool<byte>.Shared.Return(rentedRow);
        }
    }

    private static void AppendCaptureFingerprint(IncrementalHash hash, ClipboardCapture capture)
    {
        AppendUtf8String(hash, capture.Text, includeLengthPrefix: true);
        AppendUtf8String(hash, capture.Html, includeLengthPrefix: true);
        AppendUtf8String(hash, capture.Rtf, includeLengthPrefix: true);
        if (capture.ImagePng != null) hash.AppendData(capture.ImagePng);
    }

    private static void AppendUtf8String(IncrementalHash hash, string? value, bool includeLengthPrefix)
    {
        value ??= string.Empty;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (includeLengthPrefix)
        {
            Span<byte> lenBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, byteCount);
            hash.AppendData(lenBytes);
        }
        if (byteCount == 0) return;

        if (byteCount <= 512)
        {
            Span<byte> utf8 = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(value.AsSpan(), utf8);
            hash.AppendData(utf8);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = Encoding.UTF8.GetBytes(value.AsSpan(), rented.AsSpan(0, byteCount));
                hash.AppendData(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

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
            var task = _pending.ContinueWith(_ => action(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            _pending = task;
            return task;
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
            var pending = _pending;
            _disposeTask = DisposeCoreAsync(pending);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); } catch { /* Failed operations still release their resources. */ }
        LockPersonalCore();
        _searchText.Clear();
        _vault.Dispose();
        foreach (var lease in _exportLeases.Values) lease.Dispose();
        _exportLeases.Clear();
    }
}
