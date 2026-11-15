using System.IO;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

public sealed partial class ClipboardHistoryStore
{
    public Task InitializeAsync() => Run(() =>
    {
        if (_initialized) return true;
        LoadLabels();
        LoadCategoryOrder();
        Directory.CreateDirectory(Path.Combine(_root, "items"));
        Directory.CreateDirectory(Path.Combine(_root, "personal"));
        LockPersonalCore();
        string staging = Path.Combine(_root, "staging");
        TryDeleteDirectory(staging);
        ReconcileInterruptedMoves();
        var directories = Directory.EnumerateDirectories(Path.Combine(_root, "items"))
            .Where(directory => Guid.TryParseExact(Path.GetFileName(directory), "N", out _));
        foreach (string[] batch in directories.Chunk(32))
            PublicRecords.ReadStartupBatch(batch, LoadStartupRecord);
        PruneCore(DateTime.UtcNow);
        DeleteExportsCore();
        _initialized = true;
        Changed?.Invoke();
        return true;
    });

    private void LoadStartupRecord(string directory, ReadOnlySpan<byte> json, bool needsFallback, bool migrationDeferred)
    {
        if (migrationDeferred)
            RuntimeLog.Warn("CLIPBOARD", "Clipboard migration was deferred; existing records were preserved.");
        try
        {
            var entry = needsFallback
                ? ReadDocument<ClipboardEntry>(Path.Combine(directory, "entry.json"), false)
                : JsonSerializer.Deserialize<ClipboardEntry>(json);
            if (entry != null && Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) &&
                entry.Id == id && !entry.IsPersonal)
                PutEntry(RestoreFileMetadata(entry), persistExpiryMarker: false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { RuntimeLog.Warn("CLIPBOARD", "An unreadable clipboard item was preserved."); }
    }
}
