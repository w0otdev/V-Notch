using System;
using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal static partial class TranslationTextFormatting
{
    private const string NativeDll = "VNotch.Native.dll";
    private const int VN_ERR_BUFFER_TOO_SMALL = -2;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_format_text(
        ReadOnlySpan<char> textPtr,
        int textLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    // Only change paragraph separators; never rewrite translated words or punctuation.
    // Preserve internal line breaks within Markdown fenced code blocks.
    internal static string Format(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        ReadOnlySpan<char> span = text.AsSpan();
        int cap = span.Length * 2 + 32;
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        int rc = vn_translation_format_text(span, span.Length, buf, buf.Length, out int len);
        if (rc == VN_ERR_BUFFER_TOO_SMALL && len > buf.Length)
        {
            buf = new char[len];
            vn_translation_format_text(span, span.Length, buf, buf.Length, out len);
        }
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }
}
