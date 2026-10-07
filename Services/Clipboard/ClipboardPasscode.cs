using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace VNotch.Services.Clipboard;

internal static class ClipboardPasscode
{
    internal const int Length = 6;

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
            return TryCopyDigits(first, left) && TryCopyDigits(second, right)
                && CryptographicOperations.FixedTimeEquals(left, right);
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
            for (int i = 0; i < Length; i++)
            {
                char digit = (char)Marshal.ReadInt16(buffer, i * sizeof(char));
                if (digit is < '0' or > '9') { destination.Clear(); return false; }
                destination[i] = (byte)digit;
            }
            return true;
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(buffer); }
    }
}
