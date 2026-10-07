using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Clipboard;
internal sealed class ClipboardVault : IDisposable
{
    private const int Iterations = 600_000;
    private const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private readonly string _configurationPath;
    private readonly Func<DateTime> _utcNow;
    private byte[]? _key;
    private int _failedAttempts;
    private DateTime _retryAfterUtc;
    private sealed record Configuration(int Version, byte[] Salt, string Pepper, byte[] Verification);
    internal bool IsConfigured => File.Exists(_configurationPath);
    internal bool IsUnlocked => _key != null;

    internal ClipboardVault(string root, Func<DateTime>? utcNow = null)
    {
        _configurationPath = Path.Combine(root, "personal-key.json");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal bool Unlock(SecureString pin, bool create)
        => UnlockWithResult(pin, create).Succeeded;

    internal ClipboardUnlockResult UnlockWithResult(SecureString pin, bool create)
    {
        if (_utcNow() < _retryAfterUtc) return new(ClipboardUnlockStatus.RateLimited, _retryAfterUtc);
        Span<byte> digits = stackalloc byte[ClipboardPasscode.Length];
        try
        {
            return ClipboardPasscode.TryCopyDigits(pin, digits)
                ? Unlock(digits, create) : new(ClipboardUnlockStatus.InvalidPin);
        }
        finally { CryptographicOperations.ZeroMemory(digits); }
    }

    private ClipboardUnlockResult Unlock(ReadOnlySpan<byte> pin, bool create)
    {
        if (!IsConfigured && !create) return new(ClipboardUnlockStatus.InvalidPin);
        byte[]? candidate = null;
        try
        {
            var config = IsConfigured
                ? JsonSerializer.Deserialize<Configuration>(File.ReadAllText(_configurationPath)) ?? throw new CryptographicException("Invalid Personal configuration.")
                : new Configuration(1, RandomNumberGenerator.GetBytes(32), CreateProtectedPepper(), []);
            if (config.Version != 1 || config.Salt is not { Length: 32 } || string.IsNullOrWhiteSpace(config.Pepper)) throw new CryptographicException();
            byte[] pepper = DecodePepper(config.Pepper);
            byte[]? password = null;
            try
            {
                // Preserve v1's UTF-8 PIN + Base64 pepper format without creating a PIN string.
                password = new byte[pin.Length + pepper.Length];
                pin.CopyTo(password);
                pepper.CopyTo(password, pin.Length);
                candidate = Rfc2898DeriveBytes.Pbkdf2(password, config.Salt, Iterations, HashAlgorithmName.SHA256, 32);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pepper);
                if (password != null) CryptographicOperations.ZeroMemory(password);
            }
            if (IsConfigured)
            {
                byte[] verification = Decrypt(config.Verification, candidate);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(verification, "VNotch.Personal.v1"u8)) throw new CryptographicException();
                }
                finally { CryptographicOperations.ZeroMemory(verification); }
            }
            else
            {
                config = config with { Verification = Encrypt("VNotch.Personal.v1"u8, candidate) };
                AtomicWrite(_configurationPath, JsonSerializer.SerializeToUtf8Bytes(config));
            }
            Lock();
            _key = candidate;
            candidate = null;
            _failedAttempts = 0;
            _retryAfterUtc = default;
            return new(ClipboardUnlockStatus.Success);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            _failedAttempts++;
            _retryAfterUtc = _utcNow().AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, _failedAttempts - 1))));
            return new(ClipboardUnlockStatus.InvalidPin, _retryAfterUtc);
        }
        finally { if (candidate != null) CryptographicOperations.ZeroMemory(candidate); }
    }

    private static string CreateProtectedPepper()
    {
        Span<byte> random = stackalloc byte[32];
        byte[] pepper = new byte[44];
        try
        {
            RandomNumberGenerator.Fill(random);
            Base64.EncodeToUtf8(random, pepper, out _, out _);
            return "enc:" + Convert.ToBase64String(ProtectedData.Protect(pepper, null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(random); CryptographicOperations.ZeroMemory(pepper); }
    }

    /// <summary>
    /// Decodes a pepper field from its stored format.
    /// Supports "enc:" (DPAPI-protected) and "raw:" (portable base64).
    /// Throws <see cref="CryptographicException"/> for unknown or malformed formats.
    /// </summary>
    private static byte[] DecodePepper(string pepper)
    {
        if (pepper.StartsWith("enc:", StringComparison.Ordinal))
            return ProtectedData.Unprotect(Convert.FromBase64String(pepper[4..]), null, DataProtectionScope.CurrentUser);
        if (pepper.StartsWith("raw:", StringComparison.Ordinal))
            return Convert.FromBase64String(pepper[4..]);
        throw new CryptographicException("Unrecognised pepper format. Expected 'enc:' or 'raw:' prefix.");
    }

    /// <summary>
    /// Exports a portable version of the vault configuration by replacing any DPAPI-protected
    /// pepper with its machine-independent "raw:" equivalent. The exported config can be copied
    /// to another machine; the user's PIN still protects the vault.
    /// Returns null if the vault is not yet configured.
    /// </summary>
    internal string? ExportPortableConfig()
    {
        if (!IsConfigured) return null;
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(_configurationPath))
            ?? throw new CryptographicException("Invalid Personal configuration.");
        if (!config.Pepper.StartsWith("enc:", StringComparison.Ordinal)) return File.ReadAllText(_configurationPath);
        byte[] raw = ProtectedData.Unprotect(Convert.FromBase64String(config.Pepper[4..]), null, DataProtectionScope.CurrentUser);
        try
        {
            var portable = config with { Pepper = "raw:" + Convert.ToBase64String(raw) };
            return JsonSerializer.Serialize(portable);
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    /// <summary>
    /// Imports a portable vault configuration (e.g. from <see cref="ExportPortableConfig"/>)
    /// and writes it to disk. On the new machine the pepper will remain as "raw:" (portable).
    /// Overwrites any existing configuration — call only when the user explicitly requests a restore.
    /// </summary>
    internal void ImportPortableConfig(string json)
    {
        var config = JsonSerializer.Deserialize<Configuration>(json)
            ?? throw new CryptographicException("Invalid portable configuration.");
        if (config.Version != 1 || config.Salt is not { Length: 32 } || string.IsNullOrWhiteSpace(config.Pepper))
            throw new CryptographicException("Portable configuration is malformed.");
        // Validate that the pepper field is decodable before writing anything.
        byte[] test = DecodePepper(config.Pepper);
        CryptographicOperations.ZeroMemory(test);
        AtomicWrite(_configurationPath, System.Text.Encoding.UTF8.GetBytes(json));
    }

    internal byte[] Seal(ReadOnlySpan<byte> data) => Encrypt(data, Key);
    internal void Reset()
    {
        Lock();
        File.Delete(_configurationPath);
        _failedAttempts = 0;
        _retryAfterUtc = default;
    }
    internal byte[] Open(ReadOnlySpan<byte> data) => Decrypt(data, Key);
    private byte[] Key => _key ?? throw new InvalidOperationException("Personal is locked.");

    private static byte[] Encrypt(ReadOnlySpan<byte> data, byte[] key)
    {
        byte[] envelope = new byte[12 + TagSize + data.Length];
        RandomNumberGenerator.Fill(envelope.AsSpan(0, 12));
        using var cipher = new AesGcm(key, TagSize);
        cipher.Encrypt(envelope.AsSpan(0, 12), data, envelope.AsSpan(28), envelope.AsSpan(12, TagSize));
        return envelope;
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> data, byte[] key)
    {
        if (data.Length < 28) throw new CryptographicException("Invalid Personal data.");
        byte[] result = new byte[data.Length - 28];
        try
        {
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(data[..12], data[28..], data.Slice(12, TagSize), result);
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }

    // ZIP writers do not know their final length. Spool only ciphertext under a fresh,
    // memory-only key, then stream it into the existing authenticated vault format.
    // The scratch file stays exclusively open until both encryption passes finish.
    internal void WriteEncryptedFile(string destination, Func<Stream, long> write)
    {
        _ = Key;
        string temporary = destination + ".staging.enc";
        try
        {
            using var cipher = Aes.Create();
            using var scratch = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            long length;
            using (var encryptor = cipher.CreateEncryptor())
            using (var encrypted = new CryptoStream(scratch, encryptor, CryptoStreamMode.Write, leaveOpen: true))
                length = write(encrypted);
            scratch.Position = 0;
            using var decryptor = cipher.CreateDecryptor();
            using var decrypted = new CryptoStream(scratch, decryptor, CryptoStreamMode.Read, leaveOpen: true);
            using var input = new LengthReadStream(decrypted, length);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            TransformFile(input, output, true);
            output.Flush(true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class LengthReadStream(Stream input, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => input.Read(buffer);
        public override void Flush() => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Every chunk authenticates its position and total length, preventing truncation/reordering.
    internal void TransformFile(string source, string destination, bool encrypt)
    {
        using var input = File.OpenRead(source);
        string temporary = destination + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                TransformFile(input, output, encrypt);
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
    }

    internal void TransformFile(Stream input, Stream output, bool encrypt)
    {
        using var cipher = new AesGcm(Key, TagSize);
        byte[] buffer = new byte[ChunkSize];
        byte[] transformed = new byte[ChunkSize];
        byte[] nonce = new byte[12];
        byte[] tag = new byte[TagSize];
        Span<byte> header = stackalloc byte[16];
        long length;
        if (encrypt)
        {
            length = input.Length;
            "VNC1"u8.CopyTo(header);
            RandomNumberGenerator.Fill(header.Slice(4, 4));
            BinaryPrimitives.WriteInt64LittleEndian(header[8..], length);
            output.Write(header);
        }
        else
        {
            input.ReadExactly(header);
            if (!header[..4].SequenceEqual("VNC1"u8)) throw new CryptographicException("Invalid encrypted attachment.");
            length = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
            if (length < 0 || length > input.Length || input.Length != 16 + length + Math.Max(1, (length + ChunkSize - 1) / ChunkSize) * 28)
                throw new CryptographicException("Invalid encrypted attachment length.");
        }
        Span<byte> associated = stackalloc byte[24];
        header.CopyTo(associated);
        try
        {
            long position = 0;
            do
            {
                int count = (int)Math.Min(ChunkSize, length - position);
                BinaryPrimitives.WriteInt64LittleEndian(associated[16..], position);
                if (encrypt)
                {
                    input.ReadExactly(buffer.AsSpan(0, count));
                    RandomNumberGenerator.Fill(nonce);
                    cipher.Encrypt(nonce, buffer.AsSpan(0, count), transformed.AsSpan(0, count), tag, associated);
                    output.Write(nonce); output.Write(tag); output.Write(transformed, 0, count);
                }
                else
                {
                    input.ReadExactly(nonce); input.ReadExactly(tag); input.ReadExactly(buffer.AsSpan(0, count));
                    cipher.Decrypt(nonce, buffer.AsSpan(0, count), tag, transformed.AsSpan(0, count), associated);
                    output.Write(transformed, 0, count);
                }
                position += count;
            } while (position < length);
            output.Flush();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); CryptographicOperations.ZeroMemory(transformed); }
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }

    internal void Lock()
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }
    public void Dispose() => Lock();
}
