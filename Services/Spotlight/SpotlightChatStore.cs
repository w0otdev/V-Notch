using System.IO;
using System.Text.Json;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightSavedChat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Draft { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<SpotlightAiMessage> Messages { get; set; } = new();
    public string Title
    {
        get
        {
            if (Messages.FirstOrDefault(m => m.Role == "user")?.Content is not { } content) return "…";
            int lineEnd = content.IndexOfAny(['\r', '\n']);
            int length = Math.Min(64, lineEnd < 0 ? content.Length : lineEnd);
            if (length > 0 && char.IsHighSurrogate(content[length - 1])) length--;
            return content[..length];
        }
    }
}

internal sealed class SpotlightChatStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private bool _canSave = true;
    private readonly object _saveGate = new();
    private List<SpotlightSavedChat>? _pendingChats;
    private Task<bool> _saveTask = Task.FromResult(true);
    private bool _saving;
    internal SpotlightChatStore(string? path = null) => _path = path ?? Path.Combine(AppPaths.LocalAppDataRoot, "spotlight-chats.enc");

    internal List<SpotlightSavedChat> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) { _canSave = true; return new(); }
            try
            {
                var chats = JsonSerializer.Deserialize<List<SpotlightSavedChat>>(DataProtection.Unprotect(File.ReadAllText(_path)))
                    ?? throw new JsonException("Chat history must contain a list.");
                if (chats.Any(chat => chat == null || chat.Messages == null || chat.Messages.Contains(null!)))
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
            string tempPath = _path + ".tmp";
            try
            {
                string encrypted = DataProtection.Protect(JsonSerializer.Serialize(chats));
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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
        var snapshot = chats.Select(chat => new SpotlightSavedChat
        {
            Id = chat.Id,
            Provider = chat.Provider,
            Model = chat.Model,
            Draft = chat.Draft,
            UpdatedUtc = chat.UpdatedUtc,
            Messages = chat.Messages.ToList()
        }).ToList();
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

    internal Task FlushAsync() { lock (_saveGate) return _saveTask; }
}
