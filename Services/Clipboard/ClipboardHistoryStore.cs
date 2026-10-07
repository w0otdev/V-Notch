using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

/// <summary>Owns immutable local copies, retention, deduplication and the Personal vault.
/// Disk work is serialized off the UI thread; full-text search uses a bounded memory cache.</summary>
public sealed class ClipboardHistoryStore : IDisposable, IAsyncDisposable
{
    public const long ApprovalThreshold = 50L * 1024 * 1024;
    public const int MaxImportItems = 512;
    public const int MaxImportContents = 100_000;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, ClipboardEntry> _entries = new();
    private readonly ClipboardVault _vault;
    private readonly Dictionary<(string Fingerprint, bool Personal), HashSet<Guid>> _fingerprints = new();
    private readonly ClipboardSearchTextCache _searchText = new();
    private Task _pending = Task.CompletedTask;
    private Task? _disposeTask;
    private volatile bool _personalUnlocked;
    private bool _disposed;
    private bool _initialized;
    private readonly Func<long> _availableSpace;
    private readonly Dictionary<string, ClipboardPersonalExport> _personalExports = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;
    public bool IsPersonalUnlocked => _personalUnlocked;
    public bool HasPersonalPasscode => _vault.IsConfigured;

    public ClipboardHistoryStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-Notch", "Clipboard");
        _root = Path.GetFullPath(_root);
        _vault = new ClipboardVault(_root);
        _availableSpace = () => new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
    }

    internal ClipboardHistoryStore(string root, Func<long> availableSpace) : this(root) => _availableSpace = availableSpace;

    public Task InitializeAsync() => Run(() =>
    {
        if (_initialized) return true;
        Directory.CreateDirectory(Path.Combine(_root, "items"));
        Directory.CreateDirectory(Path.Combine(_root, "personal"));
        LockPersonalCore();
        string staging = Path.Combine(_root, "staging");
        TryDeleteDirectory(staging);
        foreach (string directory in Directory.EnumerateDirectories(Path.Combine(_root, "items")))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ClipboardEntry>(File.ReadAllBytes(Path.Combine(directory, "entry.json")));
                if (entry != null && entry.Id == id && !entry.IsPersonal) PutEntry(RestoreFileMetadata(entry));
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
        return _entries.Values.Where(entry => entry.IsPersonal == personal && (!personal || IsPersonalUnlocked))
            .OrderByDescending(entry => entry.CopiedUtc).ToArray();
    }

    public ClipboardEntry? GetEntry(Guid id) => _entries.TryGetValue(id, out var entry) &&
        (!entry.IsPersonal || IsPersonalUnlocked) ? entry : null;

    public IReadOnlyDictionary<string, int> GetCategoryCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Increment(string name) => counts[name] = counts.GetValueOrDefault(name) + 1;
        foreach (var entry in _entries.Values.Where(entry => !entry.IsPersonal))
        {
            Increment(entry.IsArchived ? "Archive" : "All");
            if (entry.IsPinned) Increment("Pin");
            if (entry.IsArchived) continue;
            Increment(entry.Kind switch
            {
                ClipboardKind.Link => "Links",
                ClipboardKind.Image => "Images",
                ClipboardKind.Color => "Colors",
                ClipboardKind.File => "Files",
                _ => entry.Kind.ToString()
            });
            foreach (string group in entry.Groups) Increment(group);
        }
        return counts;
    }

    public Task<ClipboardImportResult> ImportAsync(ClipboardCapture capture, bool approved = false, IProgress<double>? progress = null)
    {
        // Reject oversized drops before they enter the serialized disk-work queue.
        if (capture.FilePaths.Length > MaxImportItems)
            return Task.FromException<ClipboardImportResult>(new ClipboardImportLimitException());
        if (capture.FilePaths.Any(path => !ClipboardImportPaths.IsLocal(path)))
            return Task.FromException<ClipboardImportResult>(new IOException("Only local paths can be saved in history."));
        return Run(() => ImportCore(capture, approved, progress));
    }

    private ClipboardImportResult ImportCore(ClipboardCapture capture, bool approved, IProgress<double>? progress = null)
    {
        bool personal = capture.TargetCategory == "Personal";
        if (personal && !IsPersonalUnlocked) throw new InvalidOperationException("Unlock Personal before importing.");
        long size = capture.ImagePng?.LongLength ?? Encoding.UTF8.GetByteCount(capture.Text + capture.Html + capture.Rtf);
        int measuredItems = 0;
        if (capture.FilePaths.Length > 0)
        {
            size = 0;
            capture = capture with { FilePaths = capture.FilePaths.Select(ClipboardImportPaths.Normalize).ToArray() };
            foreach (string path in capture.FilePaths) size = checked(size + MeasurePath(path, ref measuredItems));
        }
        if (!approved && (capture.FilePaths.Length > 0 || size > ApprovalThreshold) && size > ApprovalThreshold)
            return new ClipboardImportResult(null, true, size);
        if (size == 0 && capture.FilePaths.Length == 0) return new ClipboardImportResult(null);
        EnsureDiskSpace(checked(size * (personal ? 2 : 1) + (capture.ImagePng?.LongLength ?? 0) + measuredItems * 1024L));

        if (capture.SeparateFiles && capture.FilePaths.Length > 1)
        {
            // Preflight the whole drop above before splitting it, preserving the
            // aggregate approval, disk-space and directory-content limits.
            var entries = new List<ClipboardEntry>();
            int imported = 0, failed = 0;
            bool needsApproval = false;
            long approvalSize = 0;
            for (int i = 0; i < capture.FilePaths.Length; i++)
            {
                var result = ImportCore(capture with { FilePaths = [capture.FilePaths[i]], SeparateFiles = false }, approved);
                if (result.Entry != null) entries.Add(result.Entry);
                imported += result.ImportedCount;
                failed += result.FailedCount;
                needsApproval |= result.NeedsApproval;
                approvalSize = checked(approvalSize + (result.NeedsApproval ? result.ByteSize : result.Entry?.ByteSize ?? 0));
                progress?.Report((double)(i + 1) / capture.FilePaths.Length);
            }
            return new ClipboardImportResult(entries.FirstOrDefault(), needsApproval, approvalSize, imported, failed)
            { Entries = entries };
        }

        Guid id = Guid.NewGuid();
        string staging = Path.Combine(_root, "staging", id.ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<ClipboardFile>();
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendCaptureFingerprint(fileHash, capture);
            int copiedItems = 0;
            long copiedSize = 0;
            int failedCount = 0;
            var successfulPaths = new List<string>();
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
                    fileHash.AppendData(Encoding.UTF8.GetBytes(name));
                    long WriteCopy(Stream output)
                    {
                        using var hashing = new ClipboardHashingStream(output, fileHash);
                        if (directory) copiedSize = checked(copiedSize + CopyDirectory(path, hashing, ref copiedItems));
                        else
                        {
                            using var input = OpenReadFileWithRetry(path);
                            input.CopyTo(hashing);
                            copiedSize = checked(copiedSize + input.Position);
                        }
                        return hashing.BytesWritten;
                    }
                    storedPath = Path.Combine(staging, stored + (personal ? ".enc" : ""));
                    if (personal) _vault.WriteEncryptedFile(storedPath, WriteCopy);
                    else
                    {
                        using var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        WriteCopy(output);
                    }
                    File.SetAttributes(storedPath, FileAttributes.Normal);
                    files.Add(new ClipboardFile(name, stored, directory));
                    successfulPaths.Add(path);
                }
                catch (Exception ex) when (ex is not ClipboardImportLimitException && (ex is IOException or UnauthorizedAccessException))
                {
                    RuntimeLog.Warn("CLIPBOARD", $"Could not copy file '{path}': {ex.Message}");
                    failedCount++;
                    copiedSize = previousSize;
                    if (storedPath != null) File.Delete(storedPath);
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
            if (kind == ClipboardKind.Image && files.Count > 0 &&
                !files.Any(file => !file.IsDirectory && TryCreateImagePreview(Path.Combine(staging, file.StoredName + (personal ? ".enc" : "")), staging, personal)))
                kind = ClipboardKind.File;
            string title = files.Count != 0 ? (files.Count == 1 && IsScreenshotFileName(files[0].Name) ? "Screenshot" : string.Join(", ", files.Select(file => file.Name)))
                : kind == ClipboardKind.Image ? "Image" : capture.Text.Trim();
            string preview = title.Length > 1024 ? title[..1024] : title;
            if (kind == ClipboardKind.Color && ClipboardClassifier.TryExtractHexColor(capture.Text, out string hex))
                preview = hex;
            // A failed copy may already have fed bytes into the streaming hash.
            // Replay only committed attachments, preserving the existing fingerprint format.
            if (failedCount > 0)
            {
                fileHash.GetHashAndReset();
                AppendCaptureFingerprint(fileHash, capture);
                foreach (var file in files)
                {
                    fileHash.AppendData(Encoding.UTF8.GetBytes(file.Name));
                    using var input = File.OpenRead(Path.Combine(staging, file.StoredName + (personal ? ".enc" : "")));
                    using var hashing = new ClipboardHashingStream(Stream.Null, fileHash);
                    if (personal) _vault.TransformFile(input, hashing, encrypt: false);
                    else input.CopyTo(hashing);
                }
            }
            string fingerprint = files.Count == 0 ? Fingerprint(capture)
                : Convert.ToHexString(fileHash.GetHashAndReset());
            // Deduplicate against unlocked Personal too, without publishing its contents.
            var duplicate = FindDuplicate(fingerprint, personal);
            if (duplicate == null && kind == ClipboardKind.Image && !personal)
            {
                duplicate = FindProbableDuplicateImage(staging, capture);
            }
            if (duplicate != null)
            {
                if (duplicate.IsPersonal && !IsPersonalUnlocked) return new ClipboardImportResult(null);
                var updated = duplicate with
                {
                    FileCount = files.Count,
                    PrimaryExtension = files.Count == 1 && !files[0].IsDirectory ? Path.GetExtension(files[0].Name).ToLowerInvariant() : "",
                    CopiedUtc = capture.CopiedUtc,
                    Kind = kind == ClipboardKind.Color ? kind : duplicate.Kind,
                    Preview = kind == ClipboardKind.Color ? preview : duplicate.Preview,
                    SourceApp = capture.SourceApp.Length == 0 ? duplicate.SourceApp : capture.SourceApp,
                    SourceExecutable = capture.SourceExecutable.Length == 0 ? duplicate.SourceExecutable : capture.SourceExecutable
                };
                WriteEntry(updated);
                PutEntry(updated);
                Changed?.Invoke();
                return new ClipboardImportResult(updated, ImportedCount: files.Count, FailedCount: failedCount);
            }
            var entry = new ClipboardEntry
            {
                Id = id,
                Kind = kind,
                Title = title.Length > 120 ? title[..120] : title,
                Preview = preview,
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
                if (!IsPersonalUnlocked) throw new InvalidOperationException("Personal is locked.");
            }
            WriteDocument(Path.Combine(staging, "content.json"), content, personal);
            WriteDocument(Path.Combine(staging, "entry.json"), entry, personal);
            Directory.Move(staging, EntryDirectory(entry));
            PutEntry(entry);
            _searchText.Set(entry.Id, content.Text);
            PruneCore(DateTime.UtcNow);
            Changed?.Invoke();
            return new ClipboardImportResult(entry, ImportedCount: files.Count, FailedCount: failedCount);
        }
        finally { TryDeleteDirectory(staging); }
    }

    public Task<IReadOnlyList<ClipboardEntry>> SearchAsync(string query, string category, CancellationToken cancellation = default) => Run<IReadOnlyList<ClipboardEntry>>(() =>
    {
        bool personal = category == "Personal";
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<ClipboardEntry>();
        foreach (var entry in _entries.Values.OrderByDescending(entry => entry.CopiedUtc))
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.IsPersonal != personal || personal && !IsPersonalUnlocked || !MatchesCategory(entry, category)) continue;
            if (tokens.Length == 0) { result.Add(entry); continue; }
            string searchable = entry.Title + " " + entry.Preview + " " + entry.SourceApp + " " + string.Join(' ', entry.Groups);
            string? fullText = null;
            bool matches = tokens.All(token =>
            {
                if (token.StartsWith('@')) return MatchesCategory(entry, token[1..], includeArchived: true);
                if (searchable.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
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
            });
            if (matches) result.Add(entry);
        }
        return result;
    });

    public static bool MatchesCategory(ClipboardEntry entry, string category, bool includeArchived = false) => category.ToLowerInvariant() switch
    {
        "all" or "" => !entry.IsPersonal && !entry.IsArchived,
        "archive" => entry.IsArchived && !entry.IsPersonal,
        "pin" or "pinned" => entry.IsPinned && !entry.IsPersonal,
        "personal" => entry.IsPersonal,
        "text" => (includeArchived || !entry.IsArchived) && entry.Kind == ClipboardKind.Text,
        "code" => (includeArchived || !entry.IsArchived) && entry.Kind == ClipboardKind.Code,
        "link" or "links" => (includeArchived || !entry.IsArchived) && entry.Kind == ClipboardKind.Link,
        "image" or "images" => (includeArchived || !entry.IsArchived) && entry.Kind == ClipboardKind.Image,
        "color" or "colors" => (includeArchived || !entry.IsArchived) && entry.Kind == ClipboardKind.Color,
        "file" or "files" => (includeArchived || !entry.IsArchived) && entry.Kind == ClipboardKind.File,
        _ => (includeArchived || !entry.IsArchived) && entry.Groups.Contains(category, StringComparer.OrdinalIgnoreCase)
    };

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
        EnsureDiskSpace(checked(entry.ByteSize * (content.Files.Any(file => file.IsDirectory) ? 2 : 1)));
        string export = Path.Combine(_root, "exports", entry.IsPersonal ? "personal" : "public", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(export);
        var paths = new List<string>();
        var personalExport = entry.IsPersonal ? new ClipboardPersonalExport() : null;
        try
        {
            foreach (var file in content.Files)
            {
                string destination = Path.Combine(export, Path.GetFileName(file.Name));
                // Files with the same display name retain distinct paths.
                if (File.Exists(destination) || Directory.Exists(destination))
                    destination = Path.Combine(export, paths.Count + "-" + Path.GetFileName(file.Name));
                string source = Path.Combine(EntryDirectory(entry), Path.GetFileName(file.StoredName) + (entry.IsPersonal ? ".enc" : ""));
                string materialized = file.IsDirectory ? destination + ".zip" : destination;
                if (personalExport != null)
                {
                    if (file.IsDirectory)
                    {
                        using var temporaryArchive = new ClipboardPersonalExport();
                        var stream = temporaryArchive.WriteFile(materialized, output =>
                        { using var input = File.OpenRead(source); _vault.TransformFile(input, output, false); });
                        ExtractPersonalDirectory(stream, destination, personalExport);
                    }
                    else personalExport.WriteFile(destination, output =>
                    { using var input = File.OpenRead(source); _vault.TransformFile(input, output, false); });
                }
                else File.Copy(source, materialized);
                if (file.IsDirectory && personalExport == null) { ZipFile.ExtractToDirectory(materialized, destination); File.Delete(materialized); }
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
                if (!IsPersonalUnlocked) throw new InvalidOperationException("Personal is locked.");
                _personalExports.Add(export, personalExport);
            }
            return paths.ToArray();
        }
        catch { personalExport?.Dispose(); TryDeleteDirectory(export); throw; }
    });

    private static void ExtractPersonalDirectory(Stream input, string destination, ClipboardPersonalExport lease)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            string path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid archive path.");
            if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                lease.WriteFile(path, output => { using var source = entry.Open(); source.CopyTo(output); });
            }
        }
    }

    public Task ReleasePersonalExportsAsync(IEnumerable<string> paths) => Run(() =>
    {
        var roots = paths.Select(Path.GetDirectoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string root in _personalExports.Keys.Where(roots.Contains).ToArray()) ReleasePersonalExport(root);
        return true;
    });

    private void ReleasePersonalExport(string root)
    {
        if (_personalExports.Remove(root, out var lease)) lease.Dispose();
        TryDeleteDirectory(root);
    }

    public Task UpdateAsync(Guid id, bool? pinned = null, string[]? groups = null, bool? archived = null) => Run(() =>
    {
        var entry = Find(id);
        entry = entry with
        {
            IsPinned = pinned ?? entry.IsPinned,
            Groups = groups?.Where(group => ClipboardClassifier.GroupNames.Contains(group, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? entry.Groups,
            IsArchived = archived ?? entry.IsArchived
        };
        WriteEntry(entry);
        PutEntry(entry);
        Changed?.Invoke();
        return true;
    });

    public Task MovePersonalAsync(Guid id, bool personal) => Run(() =>
    {
        if (!IsPersonalUnlocked) throw new InvalidOperationException("Unlock Personal first.");
        var entry = Find(id);
        if (entry.IsPersonal == personal) return true;
        var content = ReadContent(entry);
        var moved = entry with { IsPersonal = personal };
        EnsureDiskSpace(checked(entry.ByteSize + (entry.ByteSize / (1024 * 1024) + 1) * 28 + 64 * 1024));
        string staging = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
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
            string destination = EntryDirectory(moved);
            string previous = staging + ".previous";
            bool hadDestination = Directory.Exists(destination);
            if (hadDestination) Directory.Move(destination, previous);
            try { Directory.Move(staging, destination); }
            catch
            {
                if (hadDestination) Directory.Move(previous, destination);
                throw;
            }
            try { Directory.Delete(EntryDirectory(entry), true); }
            catch
            {
                // Keep the encrypted copy until rollback restores every missing source file.
                RestoreSourceAfterFailedMove(entry, moved, content);
                Directory.Delete(destination, true);
                if (hadDestination) Directory.Move(previous, destination);
                throw;
            }
            PutEntry(moved);
            if (hadDestination) TryDeleteDirectory(previous);
            Changed?.Invoke();
            return true;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    });

    private void RestoreSourceAfterFailedMove(ClipboardEntry original, ClipboardEntry moved, ClipboardContent content)
    {
        string directory = EntryDirectory(original);
        Directory.CreateDirectory(directory);
        foreach (string source in Directory.EnumerateFiles(EntryDirectory(moved)))
        {
            string name = Path.GetFileName(source);
            if (name.StartsWith("entry.", StringComparison.Ordinal) || name.StartsWith("content.", StringComparison.Ordinal)) continue;
            string destination = Path.Combine(directory, original.IsPersonal ? name + ".enc" : name[..^4]);
            if (File.Exists(destination)) continue;
            if (name is "image.png" or "image.png.enc")
            {
                byte[] bytes = File.ReadAllBytes(source);
                if (moved.IsPersonal) bytes = _vault.Open(bytes);
                try { File.WriteAllBytes(destination, original.IsPersonal ? _vault.Seal(bytes) : bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else _vault.TransformFile(source, destination, original.IsPersonal);
        }
        WriteDocument(Path.Combine(directory, "content.json"), content, original.IsPersonal);
        WriteDocument(Path.Combine(directory, "entry.json"), original, original.IsPersonal);
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
                var loaded = new List<ClipboardEntry>();
                foreach (string directory in Directory.EnumerateDirectories(Path.Combine(_root, "personal")))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
                    var entry = ReadDocument<ClipboardEntry>(Path.Combine(directory, "entry.json"), true);
                    if (entry.Id == id && entry.IsPersonal) loaded.Add(RestoreFileMetadata(entry));
                }
                foreach (var id in _entries.Where(pair => pair.Value.IsPersonal).Select(pair => pair.Key).ToArray()) RemoveEntry(id);
                foreach (var entry in loaded) PutEntry(entry);
                _personalUnlocked = true;
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
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        _vault.Reset();
        Directory.CreateDirectory(directory);
        Changed?.Invoke();
        return true;
    });

    private void LockPersonalCore()
    {
        _personalUnlocked = false;
        _vault.Lock();
        foreach (string root in _personalExports.Keys.ToArray()) ReleasePersonalExport(root);
        foreach (var id in _entries.Where(pair => pair.Value.IsPersonal).Select(pair => pair.Key).ToArray()) RemoveEntry(id);
        string exports = Path.Combine(_root, "exports", "personal");
        if (Directory.Exists(exports))
            TryDeleteDirectory(exports);
    }

    public Task DeleteAsync(Guid id) => Run(() =>
    {
        var entry = Find(id);
        Directory.Delete(EntryDirectory(entry), true);
        RemoveEntry(id);
        Changed?.Invoke();
        return true;
    });

    public Task PruneAsync(DateTime nowUtc) => Run(() => { PruneCore(nowUtc); DeleteExportsCore(); Changed?.Invoke(); return true; });
    private void PruneCore(DateTime nowUtc)
    {
        foreach (var entry in _entries.Values.Where(entry => !entry.IsPersonal && !entry.IsPinned && !entry.IsArchived && entry.CopiedUtc < nowUtc.AddDays(-30)).ToArray())
        {
            if (TryDeleteDirectory(EntryDirectory(entry))) RemoveEntry(entry.Id);
        }
    }
    private void DeleteExportsCore()
    {
        string root = Path.Combine(_root, "exports");
        if (!Directory.Exists(root)) return;
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
                TryDeleteDirectory(directory);
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
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
        if (entry.FileCount != 0 || entry.Kind != ClipboardKind.File) return entry;
        try
        {
            var files = ReadContent(entry).Files;
            return entry with
            {
                FileCount = files.Length,
                PrimaryExtension = files.Length == 1 && !files[0].IsDirectory
                    ? Path.GetExtension(files[0].Name).ToLowerInvariant() : ""
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            RuntimeLog.Warn("CLIPBOARD", "Could not restore file badge metadata.");
            return entry;
        }
    }

    private void PutEntry(ClipboardEntry entry)
    {
        if (_entries.TryGetValue(entry.Id, out var previous)) RemoveFingerprint(previous);
        _entries[entry.Id] = entry;
        var key = (entry.Fingerprint, entry.IsPersonal);
        if (!_fingerprints.TryGetValue(key, out var ids)) _fingerprints[key] = ids = new HashSet<Guid>();
        ids.Add(entry.Id);
    }

    private ClipboardEntry? FindDuplicate(string fingerprint, bool personal)
    {
        // A Personal import stays in the vault. Public recopy can refresh an
        // unlocked Personal entry without materializing an extra public copy.
        if (!personal && IsPersonalUnlocked && FindDuplicate(fingerprint, true) is { } privateEntry) return privateEntry;
        if (!_fingerprints.TryGetValue((fingerprint, personal), out var ids)) return null;
        return ids.Select(id => _entries.GetValueOrDefault(id)).Where(entry => entry != null)
            .OrderByDescending(entry => entry!.CopiedUtc).FirstOrDefault();
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
        if (_entries.TryRemove(id, out var entry)) RemoveFingerprint(entry);
    }

    private ClipboardEntry Find(Guid id) => _entries.TryGetValue(id, out var entry) && (!entry.IsPersonal || IsPersonalUnlocked)
        ? entry : throw new KeyNotFoundException("Clipboard item is unavailable.");
    private string EntryDirectory(ClipboardEntry entry) => Path.Combine(_root, entry.IsPersonal ? "personal" : "items", entry.Id.ToString("N"));
    private ClipboardContent ReadContent(ClipboardEntry entry) =>
        ReadDocument<ClipboardContent>(Path.Combine(EntryDirectory(entry), "content.json"), entry.IsPersonal) with { Entry = entry };
    private T ReadDocument<T>(string path, bool personal)
    {
        byte[] bytes = File.ReadAllBytes(path + (personal ? ".enc" : ""));
        if (personal) bytes = _vault.Open(bytes);
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new JsonException("Invalid clipboard record."); }
        finally { if (personal) CryptographicOperations.ZeroMemory(bytes); }
    }
    private void WriteDocument<T>(string path, T value, bool personal)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { ClipboardVault.AtomicWrite(path + (personal ? ".enc" : ""), personal ? _vault.Seal(bytes) : bytes); }
        finally { if (personal) CryptographicOperations.ZeroMemory(bytes); }
    }
    private void WriteEntry(ClipboardEntry entry) => WriteDocument(Path.Combine(EntryDirectory(entry), "entry.json"), entry, entry.IsPersonal);

    private long CopyDirectory(string source, Stream destination, ref int copiedItems)
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
                using var input = File.OpenRead(path);
                using var output = entry.Open();
                input.CopyTo(output);
                size = checked(size + input.Position);
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
            File.Delete(Path.Combine(directory, "image.png" + (personal ? ".enc" : "")));
            return false;
        }
        finally { if (decrypted != null) CryptographicOperations.ZeroMemory(decrypted.GetBuffer()); }
    }

    private static bool IsScreenshotFileName(string name) =>
        name.StartsWith('{') && name.EndsWith("}.png", StringComparison.OrdinalIgnoreCase);

    private ClipboardEntry? FindProbableDuplicateImage(string staging, ClipboardCapture capture)
    {
        if (capture.FilePaths.Length > 1) return null;
        string stagingImg = Path.Combine(staging, "image.png");
        if (!File.Exists(stagingImg)) return null;

        var recent = _entries.Values
            .Where(e => e.Kind == ClipboardKind.Image && !e.IsPersonal && Math.Abs((capture.CopiedUtc - e.CopiedUtc).TotalSeconds) < 2.0)
            .OrderByDescending(e => e.CopiedUtc)
            .FirstOrDefault();

        if (recent == null || ReadContent(recent).Files.Length > 1) return null;

        bool isScreenshotRelated = (capture.FilePaths.Length == 1 && IsScreenshotFileName(Path.GetFileName(capture.FilePaths[0])))
            || recent.Title == "Screenshot"
            || (recent.SourceApp.Length > 0 && recent.SourceApp == capture.SourceApp &&
                (recent.SourceApp.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                 recent.SourceApp.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
                 recent.SourceApp.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase)));

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
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Take(MaxImportContents + 1).Order(StringComparer.Ordinal))
            {
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
        try
        {
            using var input = new MemoryStream(image, writable: false);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var pixels = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],
                System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            hash.AppendData(BitConverter.GetBytes(pixels.PixelWidth));
            hash.AppendData(BitConverter.GetBytes(pixels.PixelHeight));
            var row = new byte[checked(pixels.PixelWidth * 4)];
            for (int y = 0; y < pixels.PixelHeight; y++)
            {
                pixels.CopyPixels(new System.Windows.Int32Rect(0, y, pixels.PixelWidth, 1), row, row.Length, 0);
                hash.AppendData(row);
            }
            return "image-v2:" + Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException
            or System.Runtime.InteropServices.COMException or InvalidOperationException or OverflowException)
        {
            // Decoding can fail after rows have been hashed; start with a fresh hash.
            return "image-raw:" + Convert.ToHexString(SHA256.HashData(image));
        }
    }

    private static void AppendCaptureFingerprint(IncrementalHash hash, ClipboardCapture capture)
    {
        foreach (string value in new[] { capture.Text, capture.Html, capture.Rtf })
        { byte[] bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
        if (capture.ImagePng != null) hash.AppendData(capture.ImagePng);
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
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && --retries >= 0)
            {
                Thread.Sleep(50 * (3 - retries));
            }
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int hr = ex.HResult & 0xFFFF;
        return hr is 32 or 33; // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            _personalUnlocked = false;
            var pending = _pending;
            return new ValueTask(_disposeTask = Task.Run(() => DisposeCoreAsync(pending)));
        }
    }

    private async Task DisposeCoreAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); } catch { /* Failed operations still release their resources. */ }
        LockPersonalCore();
        _searchText.Clear();
        _vault.Dispose();
    }
}
