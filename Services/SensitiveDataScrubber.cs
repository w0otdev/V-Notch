using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public static partial class SensitiveDataScrubber
{
    private const string NativeDll = "VNotch.Native.dll";
    private const int VN_ERR_BUFFER_TOO_SMALL = -2;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_sensitive_data_scrub(
        ReadOnlySpan<char> inputPtr,
        int inputLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outChanged);

    public static string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        ReadOnlySpan<char> span = input.AsSpan();
        int cap = Math.Max(128, span.Length + 64);
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        int rc = vn_sensitive_data_scrub(span, span.Length, buf, buf.Length, out int outLen, out byte changed);
        if (rc == VN_ERR_BUFFER_TOO_SMALL && outLen > buf.Length)
        {
            buf = new char[outLen];
            rc = vn_sensitive_data_scrub(span, span.Length, buf, buf.Length, out outLen, out changed);
        }

        if (rc != 0 || changed == 0)
            return input;

        return outLen > 0 ? new string(buf[..outLen]) : string.Empty;
    }
}
