using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Windows;

namespace VNotch.Services.Translation;

internal sealed partial class NativeSelectionState : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string NativeDll = "VNotch.Native.dll";
    private NativeSelectionState(long value) : base(true) => SetHandle((IntPtr)value);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selection_read(IntPtr window, ReadOnlySpan<int> eventId, int eventIdLength, IntPtr focus, long previous,
        byte manual, byte allowSelf, int limit, Span<char> text, int capacity, out int length, Span<double> bounds,
        out byte protectedInput, out byte editable, out byte truncated, out long handle);
    [LibraryImport(NativeDll)]
    private static partial int vn_selection_close(long handle);
    [LibraryImport(NativeDll)]
    private static partial int vn_selection_same(NativeSelectionState first, NativeSelectionState second, out byte equal);
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int vn_selection_prepare_replace(NativeSelectionState handle, ReadOnlySpan<char> expected, int expectedLength,
        ReadOnlySpan<char> replacement, int replacementLength, int* cancellation, out int outcome);

    internal static SelectionTextReader.Result? Read(IntPtr window, int[]? eventId, bool manual, bool allowSelf, NativeSelectionState? previous, out bool protectedInput)
    {
        Span<char> text = stackalloc char[TranslationInput.MaxCharacters + 1];
        Span<double> bounds = stackalloc double[4];
        bool retained = false;
        try
        {
            previous?.DangerousAddRef(ref retained);
            int status = vn_selection_read(window, eventId, eventId?.Length ?? 0, NativeSelectionReader.FocusedControl(window),
                retained ? previous!.DangerousGetHandle().ToInt64() : 0, manual ? (byte)1 : (byte)0, allowSelf ? (byte)1 : (byte)0,
                TranslationInput.MaxCharacters, text, text.Length, out int length, bounds, out byte isProtected, out byte editable, out byte truncated, out long handle);
            protectedInput = isProtected != 0;
            if (status != 0 || handle == 0) return null;
            var owner = new NativeSelectionState(handle);
            try
            {
                return new(TranslationInput.Prefix(new string(text[..length]), TranslationInput.MaxCharacters),
                    new Rect(bounds[0], bounds[1], bounds[2], bounds[3]), owner, editable != 0) { InputTruncated = truncated != 0 };
            }
            catch { owner.Dispose(); throw; }
        }
        finally { if (retained) previous!.DangerousRelease(); }
    }

    internal bool SameSelection(NativeSelectionState other) => vn_selection_same(this, other, out byte equal) == 0 && equal != 0;
    internal unsafe int PrepareReplace(string expected, string replacement, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        int[] cancelled = [0];
        using var registration = cancellation.UnsafeRegister(static state => Volatile.Write(ref ((int[])state!)[0], 1), cancelled);
        fixed (int* flag = cancelled)
            return vn_selection_prepare_replace(this, expected, expected.Length, replacement, replacement.Length, flag, out int outcome) == 0 ? outcome : 0;
    }
    protected override bool ReleaseHandle() => vn_selection_close(handle.ToInt64()) == 0;
}
