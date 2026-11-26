using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace VNotch.Services.Clipboard;

internal sealed partial class ClipboardVault
{
    internal Stream OpenAttachmentRead(string path)
    {
        var file = File.OpenRead(path);
        try { return new AttachmentReadStream(file, Key); }
        catch { file.Dispose(); throw; }
    }

    // WIC seeks in the encoded image. Keep one authenticated chunk and never spool plaintext to disk.
    private sealed class AttachmentReadStream : Stream
    {
        private readonly FileStream _file;
        private readonly AesGcm _cipher;
        private readonly byte[] _associated = new byte[24];
        private readonly byte[] _ciphertext, _plaintext;
        private readonly long _length;
        private long _position, _cachedStart = -1;
        private bool _disposed;

        internal AttachmentReadStream(FileStream file, byte[] key)
        {
            _file = file;
            _length = ReadAttachmentHeader(file, _associated.AsSpan(0, 16));
            int capacity = (int)Math.Min(_length, ChunkSize);
            _plaintext = new byte[capacity];
            _ciphertext = new byte[capacity];
            _cipher = new AesGcm(key, TagSize);
            try
            {
                // Some decoders ignore trailing bytes. Authenticate those too, including the tag of an empty file.
                long start = 0;
                do { start += LoadChunk(start); } while (start < _length);
            }
            catch { Dispose(); throw; }
        }

        private int LoadChunk(long start)
        {
            _cachedStart = -1;
            int count = (int)Math.Min(ChunkSize, _length - start);
            _file.Position = checked(16 + start + start / ChunkSize * (12 + TagSize));
            BinaryPrimitives.WriteInt64LittleEndian(_associated.AsSpan(16), start);
            DecryptAttachmentChunk(_file, _cipher, _associated, _ciphertext.AsSpan(0, count), _plaintext.AsSpan(0, count));
            _cachedStart = start;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int written = 0;
            while (!destination.IsEmpty && _position < _length)
            {
                int offset = (int)(_position % ChunkSize);
                long start = _position - offset;
                if (start != _cachedStart) LoadChunk(start);
                int count = (int)Math.Min(Math.Min(ChunkSize - offset, destination.Length), _length - _position);
                _plaintext.AsSpan(offset, count).CopyTo(destination);
                _position += count;
                written += count;
                destination = destination[count..];
            }
            return written;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0) throw new IOException("Cannot seek before the beginning of the attachment.");
            return _position = position;
        }

        public override long Position { get { ObjectDisposedException.ThrowIf(_disposed, this); return _position; } set => Seek(value, SeekOrigin.Begin); }
        public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _length; } }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                CryptographicOperations.ZeroMemory(_plaintext);
                CryptographicOperations.ZeroMemory(_ciphertext);
                _cipher.Dispose();
                _file.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
