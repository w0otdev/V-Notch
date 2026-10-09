using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VNotch.Services.Translation;

// Only invoked by the explicit shortcut after accessibility capture fails.
internal static class SelectionClipboardCapture
{
    private static volatile bool _busy;
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private static uint _ignoredSequence;
    internal static bool IgnoreClipboardCapture(uint sequence) => _busy || (sequence != 0 && sequence == _ignoredSequence);

    internal static async Task<TranslationSelection?> CaptureAsync(IntPtr window, Func<bool> isCurrent)
    {
        await CaptureGate.WaitAsync();
        if (!isCurrent() || !CanCopy(window)) { CaptureGate.Release(); return null; }
        _busy = true;
        DataObject? backup = null;
        uint copiedSequence = 0;
        try
        {
            uint before = GetClipboardSequenceNumber();
            var data = System.Windows.Clipboard.GetDataObject();
            backup = Snapshot(data);
            if (!isCurrent() || before != GetClipboardSequenceNumber()) return null;
            // Never turn a held modifier into Ctrl+Shift+C, or release a user's key.
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x43, 0x2D }.Any(k => (Win32Interop.GetAsyncKeyState(k) & 0x8000) != 0)) return null;
            // Ctrl+C interrupts terminal jobs; Ctrl+Insert is their non-destructive copy command.
            ushort copyKey = IsTerminal(window) ? (ushort)0x2D : (ushort)0x43;
            Input[] keys = [Key(0x11), Key(copyKey), Key(copyKey, true), Key(0x11, true)];
            if (SendInput(4, keys, Marshal.SizeOf<Input>()) != 4)
            {
                Input[] release = [Key(copyKey, true), Key(0x11, true)];
                SendInput(2, release, Marshal.SizeOf<Input>());
                return null;
            }
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(30);
                uint sequence = GetClipboardSequenceNumber();
                if (sequence == before) continue; // Allow an already-sent Copy to finish so it can be restored.
                GetWindowThreadProcessId(GetClipboardOwner(), out uint owner);
                GetWindowThreadProcessId(window, out uint target);
                if (owner == 0 || owner != target) return null;
                copiedSequence = sequence;
                if (!isCurrent()) return null;
                try
                {
                    string text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                    if (!AcceptCopy(before, sequence, GetClipboardSequenceNumber(), isCurrent(), text)) return null;
                    var point = System.Windows.Forms.Cursor.Position;
                    return new(-Environment.TickCount64, text, new Rect(point.X, point.Y, 1, 1), window, false);
                }
                catch (COMException) { } // The source may still hold the clipboard open.
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        { return null; }
        finally
        {
            // Do not overwrite a newer copy from the user or another application.
            try
            {
                for (int attempt = 0; backup != null && copiedSequence != 0 && GetClipboardSequenceNumber() == copiedSequence && attempt < 5; attempt++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetDataObject(backup, true);
                        _ignoredSequence = GetClipboardSequenceNumber();
                        break;
                    }
                    catch (COMException) { await Task.Delay(25); }
                }
            }
            finally { _busy = false; CaptureGate.Release(); }
        }
    }

    internal static bool AcceptCopy(uint before, uint copied, uint current, bool focused, string text) =>
        focused && copied != before && copied == current && !string.IsNullOrWhiteSpace(text) && text.Length <= 2000;

    internal static DataObject Snapshot(IDataObject? source)
    {
        var copy = new DataObject();
        if (source == null) return copy;
        foreach (string format in source.GetFormats(false))
        {
            object? value = source.GetData(format, false);
            object cloned = value switch
            {
                string text => text,
                byte[] bytes => bytes.Clone(),
                string[] paths => paths.Clone(),
                MemoryStream stream => new MemoryStream(stream.ToArray()),
                BitmapSource bitmap => bitmap.Clone(),
                null => throw new NotSupportedException("Clipboard format cannot be preserved."),
                _ => throw new NotSupportedException("Clipboard format cannot be preserved.")
            };
            copy.SetData(format, cloned, false);
        }
        return copy;
    }

    private static bool CanCopy(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        return pid != 0 && pid != Environment.ProcessId && !NativeSelectionReader.IsPassword(NativeSelectionReader.FocusedControl(window));
    }
    private static bool IsTerminal(IntPtr window)
    {
        string name = NativeSelectionReader.ClassName(window);
        if (name.Contains("Console", StringComparison.OrdinalIgnoreCase) || name.Contains("CASCADIA", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            GetWindowThreadProcessId(window, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return UsesTerminalCopy(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }
    internal static bool UsesTerminalCopy(string name) => new[] { "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "mintty", "putty", "wezterm-gui", "alacritty", "Hyper", "Tabby" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, KeyCode = key, Flags = up ? 2u : 0 };
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort KeyCode;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
