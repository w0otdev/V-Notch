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

            // Wait briefly for modifiers (like double-Shift) to be released so we never inject Ctrl+Shift+C.
            if (!await WaitForModifiersUpAsync(isCurrent)) return null;

            // Ctrl+C interrupts terminal jobs; Ctrl+Insert is their non-destructive copy command.
            ushort copyKey = IsTerminal(window) ? (ushort)0x2D : (ushort)0x43;
            Input[] keys = [Key(0x11), Key(copyKey), Key(copyKey, true), Key(0x11, true)];
            if (SendInput(4, keys, Marshal.SizeOf<Input>()) != 4)
            {
                Input[] release = [Key(copyKey, true), Key(0x11, true)];
                SendInput(2, release, Marshal.SizeOf<Input>());
                return null;
            }
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(30);
                uint sequence = GetClipboardSequenceNumber();
                if (sequence == before) continue; // Allow an already-sent Copy to finish so it can be restored.
                GetWindowThreadProcessId(GetClipboardOwner(), out uint owner);
                GetWindowThreadProcessId(window, out uint target);
                if (!IsOwnerAcceptable(owner, target)) return null;
                copiedSequence = sequence;
                if (!isCurrent()) return null;
                try
                {
                    string text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                    if (!AcceptCopy(before, sequence, GetClipboardSequenceNumber(), isCurrent(), text)) return null;
                    var point = System.Windows.Forms.Cursor.Position;
                    return new(-Environment.TickCount64, TranslationInput.Prefix(text, TranslationInput.MaxCharacters), new Rect(point.X, point.Y, 1, 1), window, false)
                    { InputTruncated = text.Length > TranslationInput.MaxCharacters };
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

    private static async Task<bool> WaitForModifiersUpAsync(Func<bool> isCurrent)
    {
        int[] keys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0x43, 0x2D];
        for (int i = 0; i < 10; i++)
        {
            if (!isCurrent()) return false;
            if (!keys.Any(k => (Win32Interop.GetAsyncKeyState(k) & 0x8000) != 0)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    internal static bool IsOwnerAcceptable(uint owner, uint target)
    {
        if (owner == 0 || owner == target) return true;
        try
        {
            using var ownerProc = Process.GetProcessById((int)owner);
            using var targetProc = Process.GetProcessById((int)target);
            if (string.Equals(ownerProc.ProcessName, targetProc.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    internal static bool AcceptCopy(uint before, uint copied, uint current, bool focused, string text) =>
        focused && copied != before && copied == current && !string.IsNullOrWhiteSpace(text);

    internal static DataObject Snapshot(IDataObject? source)
    {
        var copy = new DataObject();
        if (source == null) return copy;
        foreach (string format in source.GetFormats(false))
        {
            try
            {
                object? value = source.GetData(format, false);
                if (value == null) continue;
                object cloned = value switch
                {
                    string text => text,
                    byte[] bytes => bytes.Clone(),
                    string[] paths => paths.Clone(),
                    MemoryStream stream => new MemoryStream(stream.ToArray()),
                    Stream stream => CopyStream(stream),
                    BitmapSource bitmap => bitmap.Clone(),
                    System.Drawing.Image image => (System.Drawing.Image)image.Clone(),
                    ValueType val => val,
                    _ => throw new NotSupportedException("Clipboard format cannot be preserved.")
                };
                copy.SetData(format, cloned, false);
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
                // Format conversion or delayed-rendering read errors on auxiliary formats are safely ignored.
            }
        }
        return copy;
    }

    private static MemoryStream CopyStream(Stream stream)
    {
        // A non-seekable stream may already be partly consumed; never restore truncated data.
        if (!stream.CanSeek) throw new NotSupportedException("Clipboard stream cannot be rewound.");
        var ms = new MemoryStream();
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            stream.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }
        catch { ms.Dispose(); throw; }
        finally { stream.Position = position; }
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
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
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
