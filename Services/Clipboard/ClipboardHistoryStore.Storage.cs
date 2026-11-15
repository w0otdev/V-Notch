using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

/// <summary>Owns immutable local copies, retention, deduplication and the Personal vault.
/// Disk work is serialized off the UI thread; full-text search uses a bounded memory cache.</summary>
public sealed partial class ClipboardHistoryStore : IDisposable, IAsyncDisposable
{
    private string EntryDirectory(ClipboardEntry entry) => Path.Combine(_root, entry.IsPersonal ? "personal" : "items", entry.Id.ToString("N"));
    private ClipboardContent ReadContent(ClipboardEntry entry) =>
        ReadDocument<ClipboardContent>(Path.Combine(EntryDirectory(entry), "content.json"), entry.IsPersonal) with { Entry = entry };
    private T ReadDocument<T>(string path, bool personal)
    {
        string target = path + (personal ? ".enc" : "");
        if (!personal)
        {
            if (PublicRecords.TryReadJson<T>(target, out var value))
                return value ?? throw new JsonException("Invalid clipboard record.");
            using var stream = File.OpenRead(target);
            return JsonSerializer.Deserialize<T>(stream) ?? throw new JsonException("Invalid clipboard record.");
        }
        using (var input = File.OpenRead(target))
        {
            if (input.Length >= 44)
            {
                Span<byte> magic = stackalloc byte[4];
                if (input.Read(magic) == 4 &&
                    vn_clipboard_store_is_vault_stream_magic(magic, 4, out byte isMagic) == 0 &&
                    isMagic != 0)
                {
                    input.Position = 0;
                    using var plainStream = new NativeBufferStream();
                    _vault.TransformFile(input, plainStream, encrypt: false);
                    plainStream.Position = 0;
                    return JsonSerializer.Deserialize<T>(plainStream) ?? throw new JsonException("Invalid clipboard record.");
                }
            }
        }
        byte[] bytes = File.ReadAllBytes(target);
        bytes = _vault.Open(bytes);
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new JsonException("Invalid clipboard record."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void WriteDocument<T>(string path, T value, bool personal)
    {
        string target = path + (personal ? ".enc" : "");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!personal)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
            try { PublicRecords.Append(target, payload); }
            finally { CryptographicOperations.ZeroMemory(payload); }
            return;
        }
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var plainStream = new NativeBufferStream();
            JsonSerializer.Serialize(plainStream, value);
            plainStream.Position = 0;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                _vault.TransformFile(plainStream, output, encrypt: true);
                output.Flush(true);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }
    private void WriteEntry(ClipboardEntry entry) => WriteDocument(Path.Combine(EntryDirectory(entry), "entry.json"), entry, entry.IsPersonal);

}
