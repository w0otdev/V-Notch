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
    private static long CopyStreamBounded(Stream input, Stream destination, long maxAllowedBytes, bool approved)
    {
        if (!approved && input.CanSeek && input.Length > maxAllowedBytes)
            throw new ClipboardImportLimitException();
        return NativeClipboardStreamCopy.Copy(input, destination, approved ? long.MaxValue : maxAllowedBytes);
    }

    private long CopyDirectory(string source, Stream destination, ref int copiedItems, long maxAllowedBytes, bool approved)
    {
        long size = 0;
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var item in EnumerateDirectory(source))
        {
            CountImportItem(ref copiedItems);
            string path = item.Path;
            string name = Path.GetRelativePath(source, path).Replace('\\', '/');
            var attributes = ClipboardImportPaths.ValidateEntry(path);
            var entry = archive.CreateEntry(name + ((attributes & FileAttributes.Directory) != 0 ? "/" : ""), CompressionLevel.NoCompression);
            // Preserve source dates, including empty folders, instead of using the
            // import time (which would make identical folder copies hash differently).
            var modified = new DateTimeOffset(item.Modified);
            entry.LastWriteTime = modified.Year is >= 1980 and <= 2107 ? modified
                : new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                using var input = OpenReadFileWithRetry(path);
                long remaining = approved ? long.MaxValue : Math.Max(0L, maxAllowedBytes - size);
                if (!approved && input.CanSeek && input.Length > remaining)
                    throw new ClipboardImportLimitException();
                using var output = entry.Open();
                long written = CopyStreamBounded(input, output, remaining, approved);
                size = checked(size + written);
            }
        }
        return size;
    }

    private bool TryCreateImagePreview(string source, string directory, bool personal)
    {
        try
        {
            using var stream = personal ? _vault.OpenAttachmentRead(source) : OpenPublicBlobRead(source);
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
            try
            {
                encoder.Save(output);
                var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
                string previewPath = Path.Combine(directory, "image.png" + (personal ? ".enc" : ""));
                if (personal) File.WriteAllBytes(previewPath, _vault.Seal(bytes));
                else WritePublicBlobBytes(previewPath, bytes);
            }
            finally { CryptographicOperations.ZeroMemory(output.GetBuffer()); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            SafeFileDeletion.DeleteFile(Path.Combine(directory, "image.png" + (personal ? ".enc" : "")));
            return false;
        }
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
        long captureTicks = capture.CopiedUtc.Ticks;
        lock (_entriesLock)
        {
            foreach (var pair in _entries)
            {
                var e = pair.Value;
                _ = vn_clipboard_store_is_probable_duplicate_image_candidate(
                    (int)e.Kind,
                    e.IsPersonal ? (byte)1 : (byte)0,
                    captureTicks,
                    e.CopiedUtc.Ticks,
                    recent?.CopiedUtc.Ticks ?? 0,
                    recent != null ? (byte)1 : (byte)0,
                    out byte isBetter);
                if (isBetter != 0)
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
            string incoming = capture.ImagePng != null ? Fingerprint(capture) : FingerprintImageFile(stagingImg);
            string existing = recent.Fingerprint.StartsWith("image-v2:", StringComparison.Ordinal)
                ? recent.Fingerprint : FingerprintImageFile(existingImg);
            ReadOnlySpan<char> exSpan = existing.AsSpan();
            ReadOnlySpan<char> inSpan = incoming.AsSpan();
            if (vn_clipboard_store_are_signatures_equal(exSpan, exSpan.Length, inSpan, inSpan.Length, out byte eq) == 0 && eq != 0)
                return recent;
        }
        catch
        {
            // Ignore decoder errors
        }

        return null;
    }

    private IEnumerable<NativeClipboardDirectoryScan.Entry> EnumerateDirectory(string root)
    {
        using var scan = new NativeClipboardDirectoryScan(root, _root);
        for (int i = 0; i < scan.Count; ++i) yield return scan.Get(i);
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
        foreach (var child in EnumerateDirectory(path))
        {
            CountImportItem(ref measuredItems);
            if ((child.Attributes & FileAttributes.Directory) == 0)
                size = checked(size + child.Size);
        }
        return size;
    }
    private static string FormatPrefixedHash(ReadOnlySpan<char> prefix, NativeClipboardHash hash)
    {
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return FormatPrefixedHashBytes(prefix, digest);
    }

    private static string FormatPrefixedHashBytes(ReadOnlySpan<char> prefix, ReadOnlySpan<byte> digest)
    {
        int cap = prefix.Length + (digest.Length * 2);
        Span<char> buf = stackalloc char[cap];
        _ = vn_clipboard_store_format_prefixed_sha256_hex(prefix, prefix.Length, digest, digest.Length, buf, cap, out int written);
        return new string(buf[..written]);
    }

    private static string Fingerprint(ClipboardCapture capture)
    {
        if (capture.ImagePng != null) return capture.ImagePixelFingerprint ?? ImagePixelFingerprint(capture.ImagePng);
        using var hash = NativeClipboardHash.Create();
        AppendCaptureFingerprint(hash, capture);
        return FormatPrefixedHash(ReadOnlySpan<char>.Empty, hash);
    }

    private string FingerprintImageFile(string path)
    {
        byte[] bytes = ReadPublicBlobBytes(path);
        try
        {
            Span<byte> digest = stackalloc byte[32];
            if (!NativeClipboardHash.TryHashImage(bytes, null, digest)) throw new IOException("Could not decode clipboard image.");
            return FormatPrefixedHashBytes("image-v2:", digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool HasSensitiveFiles(IReadOnlyList<ClipboardFile> files)
    {
        for (int i = 0; i < files.Count; i++)
        {
            if (ClipboardSensitiveData.IsSensitiveFileName(files[i].Name))
                return true;
        }
        return false;
    }

}
