using System.IO;
using System.Text.Json;

namespace VNotch.Services.Clipboard;

public sealed partial class ClipboardHistoryStore
{
    private string[] _categoryOrder = [];

    /// <summary>The visible tag order, reconciled with the current label catalog.</summary>
    public IReadOnlyList<string> GetCategoryOrder()
    {
        var labels = Volatile.Read(ref _labels);
        var defaults = new List<string>
        {
            "All", "Pin", "Links", "Text", "Code", "Images", "Colors", "Files"
        };
        foreach (var label in labels)
        {
            if (label.Id == "Important") defaults.Add("Personal");
            defaults.Add(label.Id);
        }
        if (!defaults.Contains("Personal")) defaults.Add("Personal");
        defaults.Add("Archive");

        var remaining = defaults.ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
        var order = new List<string>(defaults.Count);
        foreach (string? id in Volatile.Read(ref _categoryOrder))
            if (id != null && remaining.Remove(id, out string? canonical)) order.Add(canonical);
        foreach (string id in defaults)
            if (remaining.Remove(id)) order.Add(id);
        return order.ToArray();
    }

    private void LoadCategoryOrder()
    {
        string path = Path.Combine(_root, "category-order.json");
        if (!File.Exists(path)) return;
        try
        {
            Volatile.Write(ref _categoryOrder, JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(path)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { RuntimeLog.Warn("CLIPBOARD", "Unreadable clipboard tag order was preserved."); }
    }

    /// <summary>Moves a tag before another tag, or to the end when beforeId is null.</summary>
    public Task ReorderCategoryAsync(string id, string? beforeId) => Run(() =>
    {
        var current = GetCategoryOrder();
        var order = current.ToList();
        int source = order.IndexOf(id);
        if (source < 0 || beforeId == id || beforeId != null && !order.Contains(beforeId)) return false;
        order.RemoveAt(source);
        int target = beforeId == null ? order.Count : order.IndexOf(beforeId);
        order.Insert(target, id);
        if (order.SequenceEqual(current)) return false;
        var saved = order.ToArray();
        SaveCategoryOrder(saved);
        return true;
    });

    private void SaveCategoryOrder(string[] saved)
    {
        Directory.CreateDirectory(_root);
        ClipboardVault.AtomicWrite(Path.Combine(_root, "category-order.json"), JsonSerializer.SerializeToUtf8Bytes(saved));
        Volatile.Write(ref _categoryOrder, saved);
        Changed?.Invoke();
    }
}
