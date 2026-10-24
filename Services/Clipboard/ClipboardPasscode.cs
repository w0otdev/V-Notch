using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace VNotch.Services.Clipboard;

internal static partial class ClipboardPasscode
{
    private const string NativeDll = "VNotch.Native.dll";
    internal const int Length = 6;

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_passcode_try_copy_digits(
        nint unicodePtr,
        int pinLen,
        Span<byte> outDigits,
        int destLen,
        out byte outOk);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_passcode_fixed_time_equals(
        ReadOnlySpan<byte> leftPtr,
        ReadOnlySpan<byte> rightPtr,
        int len,
        out byte outEqual);

    internal static bool IsValid(SecureString pin)
    {
        Span<byte> digits = stackalloc byte[Length];
        try { return TryCopyDigits(pin, digits); }
        finally { CryptographicOperations.ZeroMemory(digits); }
    }

    internal static bool Matches(SecureString first, SecureString second)
    {
        Span<byte> left = stackalloc byte[Length];
        Span<byte> right = stackalloc byte[Length];
        try
        {
            if (!TryCopyDigits(first, left) || !TryCopyDigits(second, right))
                return false;
            vn_clipboard_passcode_fixed_time_equals(left, right, Length, out byte eq);
            return eq != 0;
        }
        finally { CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right); }
    }

    internal static bool TryCopyDigits(SecureString pin, Span<byte> destination)
    {
        destination.Clear();
        if (pin.Length != Length || destination.Length != Length) return false;
        IntPtr buffer = Marshal.SecureStringToGlobalAllocUnicode(pin);
        try
        {
            vn_clipboard_passcode_try_copy_digits(buffer, pin.Length, destination, destination.Length, out byte ok);
            return ok != 0;
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(buffer); }
    }
}
