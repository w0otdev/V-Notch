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
    public string Title => Messages.FirstOrDefault(m => m.Role == "user")?.Content.Split('\n')[0] is { } title
        ? title[..Math.Min(64, title.Length)] : "…";
}

internal sealed class SpotlightChatStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VNotch", "spotlight-chats.enc");
    private readonly object _gate = new();
    private bool _canSave = true;
    internal List<SpotlightSavedChat> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new();
            try { return JsonSerializer.Deserialize<List<SpotlightSavedChat>>(DataProtection.Unprotect(File.ReadAllText(_path))) ?? new(); }
            catch { _canSave = false; RuntimeLog.Warn("SPOTLIGHT-CHAT", "Saved chats could not be opened; preserving the original file."); return new(); }
        }
    }
    internal bool Save(List<SpotlightSavedChat> chats)
    {
        lock (_gate)
        {
            if (!_canSave) return false;
            try
            {
                string encrypted = DataProtection.Protect(JsonSerializer.Serialize(chats));
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", encrypted);
                File.Move(_path + ".tmp", _path, true);
                return true;
            }
            catch { RuntimeLog.Warn("SPOTLIGHT-CHAT", "Could not save chat history."); return false; }
        }
    }
}
