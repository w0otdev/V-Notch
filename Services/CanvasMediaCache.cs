using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

/// <summary>Both loop decoders read one bounded local download instead of independent HTTP streams.</summary>
internal sealed class CanvasMediaCache(HttpClient client, string root)
{
    private const long MaxVideoBytes = 16 * 1024 * 1024;
    private const long MaxCacheBytes = 64 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<Uri> GetAsync(Uri remote, CancellationToken cancellationToken)
    {
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote.AbsoluteUri))) + ".mp4";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string path = Path.Combine(root, name), temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(root);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return new Uri(path);
            }
            using var response = await client.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxVideoBytes) throw new InvalidDataException("Canvas too large");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxVideoBytes) throw new InvalidDataException("Canvas too large");
                        await output.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    if (total == 0) throw new InvalidDataException("Empty Canvas");
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(bytes); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            long retained = new FileInfo(path).Length;
            int count = 1;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.mp4").Where(f => f.FullName != path).OrderByDescending(f => f.LastWriteTimeUtc))
            {
                retained += file.Length;
                if (++count > 8 || retained > MaxCacheBytes)
                    try { file.Delete(); } catch (IOException) { /* Active decoder still owns this file. */ }
            }
            return new Uri(path);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { /* Retry replaces an interrupted download. */ }
            _gate.Release();
        }
    }
}
