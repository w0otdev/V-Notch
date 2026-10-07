namespace VNotch.Models;

public enum ClipboardKind { Text, Code, Link, Image, Color, File }

public sealed record ClipboardEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ClipboardKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    public int FileCount { get; init; }
    public string PrimaryExtension { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string SourceApp { get; init; } = "";
    public string SourceExecutable { get; init; } = "";
    public string[] Groups { get; init; } = [];
    public DateTime CopiedUtc { get; init; } = DateTime.UtcNow;
    public long ByteSize { get; init; }
    public bool IsPinned { get; init; }
    public bool IsPersonal { get; init; }
    public bool IsArchived { get; init; }
}

public sealed record ClipboardFile(string Name, string StoredName, bool IsDirectory);

public sealed record ClipboardContent
{
    public ClipboardEntry Entry { get; init; } = new();
    public string Text { get; init; } = "";
    public string Html { get; init; } = "";
    public string Rtf { get; init; } = "";
    public ClipboardFile[] Files { get; init; } = [];
}

public sealed record ClipboardCapture
{
    public string? TargetCategory { get; init; }
    public DateTime CopiedUtc { get; init; } = DateTime.UtcNow;
    public string Text { get; init; } = "";
    public string Html { get; init; } = "";
    public string Rtf { get; init; } = "";
    public string SourceApp { get; init; } = "";
    public string SourceExecutable { get; init; } = "";
    public string[] FilePaths { get; init; } = [];
    public bool SeparateFiles { get; init; }
    public byte[]? ImagePng { get; init; }
}

public sealed record ClipboardImportResult(ClipboardEntry? Entry, bool NeedsApproval = false, long ByteSize = 0, int ImportedCount = 0, int FailedCount = 0)
{
    public IReadOnlyList<ClipboardEntry> Entries { get; init; } = [];
}

public enum ClipboardUnlockStatus { InvalidPin, Success, RateLimited }

public readonly record struct ClipboardUnlockResult(ClipboardUnlockStatus Status, DateTime RetryAfterUtc = default)
{
    public bool Succeeded => Status == ClipboardUnlockStatus.Success;
}
