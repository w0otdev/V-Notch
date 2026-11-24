using System.Runtime.InteropServices;

namespace VNotch.Services;

// A native callback needs a GC root during registration, even if it fires before the
// registration returns. Transfer ownership to the installed hook only after success.
internal sealed class PendingCallbackHandle(object target) : IDisposable
{
    private GCHandle _handle = GCHandle.Alloc(target);
    internal nint Context => GCHandle.ToIntPtr(_handle);

    internal GCHandle Transfer()
    {
        var handle = _handle;
        _handle = default;
        return handle;
    }

    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
