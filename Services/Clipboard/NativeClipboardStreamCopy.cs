using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VNotch.Interop;

namespace VNotch.Services.Clipboard;

internal static unsafe partial class NativeClipboardStreamCopy
{
    private const int QuotaExceededStatus = 1;

    private sealed class Context(Stream source, Stream destination)
    {
        internal readonly Stream Source = source;
        internal readonly Stream Destination = destination;
        internal ExceptionDispatchInfo? Error;
    }

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_clipboard_stream_copy(
        nint source,
        nint destination,
        delegate* unmanaged[Stdcall]<nint, byte*, int, int> read,
        delegate* unmanaged[Stdcall]<nint, byte*, int, int> write,
        nint context,
        long maximum,
        out long written,
        out int error);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Read(nint handle, byte* bytes, int count)
    {
        if (GCHandle.FromIntPtr(handle).Target is not Context context)
        {
            return -1;
        }

        try
        {
            return context.Source.Read(new Span<byte>(bytes, count));
        }
        catch (Exception error)
        {
            context.Error = ExceptionDispatchInfo.Capture(error);
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Write(nint handle, byte* bytes, int count)
    {
        if (GCHandle.FromIntPtr(handle).Target is not Context context)
        {
            return -1;
        }

        try
        {
            context.Destination.Write(new ReadOnlySpan<byte>(bytes, count));
            return 0;
        }
        catch (Exception error)
        {
            context.Error = ExceptionDispatchInfo.Capture(error);
            return -1;
        }
    }

    internal static long Copy(Stream source, Stream destination, long maximum, bool archiveExtraction = false)
    {
        var context = new Context(source, destination);
        var owner = GCHandle.Alloc(context);
        SafeFileHandle? sourceHandle = null;
        SafeFileHandle? destinationHandle = null;
        bool sourceRef = false;
        bool destinationRef = false;
        long sourcePosition = 0;
        long destinationPosition = 0;
        long written = 0;
        try
        {
            if (source is FileStream { IsAsync: false } sourceFile)
            {
                sourcePosition = sourceFile.Position;
                sourceHandle = sourceFile.SafeFileHandle;
                sourceHandle.DangerousAddRef(ref sourceRef);
            }

            if (destination is FileStream { IsAsync: false } destinationFile)
            {
                destinationPosition = destinationFile.Position;
                destinationHandle = destinationFile.SafeFileHandle;
                destinationHandle.DangerousAddRef(ref destinationRef);
            }

            int status = vn_clipboard_stream_copy(
                sourceHandle?.DangerousGetHandle() ?? 0,
                destinationHandle?.DangerousGetHandle() ?? 0,
                &Read,
                &Write,
                GCHandle.ToIntPtr(owner),
                maximum,
                out written,
                out int error);

            context.Error?.Throw();
            if (status == QuotaExceededStatus)
            {
                if (archiveExtraction)
                {
                    throw new IOException("Archive extraction quota exceeded.");
                }

                throw new ClipboardImportLimitException();
            }

            if (status != (int)NativeStatus.Ok)
            {
                throw new IOException("Could not copy the clipboard attachment.", new Win32Exception(error));
            }

            return written;
        }
        finally
        {
            // Keep managed FileStream positions aligned with OS file handles even on partial/aborted copies.
            if (sourceRef)
            {
                sourceHandle!.DangerousRelease();
                if (written > 0)
                {
                    source.Position = checked(sourcePosition + written);
                }
            }

            if (destinationRef)
            {
                destinationHandle!.DangerousRelease();
                if (written > 0)
                {
                    destination.Position = checked(destinationPosition + written);
                }
            }

            owner.Free();
        }
    }
}
