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
        var selectedIds = DeduplicateIds(ids);
        return Run(() =>
        {
            bool changed = false;
            try
            {
                for (int i = 0; i < selectedIds.Length; i++)
                {
                    var id = selectedIds[i];
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
        long cutoffTicks = nowUtc.AddDays(-30).Ticks;
        int maxEntries = Math.Max(0, MaxUnpinnedEntries);
        long maxBytes = Math.Max(0L, MaxUnpinnedBytes);

        List<ClipboardEntry> snapshot;
        lock (_entriesLock)
        {
            snapshot = new List<ClipboardEntry>(_entries.Count);
            foreach (var pair in _entries)
                snapshot.Add(pair.Value);
        }
        int count = snapshot.Count;
        if (count == 0) return;

        VnClipboardPruneCandidate[]? rentedCandidates = count > 256 ? ArrayPool<VnClipboardPruneCandidate>.Shared.Rent(count) : null;
        int[]? rentedIndices = count > 256 ? ArrayPool<int>.Shared.Rent(count) : null;
        Span<VnClipboardPruneCandidate> candidates = rentedCandidates != null
            ? rentedCandidates.AsSpan(0, count)
            : stackalloc VnClipboardPruneCandidate[count];
        Span<int> orderedIndices = rentedIndices != null
            ? rentedIndices.AsSpan(0, count)
            : stackalloc int[count];

        try
        {
            for (int i = 0; i < count; i++)
            {
                var e = snapshot[i];
                candidates[i] = new VnClipboardPruneCandidate
                {
                    EntryIndex = i,
                    IsPersonal = e.IsPersonal ? (byte)1 : (byte)0,
                    IsPinned = e.IsPinned ? (byte)1 : (byte)0,
                    IsArchived = e.IsArchived ? (byte)1 : (byte)0,
                    CopiedUtcTicks = e.CopiedUtc.Ticks,
                    ByteSize = e.ByteSize
                };
            }

            _ = vn_clipboard_store_plan_prune(
                candidates,
                count,
                cutoffTicks,
                maxEntries,
                maxBytes,
                orderedIndices,
                count,
                out int expiredCount,
                out int evictCandidateCount,
                out long totalBytes);

            int remainingCount = evictCandidateCount;
            for (int i = 0; i < expiredCount; i++)
            {
                var entry = snapshot[orderedIndices[i]];
                if (TryDeleteDirectory(EntryDirectory(entry)))
                {
                    RemoveEntry(entry.Id);
                }
                else
                {
                    remainingCount++;
                    totalBytes = checked(totalBytes + Math.Max(0L, entry.ByteSize));
                }
            }

            if (remainingCount > maxEntries || totalBytes > maxBytes)
            {
                int evictEnd = expiredCount + evictCandidateCount;
                for (int i = expiredCount; i < evictEnd && (remainingCount > maxEntries || totalBytes > maxBytes); i++)
                {
                    var entry = snapshot[orderedIndices[i]];
                    if (TryDeleteDirectory(EntryDirectory(entry)))
                    {
                        RemoveEntry(entry.Id);
                        remainingCount--;
                        totalBytes -= Math.Max(0L, entry.ByteSize);
                    }
                }
            }
        }
        finally
        {
            if (rentedCandidates != null) ArrayPool<VnClipboardPruneCandidate>.Shared.Return(rentedCandidates);
            if (rentedIndices != null) ArrayPool<int>.Shared.Return(rentedIndices);
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
                    existing = ReadDocument<ClipboardEntry>(Path.Combine(publicDir, "entry.json"), false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            bool hasPersonalDir = Directory.Exists(personalDir);
            bool ticksValid = false;
            long ticks = 0;
            if (existing == null || (!existing.IsPinned && !existing.IsArchived && !existing.IsPersonal && !hasPersonalDir))
            {
                try { ticksValid = long.TryParse(File.ReadAllText(marker), out ticks); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _ = vn_clipboard_store_evaluate_sensitive_expiry(
                existing != null ? (byte)1 : (byte)0,
                existing?.IsPinned == true ? (byte)1 : (byte)0,
                existing?.IsArchived == true ? (byte)1 : (byte)0,
                existing?.IsPersonal == true ? (byte)1 : (byte)0,
                hasPersonalDir ? (byte)1 : (byte)0,
                ticksValid ? (byte)1 : (byte)0,
                ticks,
                expiryTicks,
                out int action);
            if (action == 1)
            {
                try { SafeFileDeletion.DeleteFile(marker); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                continue;
            }
            if (action == 0) continue;
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
        DateTime cutoff = DateTime.UtcNow.AddHours(-1);
        ReadOnlySpan<string> categories = ["public", "personal"];
        for (int c = 0; c < categories.Length; c++)
        {
            string categoryPath = Path.Combine(root, categories[c]);
            if (!Directory.Exists(categoryPath)) continue;
            SafeFileDeletion.ValidatePath(categoryPath);
            string[] subDirs = Directory.GetDirectories(categoryPath);
            for (int i = 0; i < subDirs.Length; i++)
            {
                string directory = subDirs[i];
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                if (IsExportDirectoryProtected(directory)) continue;
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    TryDeleteDirectory(directory);
            }
        }
    }

    private bool IsExportDirectoryProtected(string directory)
    {
        ReadOnlySpan<char> dirSpan = directory.AsSpan();
        ushort sep = Path.DirectorySeparatorChar;
        foreach (string active in _exportLeases.Keys)
        {
            ReadOnlySpan<char> activeSpan = active.AsSpan();
            _ = vn_clipboard_store_is_export_path_protected(
                dirSpan, dirSpan.Length, activeSpan, activeSpan.Length, sep, out byte isProtected);
            if (isProtected != 0)
                return true;
        }
        return false;
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

}
