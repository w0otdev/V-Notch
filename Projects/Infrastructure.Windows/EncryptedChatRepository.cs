using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Services.Spotlight;

namespace VNotch.Infrastructure.Windows;

/// <summary>Encrypted, paged chat storage. A manifest rename commits each conversation atomically.</summary>
public sealed class EncryptedChatRepository : IChatRepository
{
    public const int ChunkSize = 32;
    private readonly string _root;
    private readonly Func<string, string> _protect;
    private readonly Func<string, string> _unprotect;
    private readonly Func<IReadOnlyList<SpotlightSavedChat>> _loadLegacy;
    private readonly object _queueGate = new();
    private Task _pending = Task.CompletedTask;
    private readonly Dictionary<string, Manifest> _chats = new(StringComparer.Ordinal);
    private ChatSummary[]? _ordered;
    private bool _initialized;
    private bool _catalogDirty;
    private readonly Dictionary<string, ChatRecoveryIssue> _recoveryIssues = new(StringComparer.Ordinal);

    private sealed record Chunk(string File, string Hash, int Count);
    private sealed record Manifest(ChatSummary Summary, string Draft, List<Chunk> Chunks);

    public EncryptedChatRepository(string root, Func<string, string> protect, Func<string, string> unprotect,
        Func<IReadOnlyList<SpotlightSavedChat>> loadLegacy)
    {
        _root = Path.GetFullPath(root);
        _protect = protect;
        _unprotect = unprotect;
        _loadLegacy = loadLegacy;
    }

    private Task<T> Run<T>(Func<T> action, CancellationToken cancellation = default)
    {
        lock (_queueGate)
        {
            // Callers submit only bounded pages or a single conversation tail.
            var task = _pending.ContinueWith(_ =>
            {
                cancellation.ThrowIfCancellationRequested();
                Initialize();
                return action();
            }, cancellation, TaskContinuationOptions.LazyCancellation, TaskScheduler.Default);
            _pending = task;
            return task;
        }
    }

    private void Initialize()
    {
        if (_initialized) return;
        Directory.CreateDirectory(_root);
        _chats.Clear();
        _recoveryIssues.Clear();
        if (File.Exists(Path.Combine(_root, "ready")) && !File.Exists(Path.Combine(_root, "catalog.dirty")) &&
            File.Exists(Path.Combine(_root, "catalog.enc")))
        {
            try
            {
                var catalog = Read<Manifest[]>(Path.Combine(_root, "catalog.enc"));
                foreach (var manifest in catalog)
                {
                    if (manifest?.Summary == null) throw new JsonException("Invalid chat catalog.");
                    ValidateId(manifest.Summary.Id);
                    Validate(manifest, manifest.Summary.Id);
                    if (!_chats.TryAdd(manifest.Summary.Id, manifest)) throw new JsonException("Duplicate chat catalog entry.");
                }
                _initialized = true;
                return;
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException or ArgumentException)
            {
                _chats.Clear(); // Per-conversation manifests remain the source of truth.
            }
        }
        InvalidateCatalog();
        if (!File.Exists(Path.Combine(_root, "ready")))
        {
            // The encrypted legacy file remains untouched. A failed migration is safe to retry.
            foreach (var chat in _loadLegacy()) SaveCore(chat);
            WriteAtomic(Path.Combine(_root, "ready"), _protect("1"));
        }
        foreach (string directory in Directory.EnumerateDirectories(_root))
        {
            string id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            string path = Path.Combine(directory, "index.enc");
            if (!File.Exists(path)) continue; // An interrupted write never became a visible conversation.
            Manifest manifest;
            try
            {
                manifest = Read<Manifest>(path);
                Validate(manifest, id);
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException)
            {
                // Never treat transient I/O errors as corruption or overwrite the last good backup.
                string backup = path + ".bak";
                try
                {
                    manifest = Read<Manifest>(backup);
                    Validate(manifest, id);
                    foreach (var chunk in manifest.Chunks) ReadChunk(id, chunk);
                }
                catch (Exception backupError) when (backupError is JsonException or CryptographicException or FileNotFoundException or DirectoryNotFoundException)
                {
                    // Preserve both generations in place. One damaged conversation must not
                    // hide healthy chats, and must never silently become an empty conversation.
                    _recoveryIssues[id] = new(id, RecoveredFromBackup: false);
                    continue;
                }
                File.Copy(path, path + "." + Guid.NewGuid().ToString("N") + ".corrupt");
                WriteAtomic(path, File.ReadAllText(backup));
                _recoveryIssues[id] = new(id, RecoveredFromBackup: true);
            }
            _chats[id] = manifest;
        }
        _ordered = null;
        _initialized = true;
    }

