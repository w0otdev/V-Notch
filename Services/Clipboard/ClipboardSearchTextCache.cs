namespace VNotch.Services.Clipboard;

/// <summary>Used only on the store's serialized worker; never persists decrypted Personal text.</summary>
internal sealed class ClipboardSearchTextCache(int maxCharacters = 8 * 1024 * 1024, int maxEntries = 2048)
{
    private readonly Dictionary<Guid, (string Text, LinkedListNode<Guid> Node)> _items = [];
    private readonly LinkedList<Guid> _recent = [];
    private int _characters;

    internal bool TryGet(Guid id, out string text)
    {
        if (!_items.TryGetValue(id, out var item)) { text = string.Empty; return false; }
        _recent.Remove(item.Node);
        _recent.AddLast(item.Node);
        text = item.Text;
        return true;
    }

    internal void Set(Guid id, string? text)
    {
        Remove(id);
        text ??= string.Empty;
        if (text.Length > maxCharacters || maxEntries <= 0) return;
        while (_recent.First is { } first &&
            (_characters > maxCharacters - text.Length || _items.Count >= maxEntries))
            Remove(first.Value);
        _items[id] = (text, _recent.AddLast(id));
        _characters += text.Length;
    }

    internal void Remove(Guid id)
    {
        if (!_items.Remove(id, out var item)) return;
        _recent.Remove(item.Node);
        _characters -= item.Text.Length;
    }

    internal void Clear()
    {
        _items.Clear();
        _recent.Clear();
        _characters = 0;
    }
}
