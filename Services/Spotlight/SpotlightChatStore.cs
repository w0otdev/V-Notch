using System.IO;
using System.Text.Json;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightChatStore : IChatRepository
{
    private readonly string _path;
    private readonly object _gate = new();
    private bool _canSave = true;
    private readonly object _saveGate = new();
    private List<SpotlightSavedChat>? _pendingChats;
    private Task<bool> _saveTask = Task.FromResult(true);
    private bool _saving;
    private readonly VNotch.Infrastructure.Windows.EncryptedChatRepository _repository;
    internal SpotlightChatStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.LocalAppDataRoot, "spotlight-chats.enc");
        _repository = new(_path + ".store", DataProtection.Protect, DataProtection.Unprotect, () =>
        {
            var chats = Load();
            if (!_canSave) throw new IOException("Saved chats are temporarily unavailable; migration was not committed.");
            return chats;
        });
    }

    public Task<ChatPage> ListAsync(int offset, int count, CancellationToken cancellation = default) => _repository.ListAsync(offset, count, cancellation);
    public Task<SpotlightSavedChat?> LoadAsync(string id, int before = int.MaxValue, int count = 32, CancellationToken cancellation = default) => _repository.LoadAsync(id, before, count, cancellation);
    public Task<ChatSummary?> LatestAsync(string provider, string model, CancellationToken cancellation = default) => _repository.LatestAsync(provider, model, cancellation);
    public Task SaveChatAsync(SpotlightSavedChat chat, CancellationToken cancellation = default) => _repository.SaveChatAsync(chat, cancellation);
    public Task DeleteAsync(string id, CancellationToken cancellation = default) => _repository.DeleteAsync(id, cancellation);

    internal List<SpotlightSavedChat> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) { _canSave = true; return new(); }
            try
            {
                var chats = JsonSerializer.Deserialize<List<SpotlightSavedChat>>(DataProtection.Unprotect(File.ReadAllText(_path)))
                    ?? throw new JsonException("Chat history must contain a list.");
                if (chats.Any(chat => chat == null || chat.Messages == null || chat.Messages.Any(m => m is null)))
                    throw new JsonException("Invalid chat history.");
                _canSave = true;
                return chats;
            }
            catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException)
            {
                // Keep the original encrypted bytes for recovery before allowing a new history.
                try
                {
                    File.Move(_path, _path + "." + Guid.NewGuid().ToString("N") + ".corrupt");
                    _canSave = true;
                    RuntimeLog.Warn("SPOTLIGHT-CHAT", "Unreadable saved chats were quarantined; a new history can be saved.");
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    _canSave = false;
                    RuntimeLog.Warn("SPOTLIGHT-CHAT", "Saved chats could not be quarantined; preserving the original file.");
                }
                return new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _canSave = false;
                RuntimeLog.Warn("SPOTLIGHT-CHAT", "Saved chats could not be read; preserving the original file until a retry succeeds.");
                return new();
            }
        }
    }
    internal bool Save(List<SpotlightSavedChat> chats)
    {
        lock (_gate)
        {
            if (!_canSave) return false;
            string tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                string encrypted = DataProtection.Protect(JsonSerializer.Serialize(chats));
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(tempPath, encrypted);
                File.Move(tempPath, _path, true);
                return true;
            }
            catch
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort cleanup of temporary chat file. */ }
                RuntimeLog.Warn("SPOTLIGHT-CHAT", "Could not save chat history.");
                return false;
            }
        }
    }

    internal Task<bool> SaveAsync(List<SpotlightSavedChat> chats)
    {
        var snapshot = new List<SpotlightSavedChat>(chats.Count);
        for (int i = 0; i < chats.Count; i++)
        {
            var chat = chats[i];
            snapshot.Add(new SpotlightSavedChat
            {
                Id = chat.Id,
                Provider = chat.Provider,
                Model = chat.Model,
                Draft = chat.Draft,
                UpdatedUtc = chat.UpdatedUtc,
                Messages = new List<SpotlightAiMessage>(chat.Messages)
            });
        }
        lock (_saveGate)
        {
            _pendingChats = snapshot;
            if (_saving) return _saveTask;
            _saving = true;
            _saveTask = Task.Run(() =>
            {
                bool result = true;
                while (true)
                {
                    List<SpotlightSavedChat> pending;
                    lock (_saveGate)
                    {
                        if (_pendingChats == null) { _saving = false; return result; }
                        pending = _pendingChats;
                        _pendingChats = null;
                    }
                    result = Save(pending);
                }
            });
            return _saveTask;
        }
    }

    public Task FlushAsync() { lock (_saveGate) return Task.WhenAll(_saveTask, _repository.FlushAsync()); }
}
