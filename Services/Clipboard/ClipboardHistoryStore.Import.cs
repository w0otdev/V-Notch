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
        if (!approved && size > ApprovalThreshold)
            return new ClipboardImportResult(null, true, size);
        if (size == 0 && capture.FilePaths.Length == 0) return new ClipboardImportResult(null);
        EnsureDiskSpace(checked(size * (personal ? 2 : 1) + (capture.ImagePng?.LongLength ?? 0) + measuredItems * 1024L));

        if (capture.SeparateFiles && capture.FilePaths.Length > 1)
            return ImportSeparateFilesBatch(capture, approved, progress);

        Guid id = Guid.NewGuid();
        string staging = Path.Combine(_root, "staging", id.ToString("N"));
        using var cleanupLease = AppFileCleanupService.Protect(staging);
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<ClipboardFile>(capture.FilePaths.Length);
            using var fileHash = NativeClipboardHash.Create();
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
                    using var attachmentHash = NativeClipboardHash.Create();
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
                    else WritePublicBlobFile(storedPath, s => WriteCopy(s));
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
            {
                string imagePath = Path.Combine(staging, "image.png" + (personal ? ".enc" : ""));
                if (personal) File.WriteAllBytes(imagePath, _vault.Seal(capture.ImagePng));
                else WritePublicBlobBytes(imagePath, capture.ImagePng);
            }
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
            string title = ResolveImportTitle(files, kind, capture.Text);
            bool sensitive = HasSensitiveFiles(files)
                || ClipboardSensitiveData.IsSensitive(capture.Text, capture.SourceApp, capture.SourceExecutable)
                || ClipboardSensitiveData.IsSensitive(capture.Html, capture.SourceApp, capture.SourceExecutable)
                || ClipboardSensitiveData.IsSensitive(capture.Rtf, capture.SourceApp, capture.SourceExecutable);
            string preview = title.Length > 1024 ? title[..1024] : title;
            if (kind == ClipboardKind.Color && ClipboardClassifier.TryExtractHexColor(capture.Text, out string hex))
                preview = hex;
            // A failed copy may already have fed bytes into the streaming hash.
            // Replay only committed attachments using the framed v2 format.
            if (failedCount > 0)
                RehashCommittedAttachments(fileHash, capture, files, staging, personal);
            // shortcut: legacy attachment hashes stay unchanged, rehash stored files if cross-version deduplication is required.
            string fingerprint = files.Count == 0 ? Fingerprint(capture)
                : FormatPrefixedHash("files-v2:", fileHash);
            var duplicate = FindDuplicate(fingerprint, personal);
            if (duplicate == null && kind == ClipboardKind.Image && !personal)
                duplicate = FindProbableDuplicateImage(staging, capture);
            if (duplicate != null)
                return CommitDuplicateImport(duplicate, capture, kind, sensitive, preview, files.Count, failedCount, batchChild);
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

    private ClipboardImportResult ImportSeparateFilesBatch(ClipboardCapture capture, bool approved, IProgress<double>? progress)
    {
        // Preflight of the whole drop already ran in ImportCore before splitting it,
        // preserving aggregate approval, disk-space and directory-content limits.
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

    private static string ResolveImportTitle(List<ClipboardFile> files, ClipboardKind kind, string text)
    {
        if (files.Count == 0)
            return kind == ClipboardKind.Image ? "Image" : text.Trim();
        if (files.Count == 1)
            return IsScreenshotFileName(files[0].Name) ? "Screenshot" : files[0].Name;

        int totalLen = (files.Count - 1) * 2;
        for (int i = 0; i < files.Count; i++)
            totalLen = checked(totalLen + files[i].Name.Length);

        return string.Create(totalLen, files, static (span, list) =>
        {
            int pos = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    span[pos++] = ',';
                    span[pos++] = ' ';
                }
                ReadOnlySpan<char> name = list[i].Name.AsSpan();
                name.CopyTo(span[pos..]);
                pos += name.Length;
            }
        });
    }

    private void RehashCommittedAttachments(
        NativeClipboardHash fileHash,
        ClipboardCapture capture,
        List<ClipboardFile> files,
        string staging,
        bool personal)
    {
        fileHash.GetHashAndReset();
        AppendCaptureFingerprint(fileHash, capture);
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            AppendUtf8String(fileHash, (file.IsDirectory ? "directory:" : "file:") + file.Name, includeLengthPrefix: true);
            string committedPath = Path.Combine(staging, file.StoredName + (personal ? ".enc" : ""));
            using var attachmentHash = NativeClipboardHash.Create();
            using var hashing = new ClipboardHashingStream(Stream.Null, attachmentHash);
            if (personal)
            {
                using var input = File.OpenRead(committedPath);
                _vault.TransformFile(input, hashing, encrypt: false);
            }
            else
            {
                using var input = OpenPublicBlobRead(committedPath);
                input.CopyTo(hashing);
            }
            fileHash.AppendData(attachmentHash.GetHashAndReset());
        }
    }

    private ClipboardImportResult CommitDuplicateImport(
        ClipboardEntry duplicate,
        ClipboardCapture capture,
        ClipboardKind kind,
        bool sensitive,
        string preview,
        int importedCount,
        int failedCount,
        bool batchChild)
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
        return new ClipboardImportResult(updated, ImportedCount: importedCount, FailedCount: failedCount);
    }

}
