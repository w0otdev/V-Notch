using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VNotch.Services.Translation;

internal sealed partial class TranslationModelStore
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_model_store_is_allowed_uri(
        ReadOnlySpan<char> schemePtr, int schemeLen,
        int userInfoLen,
        byte isDefaultPort,
        ReadOnlySpan<char> hostPtr, int hostLen,
        out byte outAllowed);

    internal sealed record Asset(string Name, string RemotePath, long Size, string Sha256);
    internal TranslationModelProfile Profile { get; }
    private Asset[] Assets => Profile.Assets;
    internal long TotalBytes => Assets.Sum(a => a.Size);
    private static readonly HttpClient Client = new(NetworkPrivacy.Handler(NetworkFeature.TranslationModels,
        new NativeWinHttpHandler { AllowAutoRedirect = false }))
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

    private string AssetPath(Asset asset) => Path.IsPathFullyQualified(asset.Name)
        ? asset.Name
        : Path.Combine(Root, asset.Name);

    internal TranslationModelStore(string? root = null, HttpClient? client = null, TranslationModelProfile? profile = null,
        TimeSpan? networkTimeout = null, NetworkPrivacy? privacy = null)
    {
        Profile = profile ?? TranslationModelProfile.QwenInstruct;
        Root = Path.GetFullPath(root ?? Path.Combine(AppPaths.LocalAppDataRoot, "translation-models", Profile.Id));
        _client = client ?? Client;
        _networkTimeout = networkTimeout ?? TimeSpan.FromSeconds(45);
        _privacy = privacy ?? NetworkPrivacy.Current;
    }

    internal bool IsInstalled => File.Exists(Path.Combine(Root, "verified.txt")) && Assets.All(a =>
    {
        var file = new FileInfo(AssetPath(a));
        return file.Exists && file.Length == a.Size;
    });

    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private (long Length, DateTime Written, DateTime Created)[]? _verifiedState;

    private (long Length, DateTime Written, DateTime Created)[] ReadAssetState() => Assets.Select(asset =>
    {
        var file = new FileInfo(AssetPath(asset));
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
                await VerifyAssetAsync(AssetPath(asset), asset, ct).ConfigureAwait(false);
            if (!state.SequenceEqual(ReadAssetState())) throw new TranslationException("translation.modelInvalid");
            _verifiedState = state;
        }
        finally { _verificationGate.Release(); }
    }

    internal static async Task VerifyAssetAsync(string path, Asset asset, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new TranslationException("translation.modelInvalid");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length != asset.Size) throw new TranslationException("translation.modelInvalid");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new TranslationException("translation.modelInvalid");
    }

    // Only the explicit Download button invokes this. Inference never constructs a request.
    internal async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct,
        IProgress<TranslationDownloadProgress>? details = null)
    {
        if (Profile.IsCustom) throw new TranslationException("translation.customInvalid");
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
                var partialFile = new FileInfo(partial);
                long offset = partialFile.Exists ? partialFile.Length : 0;
                if (offset > asset.Size) { File.Delete(partial); offset = 0; }
                if (offset < asset.Size)
                {
                    details?.Report(new(TranslationDownloadStage.Connecting, completed + offset, TotalBytes));
                    using var response = await OpenDownloadAsync(asset, offset, partial, ct).ConfigureAwait(false);
                    bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
                    if (resumed && (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != asset.Size))
                        throw new TranslationException("translation.modelInvalid");
                    if (!resumed) offset = 0;
                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
                    try
                    {
                        var clock = Stopwatch.StartNew();
                        long received = 0;
                        double lastReport = 0;
                        details?.Report(new(TranslationDownloadStage.Downloading, completed + offset, TotalBytes));
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        int count;
                        while (true)
                        {
                            readTimeout.CancelAfter(_networkTimeout);
                            try { count = await input.ReadAsync(buffer.AsMemory(0, 131072), readTimeout.Token).ConfigureAwait(false); }
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
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
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

    private async Task<HttpResponseMessage> OpenDownloadAsync(Asset asset, long offset, string partialPath, CancellationToken ct)
    {
        var rootUri = new Uri($"https://huggingface.co/{Profile.Repository}/resolve/{Profile.Revision}/{asset.RemotePath}");
        var uri = rootUri;
        int hop = 0;
        while (hop < 5)
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
                hop++;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                response.Dispose();
                File.Delete(partialPath);
                offset = 0;
                uri = rootUri;
                hop = 0;
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new TranslationException("translation.downloadFailed");
    }

    internal static bool IsAllowedDownloadUri(Uri uri) =>
        vn_translation_model_store_is_allowed_uri(
            uri.Scheme.AsSpan(), uri.Scheme.Length,
            uri.UserInfo.Length,
            uri.IsDefaultPort ? (byte)1 : (byte)0,
            uri.Host.AsSpan(), uri.Host.Length,
            out byte allowed) == 0 && allowed != 0;

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
                // A custom model can reference its original file when a hardlink is
                // unavailable. Removing the registration must never delete that file.
                if (Path.IsPathFullyQualified(asset.Name)) continue;
                File.Delete(Path.Combine(Root, asset.Name));
                File.Delete(Path.Combine(Root, asset.Name + ".part"));
                File.Delete(Path.Combine(Root, asset.Name + ".install.part"));
            }
            if (Profile.IsCustom)
            {
                File.Delete(Path.Combine(Root, "custom.json"));
                CustomTranslationModels.Shared.Forget(Profile.Id);
            }
        }
        finally { Gate.Release(); Changed?.Invoke(); }
    }

    private static async Task InstallAsync(string source, string destination, Asset asset, CancellationToken ct)
    {
        string temporary = destination + ".install.part";
        try
        {
            await CopyFileAsync(source, temporary, ct).ConfigureAwait(false);
            await VerifyAssetAsync(temporary, asset, ct).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort cleanup of temporary install file. */ }
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }
}
