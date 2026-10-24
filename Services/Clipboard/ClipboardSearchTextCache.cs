using System.Runtime.InteropServices;

namespace VNotch.Services.Clipboard;

/// <summary>Used only on the store's serialized worker; never persists decrypted Personal text.</summary>
internal sealed partial class ClipboardSearchTextCache(int maxCharacters = 8 * 1024 * 1024, int maxEntries = 2048)
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_search_cache_can_store(
        int textLength,
        int maxCharacters,
        int maxEntries,
        out byte outCanStore);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_search_cache_should_evict_oldest(
        int currentCharacters,
        int currentEntries,
        int textLength,
        int maxCharacters,
        int maxEntries,
        out byte outShouldEvict);

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
        if (vn_clipboard_search_cache_can_store(text.Length, maxCharacters, maxEntries, out byte canStore) != 0 || canStore == 0) return;
        while (_recent.First is { } first &&
            vn_clipboard_search_cache_should_evict_oldest(_characters, _items.Count, text.Length, maxCharacters, maxEntries, out byte shouldEvict) == 0 &&
            shouldEvict != 0)
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
