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
    private static ReadOnlySpan<byte> PublicBlobMagic => "VNPBLOB1"u8;

    private static readonly byte[] s_publicBlobEntropy = "VNotch.PublicBlob.v1"u8.ToArray();
    private const int PublicBlobHeaderSize = 20;
    private const int PublicBlobChunkSize = 64 * 1024;
    private const int PublicBlobTagSize = 16;
    private const int PublicBlobDiskChunkSize = PublicBlobChunkSize + PublicBlobTagSize;

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_blob_build_nonce_aad(
        ReadOnlySpan<byte> streamIdPtr,
        int streamIdLen,
        ulong chunkIndex,
        int plainLen,
        byte isFinal,
        Span<byte> outNonce,
        int nonceCap,
        Span<byte> outAad,
        int aadCap);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_blob_compute_geometry(
        long fileLen,
        out long outTotalChunks,
        out int outLastPlainLen,
        out long outPlainLength,
        out int outErrorKind);

    internal static void BuildPublicBlobNonceAndAad(
        ReadOnlySpan<byte> streamId,
        ulong chunkIndex,
        int plainLen,
        bool isFinal,
        Span<byte> nonce,
        Span<byte> aad)
    {
        vn_clipboard_blob_build_nonce_aad(
            streamId, streamId.Length,
            chunkIndex, plainLen, isFinal ? (byte)1 : (byte)0,
            nonce, nonce.Length,
            aad, aad.Length);
    }

    private byte[] GetPublicBlobKey(bool allowCreate)
    {
        lock (_publicBlobKeyLock)
        {
            if (_publicBlobKey != null) return _publicBlobKey;
            string keyPath = Path.Combine(_root, ".public-blob-key.dpapi");
            if (File.Exists(keyPath))
            {
                byte[] existing = File.ReadAllBytes(keyPath);
                if (existing.Length > 0)
                {
                    byte[] unprotected;
                    try
                    {
                        unprotected = ProtectedData.Unprotect(existing, s_publicBlobEntropy, DataProtectionScope.CurrentUser);
                    }
                    catch (CryptographicException ex)
                    {
                        throw new IOException("Could not unprotect public clipboard blob key.", ex);
                    }
                    if (unprotected.Length != 32)
                    {
                        CryptographicOperations.ZeroMemory(unprotected);
                        throw new IOException("Invalid public clipboard blob key length.");
                    }
                    _publicBlobKey = unprotected;
                    return _publicBlobKey;
                }
                if (!allowCreate)
                    throw new IOException("Public clipboard blob key is empty.");
                File.SetAttributes(keyPath, FileAttributes.Normal);
                File.Delete(keyPath);
            }
            if (!allowCreate)
                throw new IOException("Missing public clipboard blob key.");
            Directory.CreateDirectory(_root);
            byte[] created = RandomNumberGenerator.GetBytes(32);
            string temp = $"{keyPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                byte[] protectedBytes = ProtectedData.Protect(created, s_publicBlobEntropy, DataProtectionScope.CurrentUser);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(protectedBytes);
                    stream.Flush(true);
                }
                try
                {
                    File.Move(temp, keyPath, overwrite: false);
                    try { File.SetAttributes(keyPath, FileAttributes.Hidden); } catch { }
                    _publicBlobKey = created;
                    created = [];
                    return _publicBlobKey;
                }
                catch (IOException) when (File.Exists(keyPath))
                {
                    byte[] winner;
                    try
                    {
                        winner = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), s_publicBlobEntropy, DataProtectionScope.CurrentUser);
                    }
                    catch (CryptographicException ex)
                    {
                        throw new IOException("Could not unprotect public clipboard blob key.", ex);
                    }
                    if (winner.Length != 32)
                    {
                        CryptographicOperations.ZeroMemory(winner);
                        throw new IOException("Invalid public clipboard blob key length.");
                    }
                    _publicBlobKey = winner;
                    return _publicBlobKey;
                }
            }
            finally
            {
                if (created.Length > 0) CryptographicOperations.ZeroMemory(created);
                if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
            }
        }
    }

    private void WritePublicBlobBytes(string destination, ReadOnlySpan<byte> plain)
    {
        byte[] key = GetPublicBlobKey(allowCreate: true);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = $"{destination}.{Guid.NewGuid():N}.tmp";
        byte[]? cipherBuf = null;
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var cipher = new AesGcm(key, PublicBlobTagSize))
            {
                Span<byte> header = stackalloc byte[PublicBlobHeaderSize];
                PublicBlobMagic.CopyTo(header);
                RandomNumberGenerator.Fill(header[8..]);
                output.Write(header);
                ReadOnlySpan<byte> streamId = header[8..];
                Span<byte> nonce = stackalloc byte[12];
                Span<byte> aad = stackalloc byte[33];
                Span<byte> tag = stackalloc byte[PublicBlobTagSize];
                if (plain.IsEmpty)
                {
                    BuildPublicBlobNonceAndAad(streamId, 0, 0, isFinal: true, nonce, aad);
                    cipher.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, aad);
                    output.Write(tag);
                }
                else
                {
                    cipherBuf = ArrayPool<byte>.Shared.Rent(Math.Min(PublicBlobChunkSize, plain.Length));
                    ulong chunkIndex = 0;
                    int offset = 0;
                    while (offset < plain.Length)
                    {
                        int chunkLen = Math.Min(PublicBlobChunkSize, plain.Length - offset);
                        bool isFinal = offset + chunkLen == plain.Length;
                        BuildPublicBlobNonceAndAad(streamId, chunkIndex++, chunkLen, isFinal, nonce, aad);
                        Span<byte> dst = cipherBuf.AsSpan(0, chunkLen);
                        cipher.Encrypt(nonce, plain.Slice(offset, chunkLen), dst, tag, aad);
                        output.Write(tag);
                        output.Write(dst);
                        offset += chunkLen;
                    }
                }
                output.Flush(true);
            }
            File.Move(temp, destination, overwrite: true);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
        finally
        {
            if (cipherBuf != null)
            {
                CryptographicOperations.ZeroMemory(cipherBuf);
                ArrayPool<byte>.Shared.Return(cipherBuf);
            }
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
        }
    }

    private void WritePublicBlobFile(string destination, Func<Stream, long> write)
    {
        byte[] key = GetPublicBlobKey(allowCreate: true);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var encStream = new PublicBlobWriteStream(output, key))
            {
                _ = write(encStream);
                encStream.Complete();
                output.Flush(true);
            }
            File.Move(temp, destination, overwrite: true);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
        finally
        {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
        }
    }

    internal Stream OpenPublicBlobRead(string path)
    {
        var file = File.OpenRead(path);
        try
        {
            if (file.Length >= 8)
            {
                Span<byte> magic = stackalloc byte[8];
                file.ReadExactly(magic);
                if (magic.SequenceEqual(PublicBlobMagic))
                {
                    byte[] key = GetPublicBlobKey(allowCreate: false);
                    var stream = new PublicBlobReadStream(file, key);
                    file = null!;
                    return stream;
                }
            }
            file.Position = 0;
            var legacy = file;
            file = null!;
            return legacy;
        }
        finally
        {
            file?.Dispose();
        }
    }

    private byte[] ReadPublicBlobBytes(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var stream = OpenPublicBlobRead(path);
        byte[]? bytes = null;
        try
        {
            bytes = new byte[checked((int)stream.Length)];
            for (int offset = 0; offset < bytes.Length;)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = stream.Read(bytes, offset, Math.Min(PublicBlobChunkSize, bytes.Length - offset));
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            cancellation.ThrowIfCancellationRequested();
            return bytes;
        }
        catch
        {
            if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private void CopyPublicBlobToPlainFile(string source, string destination)
    {
        using var input = OpenPublicBlobRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output, PublicBlobChunkSize);
        output.Flush(true);
    }

    private sealed class PublicBlobWriteStream : Stream
    {
        private readonly Stream _output;
        private readonly AesGcm _cipher;
        private readonly byte[] _streamId = new byte[12];
        private byte[]? _plainBuf;
        private byte[]? _cipherBuf;
        private int _buffered;
        private ulong _chunkIndex;
        private bool _completed;
        private bool _disposed;

        public PublicBlobWriteStream(Stream output, byte[] key)
        {
            _output = output;
            _cipher = new AesGcm(key, PublicBlobTagSize);
            _plainBuf = ArrayPool<byte>.Shared.Rent(PublicBlobChunkSize);
            _cipherBuf = ArrayPool<byte>.Shared.Rent(PublicBlobChunkSize);
            RandomNumberGenerator.Fill(_streamId);
            Span<byte> header = stackalloc byte[PublicBlobHeaderSize];
            PublicBlobMagic.CopyTo(header);
            _streamId.CopyTo(header[8..]);
            _output.Write(header);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && !_completed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) throw new InvalidOperationException("Encrypted blob stream is already completed.");
            while (!source.IsEmpty)
            {
                if (_buffered == PublicBlobChunkSize)
                    FlushChunk(isFinal: false);
                int take = Math.Min(PublicBlobChunkSize - _buffered, source.Length);
                source[..take].CopyTo(_plainBuf.AsSpan(_buffered, take));
                _buffered += take;
                source = source[take..];
            }
        }

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) return;
            FlushChunk(isFinal: true);
            _completed = true;
        }

        private void FlushChunk(bool isFinal)
        {
            Span<byte> nonce = stackalloc byte[12];
            Span<byte> aad = stackalloc byte[33];
            Span<byte> tag = stackalloc byte[PublicBlobTagSize];
            BuildPublicBlobNonceAndAad(_streamId, _chunkIndex++, _buffered, isFinal, nonce, aad);
            Span<byte> dst = _cipherBuf.AsSpan(0, _buffered);
            _cipher.Encrypt(nonce, _plainBuf.AsSpan(0, _buffered), dst, tag, aad);
            _output.Write(tag);
            if (_buffered > 0) _output.Write(dst);
            _buffered = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _cipher.Dispose();
                if (_plainBuf != null) { CryptographicOperations.ZeroMemory(_plainBuf); ArrayPool<byte>.Shared.Return(_plainBuf); _plainBuf = null; }
                if (_cipherBuf != null) { CryptographicOperations.ZeroMemory(_cipherBuf); ArrayPool<byte>.Shared.Return(_cipherBuf); _cipherBuf = null; }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class PublicBlobReadStream : Stream
    {
        private readonly FileStream _file;
        private readonly AesGcm _cipher;
        private readonly byte[] _streamId = new byte[12];
        private readonly long _totalChunks;
        private readonly int _lastPlainLen;
        private readonly long _plainLength;
        private byte[]? _plainChunk;
        private byte[]? _cipherChunk;
        private long _cachedChunkIndex = -1;
        private int _cachedPlainLen;
        private long _position;
        private bool _disposed;

        public PublicBlobReadStream(FileStream file, byte[] key)
        {
            _file = file;
            vn_clipboard_blob_compute_geometry(
                file.Length,
                out _totalChunks,
                out _lastPlainLen,
                out _plainLength,
                out int errorKind);
            if (errorKind == 1)
                throw new IOException("Truncated encrypted clipboard attachment.");
            if (errorKind != 0)
                throw new IOException("Corrupt encrypted clipboard attachment length.");
            _file.Position = 8;
            _file.ReadExactly(_streamId);
            _cipher = new AesGcm(key, PublicBlobTagSize);
            try
            {
                _plainChunk = ArrayPool<byte>.Shared.Rent(PublicBlobChunkSize);
                _cipherChunk = ArrayPool<byte>.Shared.Rent(PublicBlobChunkSize);
                if (_plainLength == 0)
                    LoadChunk(0);
            }
            catch
            {
                _cipher.Dispose();
                if (_plainChunk != null) { CryptographicOperations.ZeroMemory(_plainChunk); ArrayPool<byte>.Shared.Return(_plainChunk); _plainChunk = null; }
                if (_cipherChunk != null) { CryptographicOperations.ZeroMemory(_cipherChunk); ArrayPool<byte>.Shared.Return(_cipherChunk); _cipherChunk = null; }
                throw;
            }
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _plainLength; } }
        public override long Position
        {
            get { ObjectDisposedException.ThrowIf(_disposed, this); return _position; }
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int totalRead = 0;
            while (!destination.IsEmpty && _position < _plainLength)
            {
                long chunkIndex = _position / PublicBlobChunkSize;
                int chunkOffset = (int)(_position % PublicBlobChunkSize);
                if (_cachedChunkIndex != chunkIndex)
                    LoadChunk(chunkIndex);
                int available = _cachedPlainLen - chunkOffset;
                int count = Math.Min(destination.Length, available);
                _plainChunk.AsSpan(chunkOffset, count).CopyTo(destination);
                _position += count;
                totalRead += count;
                destination = destination[count..];
            }
            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_plainLength + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (next < 0) throw new IOException("Attempted to seek before the beginning of the stream.");
            _position = next;
            return _position;
        }

        private void LoadChunk(long chunkIndex)
        {
            bool isFinal = chunkIndex == _totalChunks - 1;
            int plainLen = isFinal ? _lastPlainLen : PublicBlobChunkSize;
            _file.Position = PublicBlobHeaderSize + chunkIndex * PublicBlobDiskChunkSize;
            Span<byte> tag = stackalloc byte[PublicBlobTagSize];
            _file.ReadExactly(tag);
            if (plainLen > 0)
                _file.ReadExactly(_cipherChunk.AsSpan(0, plainLen));
            Span<byte> nonce = stackalloc byte[12];
            Span<byte> aad = stackalloc byte[33];
            BuildPublicBlobNonceAndAad(_streamId, (ulong)chunkIndex, plainLen, isFinal, nonce, aad);
            try
            {
                _cipher.Decrypt(nonce, _cipherChunk.AsSpan(0, plainLen), tag, _plainChunk.AsSpan(0, plainLen), aad);
            }
            catch (CryptographicException ex)
            {
                CryptographicOperations.ZeroMemory(_plainChunk);
                _cachedChunkIndex = -1;
                throw new IOException("Clipboard attachment authentication failed.", ex);
            }
            _cachedChunkIndex = chunkIndex;
            _cachedPlainLen = plainLen;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _cipher.Dispose();
                _file.Dispose();
                if (_plainChunk != null) { CryptographicOperations.ZeroMemory(_plainChunk); ArrayPool<byte>.Shared.Return(_plainChunk); _plainChunk = null; }
                if (_cipherChunk != null) { CryptographicOperations.ZeroMemory(_cipherChunk); ArrayPool<byte>.Shared.Return(_cipherChunk); _cipherChunk = null; }
            }
            base.Dispose(disposing);
        }
    }

}
