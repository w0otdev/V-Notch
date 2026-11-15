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
                    using var input = OpenPublicBlobRead(source);
                    ExtractArchiveDirectory(input, destination, null);
                }
                else CopyPublicBlobToPlainFile(source, materialized);
                paths.Add(destination);
            }
            if (entry.Kind == ClipboardKind.Image && content.Files.Length == 0)
            {
                string destination = Path.Combine(export, "Image.png");
                string imagePath = Path.Combine(EntryDirectory(entry), "image.png" + (entry.IsPersonal ? ".enc" : ""));
                if (personalExport != null) personalExport.WriteFile(destination, output => _vault.CopyImage(imagePath, output));
                else CopyPublicBlobToPlainFile(imagePath, destination);
                paths.Add(destination);
            }
            if (personalExport != null)
            {
                EnsurePersonalUnlocked();
                _personalExports.Add(export, personalExport);
            }
            _exportLeases.Add(export, AppFileCleanupService.Protect(export));
            _exportEntries[export] = entry.Id;
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
            ReadOnlySpan<char> trimmedEntryName = entry.FullName.AsSpan().TrimEnd("/\\");
            if (trimmedEntryName.IsEmpty || trimmedEntryName[^1] is '.' or ' ')
                throw new IOException("Invalid archive path or extraction quota exceeded.");
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
                    totalBytes = checked(totalBytes + NativeClipboardStreamCopy.Copy(source, output, maxTotalBytes - totalBytes, archiveExtraction: true));
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
            if (_personalExports.Count > 0)
            {
                var allPersonal = new string[_personalExports.Count];
                _personalExports.Keys.CopyTo(allPersonal, 0);
                for (int i = 0; i < allPersonal.Length; i++)
                    ReleasePersonalExport(allPersonal[i]);
            }
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
        foreach (string key in _exportLeases.Keys)
        {
            if (!_personalExports.ContainsKey(key) && roots.Contains(key))
                toRelease.Add(key);
        }
        for (int i = 0; i < toRelease.Count; i++)
            ReleasePersonalExport(toRelease[i]);
        return true;
    });

    private void ReleasePersonalExport(string root)
    {
        _exportEntries.Remove(root);
        bool personal = _personalExports.Remove(root, out var lease);
        lease?.Dispose();
        if (_exportLeases.Remove(root, out var cleanupLease)) cleanupLease.Dispose();
        if (personal) TryDeleteDirectory(root);
    }

    private void ReleaseEntryExports(Guid id)
    {
        List<string>? toRelease = null;
        foreach (var pair in _exportEntries)
        {
            if (pair.Value == id)
            {
                toRelease ??= new List<string>();
                toRelease.Add(pair.Key);
            }
        }
        if (toRelease == null) return;
        for (int i = 0; i < toRelease.Count; i++)
        {
            string root = toRelease[i];
            _exportEntries.Remove(root);
            if (_personalExports.Remove(root, out var lease)) lease.Dispose();
            if (_exportLeases.Remove(root, out var cleanupLease)) cleanupLease.Dispose();
            TryDeleteDirectory(root);
        }
    }

}
