using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

public static partial class DataProtection
{
    private const string Prefix = "enc:";

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dpapi_protect_bytes(
        byte* plainPtr, int plainLen, byte* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dpapi_unprotect_bytes(
        byte* encPtr, int encLen, byte* outBuf, int outCap, out int outLen);

    internal static Func<byte[], byte[]> ProtectBytes { get; set; } = DefaultProtectBytes;

    private static unsafe byte[] DefaultProtectBytes(byte[] data)
    {
        fixed (byte* pIn = data)
        {
            int status = vn_dpapi_protect_bytes(pIn, data.Length, null, 0, out int reqLen);
            if (status != 0 && status != -2)
                throw new CryptographicException("DPAPI encryption failed.");

            byte[] outBuf = new byte[reqLen];
            fixed (byte* pOut = outBuf)
            {
                status = vn_dpapi_protect_bytes(pIn, data.Length, pOut, reqLen, out _);
                if (status != 0)
                    throw new CryptographicException("DPAPI encryption failed.");
            }
            return outBuf;
        }
    }

    private static unsafe byte[] UnprotectBytesNative(byte[] encrypted)
    {
        fixed (byte* pIn = encrypted)
        {
            int status = vn_dpapi_unprotect_bytes(pIn, encrypted.Length, null, 0, out int reqLen);
            if (status != 0 && status != -2)
                throw new CryptographicException("DPAPI decryption failed.");

            byte[] outBuf = new byte[reqLen];
            fixed (byte* pOut = outBuf)
            {
                status = vn_dpapi_unprotect_bytes(pIn, encrypted.Length, pOut, reqLen, out _);
                if (status != 0)
                    throw new CryptographicException("DPAPI decryption failed.");
            }
            return outBuf;
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
