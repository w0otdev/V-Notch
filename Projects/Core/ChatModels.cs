using System.Text.Json;

namespace VNotch.Services.Spotlight;

public sealed record SpotlightAiMessage(string Role, string Content)
{
    public JsonElement[]? GeminiParts { get; init; }
    public bool IsIncomplete { get; init; }
}

public sealed class SpotlightSavedChat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Draft { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<SpotlightAiMessage> Messages { get; set; } = new();
    public int MessageOffset { get; set; }
    public int TotalMessages { get; set; }
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

public sealed record ChatSummary(string Id, string Provider, string Model, string Title, string Preview, DateTime UpdatedUtc, int MessageCount);

public sealed record ChatRecoveryIssue(string ChatId, bool RecoveredFromBackup);

public sealed record ChatPage(IReadOnlyList<ChatSummary> Items, int TotalCount)
{
    public IReadOnlyList<ChatRecoveryIssue> RecoveryIssues { get; init; } = [];
}

public interface IChatRepository
{
    Task<ChatPage> ListAsync(int offset, int count, CancellationToken cancellation = default);
    Task<SpotlightSavedChat?> LoadAsync(string id, int before = int.MaxValue, int count = 32, CancellationToken cancellation = default);
    Task<ChatSummary?> LatestAsync(string provider, string model, CancellationToken cancellation = default);
    Task SaveChatAsync(SpotlightSavedChat chat, CancellationToken cancellation = default);
    Task DeleteAsync(string id, CancellationToken cancellation = default);
    Task FlushAsync();
}
