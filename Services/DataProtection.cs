using System;
using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

public static class DataProtection
{
    private const string Prefix = "enc:";
    internal static Func<byte[], byte[]> ProtectBytes { get; set; } = data =>
        ProtectedData.Protect(data, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectBytes(data);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            // Never log the value that was submitted for protection.
            RuntimeLog.Warn("DPAPI", "Protect failed.");
            throw new CryptographicException("DPAPI encryption failed.", ex);
        }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Data is not DPAPI-encrypted (missing 'enc:' prefix). Use TryMigrateFromPlaintext for legacy data.");

        try
        {
            byte[] encrypted = Convert.FromBase64String(stored.Substring(Prefix.Length));
            byte[] data = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
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
