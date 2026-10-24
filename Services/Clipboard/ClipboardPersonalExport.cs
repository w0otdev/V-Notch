using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services.Clipboard;

/// <summary>Every plaintext file has a Windows delete-on-close handle before its first byte is written.</summary>
internal sealed class ClipboardPersonalExport : IDisposable
{
    private readonly List<FileStream> _leases = [];

    internal FileStream WriteFile(string path, Action<Stream> write)
    {
        // Create the deletion lease with read access so recipients need not share write access.
        // CREATE_NEW with DELETE_ON_CLOSE | TEMPORARY, opened by the native layer.
        var handle = Win32Interop.CreateDeleteOnCloseFile(path);
        if (handle == null || handle.IsInvalid)
        {
            handle?.Dispose();
            throw new IOException("Cannot create a protected temporary export.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        var lease = new FileStream(handle, FileAccess.Read);
        _leases.Add(lease);
        using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete))
        { write(output); output.Flush(true); }
        return lease;
    }

    public void Dispose()
    {
        foreach (var lease in _leases) lease.Dispose();
        _leases.Clear();
    }

}
