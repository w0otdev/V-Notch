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
                    byte[] bytes = personal ? ReadPublicBlobBytes(source) : _vault.Open(File.ReadAllBytes(source));
                    try
                    {
                        if (personal) File.WriteAllBytes(Path.Combine(staging, name + ".enc"), _vault.Seal(bytes));
                        else WritePublicBlobBytes(Path.Combine(staging, name[..^4]), bytes);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                else if (personal)
                {
                    using var input = OpenPublicBlobRead(source);
                    string dest = Path.Combine(staging, name + ".enc");
                    using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    _vault.TransformFile(input, output, encrypt: true);
                    output.Flush(true);
                }
                else
                {
                    WritePublicBlobFile(Path.Combine(staging, name[..^4]), plainOut =>
                    {
                        using var encIn = File.OpenRead(source);
                        _vault.TransformFile(encIn, plainOut, encrypt: false);
                        return 0L;
                    });
                }
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
            ClipboardUnlockResult result;
            lock (_vaultReadLock) result = _vault.UnlockWithResult(snapshot, create);
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
        lock (_vaultReadLock) _vault.Reset();
        Directory.CreateDirectory(directory);
        Changed?.Invoke();
        return true;
    });

    private void LockPersonalCore()
    {
        _personalUnlocked = false;
        lock (_vaultReadLock)
        {
            Interlocked.Increment(ref _personalEpoch);
            _vault.Lock();
            _searchText.Clear();
        }
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

}
