using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

public static partial class DataProtection
{
    private const string Prefix = "enc:";
    private const int VnOk = 0;
    private const int VnErrBufferTooSmall = -2;
    private const int DpapiOverheadEstimate = 512;

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dpapi_protect_bytes(
        byte* plainPtr, int plainLen, byte* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dpapi_unprotect_bytes(
        byte* encPtr, int encLen, byte* outBuf, int outCap, out int outLen);

    internal static Func<byte[], byte[]> ProtectBytes { get; set; } = DefaultProtectBytes;

    private static byte[] DefaultProtectBytes(byte[] data) => ProtectBytesNative(data);

    internal static unsafe byte[] ProtectBytesNative(ReadOnlySpan<byte> plainBytes)
    {
        int initialCap = checked(plainBytes.Length + DpapiOverheadEstimate);
        byte[] rented = ArrayPool<byte>.Shared.Rent(initialCap);
        try
        {
            fixed (byte* pPlain = plainBytes)
            fixed (byte* pRented = rented)
            {
                byte dummy = 0;
                byte* pIn = plainBytes.IsEmpty ? &dummy : pPlain;
                int rc = vn_dpapi_protect_bytes(pIn, plainBytes.Length, pRented, rented.Length, out int reqLen);
                if (rc == VnOk)
                {
                    return rented.AsSpan(0, reqLen).ToArray();
                }
                if (rc != VnErrBufferTooSmall || reqLen < 0)
                {
                    throw new CryptographicException("DPAPI encryption failed.");
                }

                byte[] exact = new byte[reqLen];
                fixed (byte* pExact = exact)
                {
                    byte* pOut = reqLen == 0 ? &dummy : pExact;
                    rc = vn_dpapi_protect_bytes(pIn, plainBytes.Length, pOut, reqLen, out int writtenLen);
                    if (rc != VnOk)
                        throw new CryptographicException("DPAPI encryption failed.");
                    return exact.Length == writtenLen ? exact : exact.AsSpan(0, writtenLen).ToArray();
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static unsafe byte[] UnprotectBytesNative(ReadOnlySpan<byte> cipherBytes)
    {
        if (cipherBytes.IsEmpty)
            throw new CryptographicException("DPAPI decryption failed.");

        byte[] rented = ArrayPool<byte>.Shared.Rent(cipherBytes.Length);
        try
        {
            fixed (byte* pCipher = cipherBytes)
            fixed (byte* pRented = rented)
            {
                int rc = vn_dpapi_unprotect_bytes(pCipher, cipherBytes.Length, pRented, rented.Length, out int reqLen);
                if (rc == VnOk)
                {
                    return rented.AsSpan(0, reqLen).ToArray();
                }
                if (rc != VnErrBufferTooSmall || reqLen < 0)
                {
                    throw new CryptographicException("DPAPI decryption failed.");
                }

                byte[] exact = new byte[reqLen];
                fixed (byte* pExact = exact)
                {
                    byte dummy = 0;
                    byte* pOut = reqLen == 0 ? &dummy : pExact;
                    rc = vn_dpapi_unprotect_bytes(pCipher, cipherBytes.Length, pOut, reqLen, out int writtenLen);
                    if (rc != VnOk)
                        throw new CryptographicException("DPAPI decryption failed.");
                    return exact.Length == writtenLen ? exact : exact.AsSpan(0, writtenLen).ToArray();
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] encrypted = ProtectBytes(data);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception)
        {
            RuntimeLog.Warn("DPAPI", "Protect failed.");
            throw new CryptographicException("DPAPI encryption failed.");
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Data is not DPAPI-encrypted (missing 'enc:' prefix). Use TryMigrateFromPlaintext for legacy data.");

        try
        {
            byte[] encrypted = Convert.FromBase64String(stored.Substring(Prefix.Length));
            byte[] data = UnprotectBytesNative(encrypted);
            try { return Encoding.UTF8.GetString(data); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("DPAPI encrypted data is not valid Base64.", ex);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DPAPI", "Unprotect failed.");
            throw new CryptographicException("DPAPI decryption failed.", ex);
        }
    }

    public static bool IsEncrypted(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public static string? TryProtect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            return Protect(plaintext);
        }
        catch
        {
            return null;
        }
    }
}
