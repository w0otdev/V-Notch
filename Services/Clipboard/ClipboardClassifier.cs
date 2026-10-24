using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

/// <summary>Offline keyword and syntax heuristics; no model or semantic inference is used.</summary>
public static partial class ClipboardClassifier
{
    private const string NativeDll = "VNotch.Native.dll";

    public static readonly string[] GroupNames = ["Work", "Ideas", "Email", "Shopping", "Receipts", "Important"];

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_is_image_path(ReadOnlySpan<char> pathPtr, int pathLen, out byte outIsImage);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_try_parse_color(ReadOnlySpan<char> valPtr, int valLen, out uint outArgb, out byte outOk);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_try_extract_hex_color(
        ReadOnlySpan<char> textPtr,
        int textLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outOk);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_classify_groups(ReadOnlySpan<char> textPtr, int textLen, out uint outGroupMask);

    public static ClipboardKind Detect(ClipboardCapture capture)
    {
        if (capture.FilePaths.Length != 0)
            return capture.FilePaths.All(p => IsImagePath(p)) ? ClipboardKind.Image : ClipboardKind.File;
        if (capture.ImagePng != null) return ClipboardKind.Image;
        string text = capture.Text.Trim();
        if (TryParseColor(text, out _)) return ClipboardKind.Color;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto")
            return ClipboardKind.Link;
        if (TryExtractHexColor(text, out _)) return ClipboardKind.Color;
        if (CodePattern().IsMatch(text)) return ClipboardKind.Code;
        return ClipboardKind.Text;
    }

    private static bool IsImagePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        ReadOnlySpan<char> span = path.AsSpan();
        vn_clipboard_is_image_path(span, span.Length, out byte isImage);
        return isImage != 0;
    }

    public static string[] Classify(string text, ClipboardKind kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        ReadOnlySpan<char> span = text.AsSpan();
        vn_clipboard_classify_groups(span, span.Length, out uint mask);

        List<string>? groups = null;
        for (int i = 0; i < GroupNames.Length; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                groups ??= new List<string>(2);
                groups.Add(GroupNames[i]);
            }
        }

        if (text.Contains('@') && EmailPattern().IsMatch(text))
        {
            if (groups == null) return ["Email"];
            if (!groups.Contains("Email")) groups.Add("Email");
        }

        return groups?.ToArray() ?? Array.Empty<string>();
    }

    public static bool TryParseColor(string value, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        ReadOnlySpan<char> span = value.AsSpan();
        vn_clipboard_try_parse_color(span, span.Length, out argb, out byte ok);
        return ok != 0;
    }

    public static bool TryExtractHexColor(string text, out string hex)
    {
        hex = "";
        if (string.IsNullOrEmpty(text)) return false;
        ReadOnlySpan<char> span = text.AsSpan();
        Span<char> buf = stackalloc char[16];
        vn_clipboard_try_extract_hex_color(span, span.Length, buf, buf.Length, out int len, out byte ok);
        if (ok == 0 || len <= 0) return false;
        hex = new string(buf[..len]);
        return true;
    }

    // Punctuation alone is common in prose. Require a declaration, a call or another syntax signal.
    [GeneratedRegex("""
        (?m)(
            ^[ \t]*(
                using\s+[\w.]+;
                | import\s
                | from\s+\w+\s+import
                | (?:public|private|internal)\s+(?:static\s+)?(?:class|void|Task)\b
                | (?:const|let|var|int|long|float|double|decimal|bool|string|char|byte)\s+\w+\s*=
                | def\s+\w+\(
                | function\s+\w+\(
                | SELECT\s+.+\s+FROM\b
                | <\/?(?:div|html|script|style|body)\b
                | (?:\w+\.)*\w+\([^\r\n]*\);[ \t]*$
            )
            | =>\s*\{
        )
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex CodePattern();
    [GeneratedRegex(@"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
