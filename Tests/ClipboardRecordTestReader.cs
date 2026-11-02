using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using VNotch.Services.Clipboard;

namespace VNotch.Tests;

internal static class ClipboardRecordTestReader
{
    internal static T Read<T>(string root, string path)
    {
        using var records = new NativeClipboardRecordStore(root);
        byte[] bytes = records.Read(path) ?? File.ReadAllBytes(path);
        try { return JsonSerializer.Deserialize<T>(bytes)!; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
