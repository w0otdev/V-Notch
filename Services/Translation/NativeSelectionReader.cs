using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace VNotch.Services.Translation;

internal static class NativeSelectionReader
{
    internal static IntPtr FocusedControl(IntPtr window)
    {
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    internal static string ClassName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        GetClassName(handle, name, name.Capacity);
        return name.ToString();
    }
    internal static bool IsPassword(IntPtr handle) => IsEdit(ClassName(handle)) && (GetWindowLong(handle, -16) & 0x20) != 0;
    private static bool IsEdit(string name) => name.Equals("Edit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase);

    internal static TranslationSelection? Read(IntPtr window)
    {
        var control = FocusedControl(window);
        if (control == IntPtr.Zero || (control != window && !IsChild(window, control))) return null;
        string? text = ReadControl(control);
        if (text == null) return null;
        var cursor = System.Windows.Forms.Cursor.Position;
        return new(-Environment.TickCount64, text, new Rect(cursor.X, cursor.Y, 1, 1), window, false);
    }

    internal static string? ReadControl(IntPtr control)
    {
        if (!IsEdit(ClassName(control)) || IsPassword(control)) return null;
        // EM_GETSEL and WM_GETTEXT are system messages marshalled by Windows.
        // No remote pointers or unbounded SendMessage calls are used.
        if (SendMessageTimeout(control, 0xB0, IntPtr.Zero, IntPtr.Zero, 2, 100, out var selection) == IntPtr.Zero) return null;
        uint packed = unchecked((uint)selection.ToInt64());
        if (packed == uint.MaxValue) return null;
        int start = (int)(packed & 0xffff), end = (int)(packed >> 16);
        if (end <= start || end - start > 2000) return null;
        var buffer = new StringBuilder(end + 1);
        if (GetTextTimeout(control, 0xD, new IntPtr(buffer.Capacity), buffer, 2, 100, out _) == IntPtr.Zero) return null;
        if (SendMessageTimeout(control, 0xB0, IntPtr.Zero, IntPtr.Zero, 2, 100, out var after) == IntPtr.Zero || after != selection) return null;
        string text = buffer.ToString();
        // RichEdit versions disagree about CR vs CRLF offsets in WM_GETTEXT.
        // Let the selection-aware UIA/Copy paths handle those instead of slicing the wrong text.
        if (ClassName(control).Contains("RichEdit", StringComparison.OrdinalIgnoreCase) && text.Contains('\r')) return null;
        return Slice(text, start, end);
    }
    internal static string? Slice(string text, int start, int end) => start >= 0 && end > start && end <= text.Length && end - start <= 2000 &&
        !string.IsNullOrWhiteSpace(text[start..end]) ? text[start..end] : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern IntPtr GetTextTimeout(IntPtr hwnd, uint message, IntPtr wparam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
}
