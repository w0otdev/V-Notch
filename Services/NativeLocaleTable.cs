using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

internal sealed partial class NativeLocaleTable : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string Dll = "VNotch.Native.dll";
    internal NativeLocaleTable() : base(true)
    {
        SetHandle(vn_locale_create());
        if (IsInvalid) throw new OutOfMemoryException();
    }

    [LibraryImport(Dll)] private static partial nint vn_locale_create();
    [LibraryImport(Dll)] private static partial void vn_locale_destroy(nint table);
    [LibraryImport(Dll)] private static partial int vn_locale_merge(NativeLocaleTable table, ReadOnlySpan<byte> json, int length);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint vn_locale_get(NativeLocaleTable table, string key, int length, out int size);
    [LibraryImport(Dll)] private static partial nint vn_locale_key(NativeLocaleTable table, int index, out int size);

    protected override bool ReleaseHandle() { vn_locale_destroy(handle); return true; }
    internal void Merge(ReadOnlySpan<byte> json) => Marshal.ThrowExceptionForHR(vn_locale_merge(this, json, json.Length));

    internal string? Get(string key)
    {
        nint ptr = vn_locale_get(this, key, key.Length, out int length);
        string? value = ptr == 0 ? null : Marshal.PtrToStringUni(ptr, length);
        GC.KeepAlive(this);
        return value;
    }

    internal IReadOnlyCollection<string> Keys()
    {
        var keys = new List<string>();
        for (int i = 0; ; i++)
        {
            nint ptr = vn_locale_key(this, i, out int length);
            if (ptr == 0) break;
            keys.Add(Marshal.PtrToStringUni(ptr, length)!);
        }
        GC.KeepAlive(this);
        return keys;
    }
}

internal sealed partial class NativeTranslationIndex : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string Dll = "VNotch.Native.dll";
    private readonly NativeLocaleTable[] _tables;
    private readonly NativeLocaleTable[] _owned;
    [StructLayout(LayoutKind.Sequential)]
    private struct Translation { public nint Data; public int Length; }

    [LibraryImport(Dll)] private static partial nint vn_translation_index(ReadOnlySpan<nint> tables, int count);
    [LibraryImport(Dll)] private static partial void vn_translation_destroy(nint index);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_find(NativeTranslationIndex index, string query, int length,
        Span<Translation> translations, int capacity);

    internal NativeTranslationIndex(NativeLocaleTable[] tables, NativeLocaleTable[] owned) : base(true)
    {
        _tables = tables;
        _owned = owned;
        var handles = Array.ConvertAll(tables, t => t.DangerousGetHandle());
        SetHandle(vn_translation_index(handles, handles.Length));
        if (IsInvalid) throw new OutOfMemoryException();
    }
    protected override bool ReleaseHandle()
    {
        vn_translation_destroy(handle);
        foreach (var table in _owned) table.Dispose();
        return true;
    }

    internal List<string> Find(string text)
    {
        Span<Translation> buffer = stackalloc Translation[32];
        int count;
        while ((count = vn_translation_find(this, text, text.Length, buffer, buffer.Length)) < 0)
            buffer = new Translation[checked(buffer.Length * 2)];
        if (count == 0) return [text];
        var translations = new List<string>(count);
        foreach (var value in buffer[..count])
            translations.Add(Marshal.PtrToStringUni(value.Data, value.Length)!);
        GC.KeepAlive(_tables);
        return translations;
    }
}
