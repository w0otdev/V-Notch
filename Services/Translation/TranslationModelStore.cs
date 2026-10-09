using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace VNotch.Services.Translation;

internal sealed class TranslationModelStore
{
    internal sealed record Asset(string Name, string RemotePath, long Size, string Sha256);
    internal TranslationModelProfile Profile { get; }
    private Asset[] Assets => Profile.Assets;
    internal long TotalBytes => Assets.Sum(a => a.Size);
    private static readonly HttpClient Client = new(NetworkPrivacy.Handler(NetworkFeature.TranslationModels,
        new HttpClientHandler { AllowAutoRedirect = false }))
    { Timeout = Timeout.InfiniteTimeSpan };
    // The singleton constructor captures Client; initialize the transport first.
    internal static TranslationModelStore Shared { get; } = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TranslationModelStore> Stores = new();
    internal static TranslationModelStore For(string? id)
    {
        var profile = TranslationModelCatalog.Find(id);
        return profile.Id == Shared.Profile.Id ? Shared : Stores.GetOrAdd(profile.Id, _ => new(profile: profile));
    }
    private readonly HttpClient _client;
    private readonly TimeSpan _networkTimeout;
    private readonly NetworkPrivacy _privacy;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal string Root { get; }
    internal event Action? Changed;
    // Called under Gate before removing files; engine releases its file handles synchronously.
    internal event Action? Replacing;

    internal TranslationModelStore(string? root = null, HttpClient? client = null, TranslationModelProfile? profile = null,
        TimeSpan? networkTimeout = null, NetworkPrivacy? privacy = null)
    {
        Profile = profile ?? TranslationModelProfile.QwenInstruct;
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VNotch", "translation-models", Profile.Id));
        _client = client ?? Client;
        _networkTimeout = networkTimeout ?? TimeSpan.FromSeconds(45);
        _privacy = privacy ?? NetworkPrivacy.Current;
    }

    internal bool IsInstalled => File.Exists(Path.Combine(Root, "verified.txt")) && Assets.All(a =>
        File.Exists(Path.Combine(Root, a.Name)) && new FileInfo(Path.Combine(Root, a.Name)).Length == a.Size);

    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private (long Length, DateTime Written, DateTime Created)[]? _verifiedState;

    private (long Length, DateTime Written, DateTime Created)[] ReadAssetState() => Assets.Select(asset =>
    {
        var file = new FileInfo(Path.Combine(Root, asset.Name));
        return (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
    }).ToArray();

    internal async Task VerifyAsync(CancellationToken ct)
    {
        await _verificationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var state = ReadAssetState();
            if (_verifiedState != null && state.SequenceEqual(_verifiedState)) return;
            _verifiedState = null;
            foreach (var asset in Assets)
                await VerifyAssetAsync(Path.Combine(Root, asset.Name), asset, ct).ConfigureAwait(false);
            if (!state.SequenceEqual(ReadAssetState())) throw new TranslationException("translation.modelInvalid");
            _verifiedState = state;
        }
        finally { _verificationGate.Release(); }
    }

