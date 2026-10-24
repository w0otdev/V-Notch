using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services;

// Focus, child-window and message helpers used by the selection readers. Native side: Native/Services/Win32Interop.cpp.
internal static unsafe partial class Win32Interop
{
    private const int ChildWindowCapacity = 1024;

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_gui_thread_focus")]
    private static partial int NativeGetGuiThreadFocus(uint threadId, out long focus);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_is_child")]
    private static partial int NativeIsChild(long parent, long child, out byte isChild);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_ancestor")]
    private static partial int NativeGetAncestor(long hwnd, uint flags, out long ancestor);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_enum_child_windows")]
    private static partial int NativeEnumChildWindows(long parent, long* buffer, int capacity, out int written);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_send_unicode_text")]
    private static partial int NativeSendUnicodeText(char* text, int length, out int sent, out int expected);

    /// <summary>Focused control of the foreground GUI thread that owns <paramref name="window"/>, or zero.</summary>
    public static IntPtr GetFocusedControl(IntPtr window)
    {
        uint thread = GetWindowThreadProcessId(window, out _);
        return thread != 0 && NativeGetGuiThreadFocus(thread, out long focus) == 0 ? (IntPtr)focus : IntPtr.Zero;
    }

    public static bool IsChild(IntPtr parent, IntPtr child) =>
        NativeIsChild(parent, child, out byte isChild) == 0 && isChild != 0;

    public static IntPtr GetAncestor(IntPtr hwnd, uint flags) =>
        NativeGetAncestor(hwnd, flags, out long ancestor) == 0 ? (IntPtr)ancestor : IntPtr.Zero;

    /// <summary>Walks a native snapshot of the child windows until <paramref name="enumProc"/> returns false.</summary>
    public static bool EnumChildWindows(IntPtr parent, EnumWindowsProc enumProc, IntPtr lParam)
    {
        long* windows = stackalloc long[ChildWindowCapacity];
        if (NativeEnumChildWindows(parent, windows, ChildWindowCapacity, out int count) != 0) return false;
        for (int i = 0; i < count; i++)
        {
            if (!enumProc((IntPtr)windows[i], lParam)) break;
        }

        return true;
    }

    /// <summary>
    /// Sends a message whose WPARAM and LPARAM are written by the target (EM_GETSEL style).
    /// </summary>
    public static IntPtr SendMessageSelectionTimeout(
        IntPtr hwnd, uint message, out int wParam, out int lParam, uint flags, uint timeout, out IntPtr result)
    {
        int start = 0, end = 0;
        IntPtr sent = SendMessageTimeout(hwnd, message, (IntPtr)(&start), (IntPtr)(&end), flags, timeout, out result);
        wParam = start;
        lParam = end;
        return sent;
    }

    /// <summary>
    /// Sends a text-retrieval message (WM_GETTEXT, LB_GETTEXT, ...) with a UTF-16 buffer sized from
    /// <paramref name="text"/>.Capacity and appends the null-terminated reply to it.
    /// </summary>
    public static IntPtr SendMessageTextTimeout(
        IntPtr hwnd, uint message, IntPtr wParam, StringBuilder text, uint flags, uint timeout, out IntPtr result)
    {
        int capacity = Math.Max(2, text.Capacity + 1);
        char[] rented = ArrayPool<char>.Shared.Rent(capacity);
        try
        {
            rented.AsSpan(0, capacity).Clear();
            fixed (char* buffer = rented)
            {
                IntPtr sent = SendMessageTimeout(hwnd, message, wParam, (IntPtr)buffer, flags, timeout, out result);
                if (sent != IntPtr.Zero)
                {
                    int length = Array.IndexOf(rented, '\0', 0, capacity);
                    text.Clear();
                    text.Append(rented, 0, length < 0 ? capacity : length);
                }

                return sent;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Types the text as Unicode keystrokes; true when every key event was accepted.</summary>
    public static bool SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        fixed (char* chars = text)
            return NativeSendUnicodeText(chars, text.Length, out _, out _) == 0;
    }
}
