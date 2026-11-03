using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services.Translation;

internal static partial class SelectionTextReader
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selection_text_reader_needs_sanitization(
        ReadOnlySpan<char> part,
        int partLen,
        out byte outNeeds);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selection_text_reader_sanitize(
        ReadOnlySpan<char> part,
        int partLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll)]
    private static partial int vn_selection_text_reader_valid_bounds(
        byte isEmpty,
        double x,
        double y,
        double width,
        double height,
        out byte outValid);

    internal sealed record Result(string Text, Rect Bounds, NativeSelectionState Native, bool Editable) : IDisposable
    {
        internal bool InputTruncated { get; init; }
        public void Dispose() => Native.Dispose();
    }

    internal static Result? Read(IntPtr window, AutomationElement? eventElement, bool manual, out bool protectedInput,
        AutomationElement? previousElement = null, AutomationElement? rootElement = null, bool allowOwnProcess = false,
        NativeSelectionState? previousSelection = null)
    {
        int[]? eventId = null;
        try { eventId = eventElement?.GetRuntimeId(); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException or ArgumentException or NotSupportedException) { }
        return NativeSelectionState.Read(window, eventId, manual, allowOwnProcess, previousSelection, out protectedInput);
    }

    internal static string? JoinSelection(IEnumerable<string> parts)
    {
        var nonempty = parts
            .Select(SanitizeSelectionPart)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (nonempty.Length == 0) return null;
        string text = string.Join("\n", nonempty);
        // One extra character signals truncation without reading an unbounded UIA range.
        return text[..Math.Min(text.Length, TranslationInput.MaxCharacters + 1)];
    }

    internal static string SanitizeSelectionPart(string? part)
    {
        if (string.IsNullOrEmpty(part)) return "";
        if (vn_selection_text_reader_needs_sanitization(part.AsSpan(), part.Length, out byte needs) != 0 || needs == 0)
            return part;
        char[] buf = new char[part.Length];
        _ = vn_selection_text_reader_sanitize(part.AsSpan(), part.Length, buf, buf.Length, out int len);
        return new string(buf, 0, Math.Clamp(len, 0, buf.Length));
    }

    internal static bool ValidBounds(Rect r) =>
        vn_selection_text_reader_valid_bounds(r.IsEmpty ? (byte)1 : (byte)0, r.X, r.Y, r.Width, r.Height, out byte valid) == 0 && valid != 0;

}
