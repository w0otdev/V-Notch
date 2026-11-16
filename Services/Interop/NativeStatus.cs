namespace VNotch.Services.Interop;

// Mirrors Native/vnotch_abi.h; never use this table for HRESULT-returning APIs.
internal enum NativeStatus
{
    Ok = 0,
    InvalidArgument = -1,
    BufferTooSmall = -2,
    Failure = -3
}

internal static class NativeResult
{
    internal static void RequireSuccess(int status, string operation)
    {
        if (status != (int)NativeStatus.Ok)
            throw new InvalidOperationException($"{operation} failed: {(NativeStatus)status} ({status}).");
    }
}
