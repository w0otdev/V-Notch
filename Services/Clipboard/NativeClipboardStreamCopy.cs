using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services.Clipboard;

internal static unsafe partial class NativeClipboardStreamCopy
{
    private sealed class Context(Stream source, Stream destination)
    {
        internal readonly Stream Source = source, Destination = destination;
        internal ExceptionDispatchInfo? Error;
    }
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_clipboard_stream_copy(nint source, nint destination,
        delegate* unmanaged<nint, byte*, int, int> read, delegate* unmanaged<nint, byte*, int, int> write,
        nint context, long maximum, out long written, out int error);
    [UnmanagedCallersOnly]
    private static int Read(nint handle, byte* bytes, int count)
    {
        var context = (Context)GCHandle.FromIntPtr(handle).Target!;
        try { return context.Source.Read(new Span<byte>(bytes, count)); }
        catch (Exception error) { context.Error = ExceptionDispatchInfo.Capture(error); return -1; }
    }
    [UnmanagedCallersOnly]
    private static int Write(nint handle, byte* bytes, int count)
    {
        var context = (Context)GCHandle.FromIntPtr(handle).Target!;
        try { context.Destination.Write(new ReadOnlySpan<byte>(bytes, count)); return 0; }
        catch (Exception error) { context.Error = ExceptionDispatchInfo.Capture(error); return -1; }
    }
    internal static long Copy(Stream source, Stream destination, long maximum, bool archiveExtraction = false)
    {
        var context = new Context(source, destination);
        var owner = GCHandle.Alloc(context);
        SafeFileHandle? sourceHandle = null, destinationHandle = null;
        bool sourceRef = false, destinationRef = false;
        long sourcePosition = 0, destinationPosition = 0;
        try
        {
            if (source is FileStream { IsAsync: false } sourceFile) { sourcePosition = sourceFile.Position; sourceHandle = sourceFile.SafeFileHandle; sourceHandle.DangerousAddRef(ref sourceRef); }
            if (destination is FileStream { IsAsync: false } destinationFile) { destinationPosition = destinationFile.Position; destinationHandle = destinationFile.SafeFileHandle; destinationHandle.DangerousAddRef(ref destinationRef); }
            int status = vn_clipboard_stream_copy(sourceHandle?.DangerousGetHandle() ?? 0, destinationHandle?.DangerousGetHandle() ?? 0,
                &Read, &Write, GCHandle.ToIntPtr(owner), maximum, out long written, out int error);
            context.Error?.Throw();
            if (sourceHandle != null) source.Position = checked(sourcePosition + written);
            if (destinationHandle != null) destination.Position = checked(destinationPosition + written);
            if (status == 1)
            {
                if (archiveExtraction) throw new IOException("Archive extraction quota exceeded.");
                throw new ClipboardImportLimitException();
            }
            if (status != 0) throw new IOException("Could not copy the clipboard attachment.", new Win32Exception(error));
            return written;
        }
        finally
        {
            if (sourceRef) sourceHandle!.DangerousRelease();
            if (destinationRef) destinationHandle!.DangerousRelease();
            owner.Free();
        }
    }
}
