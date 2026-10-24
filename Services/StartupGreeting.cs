using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class StartupGreeting
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_startup_greeting_uses_vietnamese(
        ReadOnlySpan<char> langPtr,
        int langLen,
        out byte outResult);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_startup_greeting_uses_english_handwriting(
        ReadOnlySpan<char> langPtr,
        int langLen,
        out byte outResult);

    // Vietnamese uses Xin Chao; every other locale falls back to Hello.
    internal static bool UsesVietnamese(string? language)
    {
        if (language == null) return false;
        ReadOnlySpan<char> span = language.AsSpan();
        return vn_startup_greeting_uses_vietnamese(span, span.Length, out byte res) == 0 && res != 0;
    }

    internal static bool UsesEnglishHandwriting(string? language)
    {
        if (language == null) return true;
        ReadOnlySpan<char> span = language.AsSpan();
        return vn_startup_greeting_uses_english_handwriting(span, span.Length, out byte res) == 0 ? res != 0 : !UsesVietnamese(language);
    }
}