    internal static async Task VerifyAssetAsync(string path, Asset asset, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length != asset.Size) throw new TranslationException("translation.modelInvalid");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new TranslationException("translation.modelInvalid");
    }

    // Only the explicit Download button invokes this. Inference never constructs a request.
    internal async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct,
        IProgress<TranslationDownloadProgress>? details = null)
    {
        using var permission = CancellationTokenSource.CreateLinkedTokenSource(ct, _privacy.Acquire(NetworkFeature.TranslationModels));
        ct = permission.Token;
        details?.Report(new(TranslationDownloadStage.Waiting, 0, TotalBytes));
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Root);
            _verifiedState = null;
            Replacing?.Invoke();
            File.Delete(Path.Combine(Root, "verified.txt"));
            long completed = 0;
            foreach (var asset in Assets)
            {
                string path = Path.Combine(Root, asset.Name);
                if (File.Exists(path))
                {
                    details?.Report(new(TranslationDownloadStage.Verifying, completed + asset.Size, TotalBytes));
                    try { await VerifyAssetAsync(path, asset, ct).ConfigureAwait(false); completed += asset.Size; continue; }
                    catch (TranslationException)
                    {
                        File.Delete(path);
                    }
                }
                string partial = path + ".part";
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > asset.Size) { File.Delete(partial); offset = 0; }
                if (offset < asset.Size)
                {
                    details?.Report(new(TranslationDownloadStage.Connecting, completed + offset, TotalBytes));
                    using var response = await OpenDownloadAsync(asset, offset, ct).ConfigureAwait(false);
                    bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
                    if (resumed && (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != asset.Size))
                        throw new TranslationException("translation.modelInvalid");
                    if (!resumed) offset = 0;
                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                    byte[] buffer = new byte[131072];
                    var clock = Stopwatch.StartNew();
                    long received = 0;
                    double lastReport = 0;
                    details?.Report(new(TranslationDownloadStage.Downloading, completed + offset, TotalBytes));
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    int count;
                    while (true)
                    {
                        readTimeout.CancelAfter(_networkTimeout);
                        try { count = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        { throw new TranslationException("translation.downloadTimeout"); }
                        finally { readTimeout.CancelAfter(Timeout.InfiniteTimeSpan); }
                        if (count == 0) break;
                        offset += count;
                        if (offset > asset.Size) throw new TranslationException("translation.modelInvalid");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                        received += count;
                        if (clock.Elapsed.TotalSeconds - lastReport >= .25 || offset == asset.Size)
                        {
                            progress?.Report((completed + offset) / (double)TotalBytes);
                            details?.Report(new(TranslationDownloadStage.Downloading, completed + offset, TotalBytes,
                                received / Math.Max(.001, clock.Elapsed.TotalSeconds)));
                            lastReport = clock.Elapsed.TotalSeconds;
                        }
                    }
                }
                details?.Report(new(TranslationDownloadStage.Verifying, completed + offset, TotalBytes));
                try { await VerifyAssetAsync(partial, asset, ct).ConfigureAwait(false); }
                catch (TranslationException) { File.Delete(partial); throw; }
                // Already verified: atomic rename avoids another multi-GB copy and hash pass.
                ct.ThrowIfCancellationRequested();
                File.Move(partial, path, true);
                completed += asset.Size;
                progress?.Report(completed / (double)TotalBytes);
            }
            await File.WriteAllTextAsync(Path.Combine(Root, "verified.txt"), Profile.Revision, ct).ConfigureAwait(false);
            details?.Report(new(TranslationDownloadStage.Completed, TotalBytes, TotalBytes));
        }
        finally { Gate.Release(); Changed?.Invoke(); }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Asset asset, long offset, CancellationToken ct)
    {
        var uri = new Uri($"https://huggingface.co/{Profile.Repository}/resolve/{Profile.Revision}/{asset.RemotePath}");
        for (int hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_networkTimeout);
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TranslationException("translation.downloadTimeout"); }
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                if (!IsAllowedDownloadUri(next)) throw new TranslationException("translation.downloadFailed");
                uri = next;
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new TranslationException("translation.downloadFailed");
    }

    internal static bool IsAllowedDownloadUri(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort &&
        (uri.Host == "huggingface.co" || uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase));

    internal async Task ImportAsync(string folder, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        using var cleanupLease = AppFileCleanupService.Protect(Root);
        try
        {
            // Accept only the pinned files, irrespective of filenames supplied by a manifest.
            foreach (var asset in Assets)
            {
                string source = Path.Combine(folder, asset.Name);
                await VerifyAssetAsync(source, asset, ct).ConfigureAwait(false);
            }
            Directory.CreateDirectory(Root);
            _verifiedState = null;
            Replacing?.Invoke();
            File.Delete(Path.Combine(Root, "verified.txt"));
            foreach (var asset in Assets)
            {
                string destination = Path.Combine(Root, asset.Name);
                string source = Path.GetFullPath(Path.Combine(folder, asset.Name));
                if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) continue;
                await InstallAsync(source, destination, asset, ct).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(Root, "verified.txt"), Profile.Revision, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); Changed?.Invoke(); }
    }

    internal async Task RemoveAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _verifiedState = null;
            Replacing?.Invoke();
            File.Delete(Path.Combine(Root, "verified.txt"));
            foreach (var asset in Assets)
            {
                File.Delete(Path.Combine(Root, asset.Name));
                File.Delete(Path.Combine(Root, asset.Name + ".part"));
                File.Delete(Path.Combine(Root, asset.Name + ".install.part"));
            }
        }
        finally { Gate.Release(); Changed?.Invoke(); }
    }

    private static async Task InstallAsync(string source, string destination, Asset asset, CancellationToken ct)
    {
        string temporary = destination + ".install.part";
        try
        {
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            await VerifyAssetAsync(temporary, asset, ct).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
