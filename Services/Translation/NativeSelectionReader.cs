using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using static VNotch.Services.Win32Interop;
using NativePoint = VNotch.Services.Win32Interop.POINT;

namespace VNotch.Services.Translation;

internal static partial class NativeSelectionReader
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_native_selection_is_edit(
        ReadOnlySpan<char> name,
        int nameLen,
        out byte outRes);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_native_selection_is_scintilla(
        ReadOnlySpan<char> name,
        int nameLen,
        out byte outRes);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_native_selection_is_combobox(
        ReadOnlySpan<char> name,
        int nameLen,
        out byte outRes);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_native_selection_is_listbox(
        ReadOnlySpan<char> name,
        int nameLen,
        out byte outRes);

    [LibraryImport(NativeDll)]
    private static partial int vn_native_selection_resolve_range(
        int rawStart,
        int rawEnd,
        long packedSelection,
        out int outStart,
        out int outEnd,
        out byte outValid);

    private const uint EmGetSel = 0x00B0;
    private const uint CbGetEditSel = 0x0140;
    private const uint CbGetCurSel = 0x0147;
    private const uint CbGetLbText = 0x0148;
    private const uint CbGetLbTextLen = 0x0149;
    private const uint LbGetSel = 0x0187;
    private const uint LbGetCurSel = 0x0188;
    private const uint LbGetText = 0x0189;
    private const uint LbGetTextLen = 0x018A;
    private const uint LbGetCount = 0x018B;
    private const uint SciGetSelectionStart = 2143;
    private const uint SciGetSelectionEnd = 2145;
    private const uint SciGetSelText = 2161;
    private const uint WmGetText = 0x000D;
    private const uint EmGetSelText = 0x043E;
    private const uint EmGetPasswordChar = 0x00D2;
    private const int GwlStyle = -16;
    private const int EsPassword = 0x0020;
    private const int LbsMultiSel = 0x0008;
    private const int LbsExtendedSel = 0x0800;

    internal static IntPtr FocusedControl(IntPtr window)
    {
        return GetFocusedControl(window);
    }

    internal static string ClassName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "";
        var name = new StringBuilder(256);
        GetClassName(handle, name, name.Capacity);
        return name.ToString();
    }

    internal static bool IsPassword(IntPtr handle) => IsPassword(handle, ClassName(handle));

    internal static bool IsPassword(IntPtr handle, string className)
    {
        if (handle == IntPtr.Zero || !IsEditOrCombo(className)) return false;
        if ((GetWindowLong(handle, GwlStyle) & EsPassword) != 0) return true;
        return SendMessageTimeout(handle, EmGetPasswordChar, IntPtr.Zero, IntPtr.Zero, 2, 50, out var pwdChar) != IntPtr.Zero && pwdChar != IntPtr.Zero;
    }

    internal static bool IsEdit(string name) =>
        name != null && vn_native_selection_is_edit(name.AsSpan(), name.Length, out byte res) == 0 && res != 0;

    internal static bool IsScintilla(string name) =>
        name != null && vn_native_selection_is_scintilla(name.AsSpan(), name.Length, out byte res) == 0 && res != 0;

    internal static bool IsComboBox(string name) =>
        name != null && vn_native_selection_is_combobox(name.AsSpan(), name.Length, out byte res) == 0 && res != 0;

    internal static bool IsListBox(string name) =>
        name != null && vn_native_selection_is_listbox(name.AsSpan(), name.Length, out byte res) == 0 && res != 0;

    private static bool IsEditOrCombo(string name) => IsEdit(name) || IsComboBox(name);
    private static bool IsSupportedNativeControl(string name) => IsEdit(name) || IsComboBox(name) || IsListBox(name) || IsScintilla(name);

    internal static TranslationSelection? Read(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        var cursor = System.Windows.Forms.Cursor.Position;
        var candidates = new List<IntPtr>(8);

        var focused = FocusedControl(window);
        if (focused != IntPtr.Zero && BelongsToWindow(window, focused)) candidates.Add(focused);

        var pointHwnd = WindowFromPoint(new NativePoint { X = cursor.X, Y = cursor.Y });
        if (pointHwnd != IntPtr.Zero && BelongsToWindow(window, pointHwnd) && !candidates.Contains(pointHwnd))
            candidates.Add(pointHwnd);

        if (!candidates.Contains(window)) candidates.Add(window);

        foreach (var candidate in candidates)
        {
            string? text = ReadControl(candidate);
            if (text != null) return ToSelection(text, cursor, window);
        }

        // Enumerate child controls (up to 32) in case focus was transferred to a container/toolbar
        // while an edit control inside the window still holds the active selection.
        string? childSelection = null;
        int inspected = 0;
        EnumChildWindows(window, (child, _) =>
        {
            if (++inspected > 32) return false;
            if (candidates.Contains(child)) return true;
            string className = ClassName(child);
            if (!IsSupportedNativeControl(className)) return true;
            childSelection = ReadControl(child, className);
            return childSelection == null;
        }, IntPtr.Zero);

        return childSelection != null ? ToSelection(childSelection, cursor, window) : null;
    }

    private static TranslationSelection ToSelection(string text, System.Drawing.Point cursor, IntPtr window) =>
        new(-Environment.TickCount64, TranslationInput.Prefix(text, TranslationInput.MaxCharacters), new Rect(cursor.X, cursor.Y, 1, 1), window, false)
        { InputTruncated = text.Length > TranslationInput.MaxCharacters };

    private static bool BelongsToWindow(IntPtr window, IntPtr candidate)
    {
        if (candidate == IntPtr.Zero) return false;
        if (candidate == window || IsChild(window, candidate) || GetAncestor(candidate, 2) == window || GetAncestor(candidate, 3) == window)
            return true;
        GetWindowThreadProcessId(window, out uint windowPid);
        GetWindowThreadProcessId(candidate, out uint candidatePid);
        return windowPid != 0 && windowPid == candidatePid;
    }

    internal static string? ReadControl(IntPtr control) =>
        control == IntPtr.Zero ? null : ReadControl(control, ClassName(control));

    internal static string? ReadControl(IntPtr control, string className)
    {
        if (control == IntPtr.Zero || !IsSupportedNativeControl(className) || IsPassword(control, className)) return null;

        if (IsListBox(className))
            return ReadListBoxControl(control);

        if (IsScintilla(className))
            return ReadScintillaControl(control);

        bool isCombo = IsComboBox(className);
        uint selMsg = isCombo ? CbGetEditSel : EmGetSel;
        if (SendMessageSelectionTimeout(control, selMsg, out int start, out int end, 2, 100, out var selection) == IntPtr.Zero)
            return isCombo ? ReadComboBoxSelectedItem(control) : null;

        if (vn_native_selection_resolve_range(start, end, selection.ToInt64(), out start, out end, out byte validRange) != 0 || validRange == 0)
            return isCombo ? ReadComboBoxSelectedItem(control) : null;

        bool isRichEdit = className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
        int selectionLength = end - start;

        if (isRichEdit && selectionLength <= TranslationInput.MaxCharacters + 1)
        {
            GetWindowThreadProcessId(control, out uint controlPid);
            if (controlPid == Environment.ProcessId)
            {
                var selBuf = new StringBuilder(selectionLength + 4);
                if (SendMessageTextTimeout(control, EmGetSelText, IntPtr.Zero, selBuf, 2, 100, out var copied) != IntPtr.Zero && copied != IntPtr.Zero)
                {
                    string direct = selBuf.ToString();
                    if (!string.IsNullOrWhiteSpace(direct))
                        return SelectionTextReader.SanitizeSelectionPart(direct.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                }
            }
        }

        // Allocate extra headroom for RichEdit where WM_GETTEXT expands '\r' into "\r\n"
        int capturedEnd = Math.Min(end, start + TranslationInput.MaxCharacters + 1);
        if (capturedEnd > 65536) return null;

        int bufferCapacity = isRichEdit ? Math.Min(65536, capturedEnd * 2 + 64) : capturedEnd + 1;
        var buffer = new StringBuilder(bufferCapacity);
        if (SendMessageTextTimeout(control, WmGetText, new IntPtr(buffer.Capacity), buffer, 2, 100, out _) == IntPtr.Zero) return null;
        if (SendMessageSelectionTimeout(control, selMsg, out int afterStart, out int afterEnd, 2, 100, out var after) == IntPtr.Zero) return null;
        if (after != selection && (afterStart != start || afterEnd != end)) return null;

        string text = buffer.ToString();
        if (isRichEdit && text.Contains('\r'))
        {
            // RichEdit 2.0+ EM_GETSEL uses character indices where CRLF is 1 character ('\r'),
            // whereas WM_GETTEXT returns "\r\n". Normalize "\r\n" -> "\n" and '\r' -> '\n'.
            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (end <= normalized.Length)
                return Slice(normalized, start, Math.Min(end, start + TranslationInput.MaxCharacters + 1));
            return null;
        }
        return Slice(text, start, capturedEnd);
    }

    private static string? ReadScintillaControl(IntPtr control)
    {
        if (SendMessageTimeout(control, SciGetSelectionStart, IntPtr.Zero, IntPtr.Zero, 2, 100, out var startRes) == IntPtr.Zero ||
            SendMessageTimeout(control, SciGetSelectionEnd, IntPtr.Zero, IntPtr.Zero, 2, 100, out var endRes) == IntPtr.Zero)
            return null;
        long start = startRes.ToInt64(), end = endRes.ToInt64();
        if (end <= start || start < 0 || end > 65536) return null;

        GetWindowThreadProcessId(control, out uint controlPid);
        if (controlPid == Environment.ProcessId && end - start <= 8192)
        {
            byte[] utf8 = new byte[(int)(end - start) + 4];
            unsafe
            {
                fixed (byte* ptr = utf8)
                {
                    if (SendMessageTimeout(control, SciGetSelText, IntPtr.Zero, new IntPtr(ptr), 2, 100, out var lenRes) != IntPtr.Zero && lenRes.ToInt64() > 0)
                    {
                        int byteLen = Array.IndexOf(utf8, (byte)0);
                        if (byteLen < 0) byteLen = (int)Math.Min(lenRes.ToInt64(), utf8.Length);
                        string decoded = Encoding.UTF8.GetString(utf8, 0, byteLen);
                        if (!string.IsNullOrWhiteSpace(decoded))
                            return SelectionTextReader.JoinSelection([decoded]);
                    }
                }
            }
        }
        return null;
    }

    private static string? ReadComboBoxSelectedItem(IntPtr control)
    {
        if (SendMessageTimeout(control, CbGetCurSel, IntPtr.Zero, IntPtr.Zero, 2, 100, out var indexResult) == IntPtr.Zero) return null;
        int index = (int)indexResult.ToInt64();
        if (index < 0) return null;
        if (SendMessageTimeout(control, CbGetLbTextLen, new IntPtr(index), IntPtr.Zero, 2, 100, out var lenResult) == IntPtr.Zero) return null;
        int len = (int)lenResult.ToInt64();
        if (len <= 0 || len > TranslationInput.MaxCharacters + 256) return null;
        var buffer = new StringBuilder(len + 2);
        if (SendMessageTextTimeout(control, CbGetLbText, new IntPtr(index), buffer, 2, 100, out _) == IntPtr.Zero) return null;
        return SelectionTextReader.JoinSelection([buffer.ToString()]);
    }

    private static string? ReadListBoxControl(IntPtr control)
    {
        int style = GetWindowLong(control, GwlStyle);
        bool multi = (style & (LbsMultiSel | LbsExtendedSel)) != 0;
        if (!multi)
        {
            if (SendMessageTimeout(control, LbGetCurSel, IntPtr.Zero, IntPtr.Zero, 2, 100, out var idxRes) == IntPtr.Zero) return null;
            int idx = (int)idxRes.ToInt64();
            string? item = idx >= 0 ? ReadListBoxItemText(control, idx) : null;
            return item == null ? null : SelectionTextReader.JoinSelection([item]);
        }

        if (SendMessageTimeout(control, LbGetCount, IntPtr.Zero, IntPtr.Zero, 2, 100, out var countRes) == IntPtr.Zero) return null;
        int count = Math.Min((int)countRes.ToInt64(), 256);
        if (count <= 0) return null;
        var parts = new List<string>();
        int total = 0;
        for (int i = 0; i < count && parts.Count < 32; i++)
        {
            if (SendMessageTimeout(control, LbGetSel, new IntPtr(i), IntPtr.Zero, 2, 50, out var selRes) == IntPtr.Zero || selRes.ToInt64() <= 0)
                continue;
            string? item = ReadListBoxItemText(control, i);
            if (string.IsNullOrWhiteSpace(item)) continue;
            parts.Add(item);
            total += item.Length + 1;
            if (total > TranslationInput.MaxCharacters + 1) break;
        }
        return SelectionTextReader.JoinSelection(parts);
    }

    private static string? ReadListBoxItemText(IntPtr control, int index)
    {
        if (SendMessageTimeout(control, LbGetTextLen, new IntPtr(index), IntPtr.Zero, 2, 100, out var lenRes) == IntPtr.Zero) return null;
        int len = (int)lenRes.ToInt64();
        if (len <= 0 || len > TranslationInput.MaxCharacters + 256) return null;
        var buffer = new StringBuilder(len + 2);
        return SendMessageTextTimeout(control, LbGetText, new IntPtr(index), buffer, 2, 100, out _) != IntPtr.Zero ? buffer.ToString() : null;
    }

    internal static string? Slice(string text, int start, int end)
    {
        if (start < 0 || end <= start || end > text.Length) return null;
        int cappedEnd = Math.Min(end, start + TranslationInput.MaxCharacters + 1);
        ReadOnlySpan<char> span = text.AsSpan(start, cappedEnd - start);
        return span.IsWhiteSpace() ? null : SelectionTextReader.SanitizeSelectionPart(span.ToString());
    }
}
