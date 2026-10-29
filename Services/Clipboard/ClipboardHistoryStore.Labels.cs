using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VNotch.Services.Clipboard;

public sealed record ClipboardLabel(string Id, string Name)
{
    [JsonIgnore] public bool IsCustom => IsCustomId(Id);
    public static bool IsCustomId(string id) => id.StartsWith("label:", StringComparison.Ordinal)
        && Guid.TryParseExact(id.AsSpan(6), "N", out _);
}

public sealed partial class ClipboardHistoryStore
{
    private ClipboardLabel[] _labels = DefaultLabels();
    private static ClipboardLabel[] DefaultLabels() => ClipboardClassifier.GroupNames.Select(name => new ClipboardLabel(name, name)).ToArray();

    /// <summary>Ordered labels. Labels have no item-count limit and custom IDs never collide with type tabs.</summary>
    public IReadOnlyList<ClipboardLabel> GetLabels() => Volatile.Read(ref _labels).ToArray();
    public bool IsLabel(string id) => Volatile.Read(ref _labels).Any(label => string.Equals(label.Id, id, StringComparison.OrdinalIgnoreCase));

    private void LoadLabels()
    {
        string path = Path.Combine(_root, "labels.json");
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<ClipboardLabel[]>(File.ReadAllBytes(path)) ?? [];
            var labels = new List<ClipboardLabel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in saved)
            {
                if (label == null || string.IsNullOrWhiteSpace(label.Id) || string.IsNullOrWhiteSpace(label.Name)) continue;
                if ((ClipboardClassifier.GroupNames.Contains(label.Id) || ClipboardLabel.IsCustomId(label.Id)) && seen.Add(label.Id))
                    labels.Add(label with { Name = label.Name.Trim() });
            }
            foreach (var label in DefaultLabels())
                if (seen.Add(label.Id)) labels.Add(label);
            Volatile.Write(ref _labels, labels.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { RuntimeLog.Warn("CLIPBOARD", "Unreadable clipboard labels were preserved."); }
    }

    private void SaveLabels(ClipboardLabel[] labels)
    {
        Directory.CreateDirectory(_root);
        ClipboardVault.AtomicWrite(Path.Combine(_root, "labels.json"), JsonSerializer.SerializeToUtf8Bytes(labels));
        Volatile.Write(ref _labels, labels);
        Changed?.Invoke();
    }

    public Task<ClipboardLabel> AddLabelAsync(string name) => Run(() => AddLabelCore(name));

    private ClipboardLabel AddLabelCore(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 64 || name.Any(char.IsControl)) throw new ArgumentException("A label must contain 1–64 characters.", nameof(name));
        if (_labels.Any(label => string.Equals(label.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A label with this name already exists.", nameof(name));
        var label = new ClipboardLabel("label:" + Guid.NewGuid().ToString("N"), name);
        SaveLabels([.. _labels, label]);
        return label;
    }

    public Task MoveLabelAsync(string id, int offset) => Run(() =>
    {
        var labels = _labels.ToList();
        int oldIndex = labels.FindIndex(label => label.Id == id);
        if (oldIndex < 0) return false;
        int newIndex = (int)Math.Clamp((long)oldIndex + offset, 0, labels.Count - 1);
        if (oldIndex == newIndex) return false;
        var label = labels[oldIndex];
        labels.RemoveAt(oldIndex);
        labels.Insert(newIndex, label);
        SaveLabels(labels.ToArray());
        return true;
    });

    /// <summary>Commits a drag to its final catalog position in one queued write.</summary>
    public Task ReorderLabelAsync(string id, int destinationIndex) => Run(() =>
    {
        var labels = _labels.ToList();
        int sourceIndex = labels.FindIndex(label => label.Id == id);
        if (sourceIndex < 0) return false;
        int targetIndex = Math.Clamp(destinationIndex, 0, labels.Count - 1);
        if (sourceIndex == targetIndex) return false;
        var label = labels[sourceIndex];
        labels.RemoveAt(sourceIndex);
        labels.Insert(targetIndex, label);
        SaveLabels(labels.ToArray());
        return true;
    });
}