    public Task<ChatPage> ListAsync(int offset, int count, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        return Run(() =>
        {
            _ordered ??= _chats.Values.Select(c => c.Summary).OrderByDescending(c => c.UpdatedUtc)
                .ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
            return new ChatPage(_ordered.Skip(offset).Take(count).ToArray(), _ordered.Length)
            {
                RecoveryIssues = _recoveryIssues.Values.ToArray()
            };
        }, cancellation);
    }

    public Task<ChatSummary?> LatestAsync(string provider, string model, CancellationToken cancellation = default) =>
        Run(() => _chats.Values.Select(c => c.Summary).Where(c => c.Provider == provider && c.Model == model && c.MessageCount > 0)
            .MaxBy(c => c.UpdatedUtc), cancellation);

    public Task<SpotlightSavedChat?> LoadAsync(string id, int before = int.MaxValue, int count = ChunkSize,
        CancellationToken cancellation = default)
    {
        ValidateId(id);
        if (count is < 1 or > 100 || before < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return Run<SpotlightSavedChat?>(() =>
        {
            if (!_chats.TryGetValue(id, out var manifest))
            {
                if (_recoveryIssues.ContainsKey(id))
                    throw new InvalidDataException("This conversation is damaged; its files have been preserved for recovery.");
                return null;
            }
            int end = Math.Min(before, manifest.Summary.MessageCount);
            int start = Math.Max(0, end - count);
            var messages = new List<SpotlightAiMessage>(end - start);
            for (int i = start / ChunkSize; i * ChunkSize < end; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var chunk = manifest.Chunks[i];
                var batch = ReadChunk(id, chunk);
                messages.AddRange(batch.Skip(Math.Max(0, start - i * ChunkSize)).Take(end - Math.Max(start, i * ChunkSize)));
            }
            return new SpotlightSavedChat
            {
                Id = id, Provider = manifest.Summary.Provider, Model = manifest.Summary.Model, Draft = manifest.Draft,
                UpdatedUtc = manifest.Summary.UpdatedUtc, Messages = messages, MessageOffset = start,
                TotalMessages = manifest.Summary.MessageCount
            };
        }, cancellation);
    }

    public Task SaveChatAsync(SpotlightSavedChat chat, CancellationToken cancellation = default)
    {
        ValidateId(chat.Id);
        ArgumentNullException.ThrowIfNull(chat.Messages);
        if (chat.Provider == null || chat.Model == null || chat.Draft == null ||
            chat.Messages.Any(m => m == null || m.Content == null || m.Role == null))
            throw new ArgumentException("Invalid conversation.", nameof(chat));
        // Own the mutable list before leaving the caller's thread.
        var snapshot = new SpotlightSavedChat
        {
            Id = chat.Id, Provider = chat.Provider, Model = chat.Model, Draft = chat.Draft, UpdatedUtc = chat.UpdatedUtc,
            MessageOffset = chat.MessageOffset, Messages = chat.Messages.Select(m => m with
            { GeminiParts = m.GeminiParts?.Select(p => p.Clone()).ToArray() }).ToList()
        };
        return Run(() => { SaveCore(snapshot); return true; }, cancellation);
    }

    private void SaveCore(SpotlightSavedChat chat)
    {
        ValidateId(chat.Id);
        if (_recoveryIssues.TryGetValue(chat.Id, out var issue) && !issue.RecoveredFromBackup)
            throw new InvalidDataException("Cannot overwrite a damaged conversation. Save as a new conversation or recover its files first.");
        _chats.TryGetValue(chat.Id, out var previous);
        if (chat.MessageOffset < 0 || chat.MessageOffset > (previous?.Summary.MessageCount ?? 0))
            throw new InvalidOperationException("The chat tail does not follow the stored conversation.");
        string directory = Path.Combine(_root, chat.Id);
        InvalidateCatalog();
        Directory.CreateDirectory(directory);
        var chunks = previous?.Chunks.Take(chat.MessageOffset / ChunkSize).ToList() ?? [];
        var tail = new List<SpotlightAiMessage>();
        if (chat.MessageOffset % ChunkSize != 0)
        {
            var old = previous!.Chunks[chunks.Count];
            tail.AddRange(ReadChunk(chat.Id, old).Take(chat.MessageOffset % ChunkSize));
        }
        tail.AddRange(chat.Messages);
        for (int i = 0; i < tail.Count; i += ChunkSize)
        {
            var messages = tail.Skip(i).Take(ChunkSize).ToArray();
            string json = JsonSerializer.Serialize(messages);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            string hash;
            try { hash = Convert.ToHexString(SHA256.HashData(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            var old = previous?.Chunks.ElementAtOrDefault(chunks.Count);
            if (old?.Hash == hash) { chunks.Add(old); continue; }
            string file = Guid.NewGuid().ToString("N") + ".enc";
            WriteAtomic(Path.Combine(directory, file), _protect(json));
            chunks.Add(new Chunk(file, hash, messages.Length));
        }
        int total = checked(chat.MessageOffset + chat.Messages.Count);
        string preview = chat.Messages.LastOrDefault()?.Content ?? previous?.Summary.Preview ?? "";
        preview = preview[..Math.Min(160, preview.Length)].ReplaceLineEndings(" ");
        var summary = new ChatSummary(chat.Id, chat.Provider, chat.Model,
            chat.MessageOffset == 0 ? chat.Title : previous!.Summary.Title, preview, chat.UpdatedUtc, total);
        var manifest = new Manifest(summary, chat.Draft, chunks);
        string index = Path.Combine(directory, "index.enc");
        WriteAtomic(index, _protect(JsonSerializer.Serialize(manifest)), keepBackup: true);
        _chats[chat.Id] = manifest;
        _ordered = null;
        // Keep the previous manifest and its chunks for recovery; remove only unreferenced generations.
        var retained = chunks.Select(c => c.File).ToHashSet(StringComparer.Ordinal);
        if (previous != null) retained.UnionWith(previous.Chunks.Select(c => c.File));
        foreach (string file in Directory.EnumerateFiles(directory, "*.enc"))
        {
            if (Path.GetFileName(file) == "index.enc" || retained.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public Task DeleteAsync(string id, CancellationToken cancellation = default)
    {
        ValidateId(id);
        return Run(() =>
        {
            InvalidateCatalog();
            string directory = Path.Combine(_root, id);
            if (Directory.Exists(directory))
            {
                string tombstone = directory + "." + Guid.NewGuid().ToString("N") + ".deleted";
                Directory.Move(directory, tombstone);
                // The rename commits deletion. Cleanup failure must not resurrect a conversation.
                try { Directory.Delete(tombstone, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _chats.Remove(id);
            _recoveryIssues.Remove(id);
            _ordered = null;
            return true;
        }, cancellation);
    }

    public Task FlushAsync() => Run(() =>
    {
        if (!_catalogDirty) return true;
        // A partial catalog must not hide damaged records on the next launch.
        if (_recoveryIssues.Values.Any(issue => !issue.RecoveredFromBackup)) return true;
        WriteAtomic(Path.Combine(_root, "catalog.enc"), _protect(JsonSerializer.Serialize(_chats.Values)), keepBackup: true);
        File.Delete(Path.Combine(_root, "catalog.dirty"));
        _catalogDirty = false;
        return true;
    });

    private void InvalidateCatalog()
    {
        if (_catalogDirty) return;
        // Commit this marker before touching a manifest so interrupted writes force a safe rebuild.
        WriteAtomic(Path.Combine(_root, "catalog.dirty"), "1");
        _catalogDirty = true;
    }

    private T Read<T>(string path) => JsonSerializer.Deserialize<T>(_unprotect(File.ReadAllText(path)))
        ?? throw new JsonException("Empty encrypted chat record.");

    private SpotlightAiMessage[] ReadChunk(string id, Chunk chunk)
    {
        string json = _unprotect(File.ReadAllText(Path.Combine(_root, id, chunk.File)));
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            if (Convert.ToHexString(SHA256.HashData(bytes)) != chunk.Hash)
                throw new JsonException("Chat chunk does not match its committed manifest.");
            var batch = JsonSerializer.Deserialize<SpotlightAiMessage[]>(bytes);
            if (batch == null || batch.Length != chunk.Count || batch.Any(m => m == null || m.Content == null || m.Role == null))
                throw new JsonException("Invalid chat messages.");
            return batch;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid conversation identifier.", nameof(id));
    }

    private static void Validate(Manifest manifest, string id)
    {
        if (manifest.Summary == null || manifest.Summary.Id != id || manifest.Chunks == null || manifest.Draft == null ||
            manifest.Summary.Provider == null || manifest.Summary.Model == null || manifest.Summary.Title == null ||
            manifest.Summary.Preview == null || manifest.Summary.MessageCount < 0 ||
            manifest.Chunks.Any(c => c == null || c.Count is < 1 or > ChunkSize ||
                c.Hash == null || c.Hash.Length != 64 || c.File == null ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(c.File), "N", out _) || c.File != Path.GetFileName(c.File) ||
                Path.GetExtension(c.File) != ".enc") ||
            manifest.Chunks.Sum(c => (long)c.Count) != manifest.Summary.MessageCount ||
            manifest.Chunks.Take(Math.Max(0, manifest.Chunks.Count - 1)).Any(c => c.Count != ChunkSize))
            throw new JsonException("Invalid conversation manifest.");
    }

    private static void WriteAtomic(string path, string encrypted, bool keepBackup = false)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(encrypted);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (keepBackup && File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
