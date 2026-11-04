using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

// Only the references needed by the UI stay managed. GUID and duplicate lookup tables
// live in native flat storage. Every operation is protected by the store's _entriesLock.
internal sealed partial class NativeClipboardEntryIndex : IEnumerable<KeyValuePair<Guid, ClipboardEntry>>, IDisposable
{
    private sealed class IndexHandle : SafeHandle
    {
        internal IndexHandle(long value) : base(IntPtr.Zero, true) => SetHandle((nint)value);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => vn_clipboard_index_close((long)handle) == 0;
    }
    private readonly List<ClipboardEntry> _items = new();
    private readonly IndexHandle _handle;
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_index_create(out long handle);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_index_close(long handle);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_index_find(IndexHandle handle, ulong a, ulong b, out int slot);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_index_remove(IndexHandle handle, ulong a, ulong b);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_index_put(IndexHandle handle, ulong a, ulong b, int slot, ReadOnlySpan<char> fingerprint, int length, long ticks, byte personal);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_index_duplicate(IndexHandle handle, ReadOnlySpan<char> fingerprint, int length, byte personal, out int slot);

    internal NativeClipboardEntryIndex()
    {
        Check(vn_clipboard_index_create(out long handle));
        _handle = new(handle);
    }
    private static void Check(int status) { if (status != 0) throw new IOException("Clipboard memory index operation failed."); }
    private static (ulong A, ulong B) Key(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16]; id.TryWriteBytes(bytes);
        var parts = MemoryMarshal.Cast<byte, ulong>(bytes); return (parts[0], parts[1]);
    }
    private int Find(Guid id)
    {
        var (a, b) = Key(id); Check(vn_clipboard_index_find(_handle, a, b, out int slot)); return slot;
    }
    private void Put(ClipboardEntry entry, int slot)
    {
        var (a, b) = Key(entry.Id);
        Check(vn_clipboard_index_put(_handle, a, b, slot, entry.Fingerprint, entry.Fingerprint.Length, entry.CopiedUtc.Ticks, entry.IsPersonal ? (byte)1 : (byte)0));
    }
    internal int Count => _items.Count;
    internal ClipboardEntry this[Guid id]
    {
        set
        {
            int slot = Find(id);
            if (slot >= 0) { Put(value, slot); _items[slot] = value; }
            else
            {
                _items.EnsureCapacity(_items.Count + 1);
                Put(value, _items.Count); _items.Add(value);
            }
        }
    }
    internal bool TryGetValue(Guid id, [NotNullWhen(true)] out ClipboardEntry? entry)
    {
        int slot = Find(id); entry = slot < 0 ? null : _items[slot]; return entry != null;
    }
    internal bool TryRemove(Guid id, [NotNullWhen(true)] out ClipboardEntry? entry)
    {
        int slot = Find(id);
        if (slot < 0) { entry = null; return false; }
        entry = _items[slot];
        var last = _items[^1];
        if (slot != _items.Count - 1) Put(last, slot);
        var (a, b) = Key(id); Check(vn_clipboard_index_remove(_handle, a, b));
        _items[slot] = last; _items.RemoveAt(_items.Count - 1); return true;
    }
    internal ClipboardEntry? FindDuplicate(string fingerprint, bool personal)
    {
        Check(vn_clipboard_index_duplicate(_handle, fingerprint, fingerprint.Length, personal ? (byte)1 : (byte)0, out int slot));
        return slot < 0 ? null : _items[slot];
    }
    public IEnumerator<KeyValuePair<Guid, ClipboardEntry>> GetEnumerator()
    {
        foreach (var entry in _items) yield return new(entry.Id, entry);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { _items.Clear(); _handle.Dispose(); }
}
