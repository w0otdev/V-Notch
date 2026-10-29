using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

internal static partial class SpotifyScriptMetadataReader
{
    private const int BlockSize = 8192;
    private const int OverlapSize = 1024;
    internal static readonly Regex FindTracksPattern = CreatePattern("findTracks");
    internal static readonly Regex CanvasPattern = CreatePattern("canvas");

    private static Regex CreatePattern(string operation) => new(
        "[\"']" + operation + "[\"']\\s{0,128},\\s{0,128}[\"']query[\"']\\s{0,128},\\s{0,128}[\"'](?<hash>[a-f0-9]{64})[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static async Task<string?> ReadHashAsync(
        Stream stream, Regex pattern, int maxBytes, CancellationToken token)
    {
        var hashes = await ReadAsync(stream, pattern, null, maxBytes, token).ConfigureAwait(false);
        return hashes?.FindTracksHash;
    }

    internal static Task<QueryHashes?> ReadMetadataAsync(Stream stream, int maxBytes, CancellationToken token) =>
        ReadAsync(stream, FindTracksPattern, CanvasPattern, maxBytes, token);

    private static async Task<QueryHashes?> ReadAsync(
        Stream stream, Regex firstPattern, Regex? secondPattern, int maxBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(BlockSize);
        char[] window = ArrayPool<char>.Shared.Rent(BlockSize + OverlapSize);
        int retained = 0;
        int totalRead = 0;
        string? firstHash = null;
        string? secondHash = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(
                    bytes.AsMemory(0, (int)Math.Min(BlockSize, (long)maxBytes - totalRead + 1)), token)
                    .ConfigureAwait(false);
                if (read == 0)
                    return new QueryHashes(firstHash, secondHash);
                if (read > maxBytes - totalRead)
                    return null;
                totalRead += read;

                Encoding.Latin1.GetChars(bytes, 0, read, window, retained);
                int length = retained + read;
                firstHash ??= FindHash(window, length, firstPattern);
                if (secondPattern != null)
                    secondHash ??= FindHash(window, length, secondPattern);
                if (firstHash != null && (secondPattern == null || secondHash != null))
                    return new QueryHashes(firstHash, secondHash);

                retained = Math.Min(OverlapSize, length);
                Array.Copy(window, length - retained, window, 0, retained);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(window);
        }
    }

    internal sealed record QueryHashes(string? FindTracksHash, string? CanvasHash);

    private static unsafe string? FindHash(char[] window, int length, Regex pattern)
    {
        string? op = null;
        if (ReferenceEquals(pattern, FindTracksPattern)) op = "findTracks";
        else if (ReferenceEquals(pattern, CanvasPattern)) op = "canvas";

        if (op != null)
        {
            Span<char> hashBuf = stackalloc char[64];
            byte found;
            fixed (char* pWin = window)
            fixed (char* pOp = op)
            fixed (char* pHash = hashBuf)
            {
                vn_spotify_script_find_hash(pWin, length, pOp, op.Length, pHash, out found);
            }
            return found != 0 ? new string(hashBuf) : null;
        }

        var characters = window.AsSpan(0, length);
        var enumerator = pattern.EnumerateMatches(characters);
        if (enumerator.MoveNext())
        {
            var match = enumerator.Current;
            return pattern.Match(characters.Slice(match.Index, match.Length).ToString()).Groups["hash"].Value;
        }
        return null;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_spotify_script_find_hash(
        char* window_ptr, int window_len,
        char* op_ptr, int op_len,
        char* out_hash64, out byte out_found);
}
