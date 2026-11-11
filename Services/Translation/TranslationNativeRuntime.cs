using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Threading;

namespace VNotch.Services.Translation;

internal static partial class TranslationNativeRuntime
{
    private const string LogCategory = "TRANSLATION";
    private const long MaxVulkanPackageBytes = 150L * 1024 * 1024;
    private const long MaxVulkanExtractBytes = 150L * 1024 * 1024;
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim VulkanInstallLock = new(1, 1);
    private static readonly object LoadLock = new();
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_runtime_is_allowed_uri(
        ReadOnlySpan<char> schemePtr, int schemeLen,
        int userInfoLen,
        byte isDefaultPort,
        ReadOnlySpan<char> hostPtr, int hostLen,
        out byte outAllowed);

    internal static bool IsAllowedRuntimeUri(Uri uri) =>
        vn_translation_runtime_is_allowed_uri(
            uri.Scheme.AsSpan(), uri.Scheme.Length,
            uri.UserInfo.Length,
            uri.IsDefaultPort ? (byte)1 : (byte)0,
            uri.Host.AsSpan(), uri.Host.Length,
            out byte allowed) == 0 && allowed != 0;

    internal const string VulkanNupkgUrl = "https://api.nuget.org/v3-flatcontainer/llamasharp.backend.vulkan.windows/0.27.0/llamasharp.backend.vulkan.windows.0.27.0.nupkg";
    private const string VulkanNupkgPrefix = "LLamaSharpRuntimes/win-x64/native/vulkan/";
    private const string LegacyVulkanNupkgPrefix = "runtimes/win-x64/native/vulkan/";

    private static readonly (string Name, string Sha256)[] VulkanAssets =
    [
        ("llama.dll", "E9F3127597085CE9D9A0E49E4BD1F62EFE35624C76F0964E802DC7C60B5156BB"),
        ("ggml-vulkan.dll", "515171EFA8556572795EA37F78193EF56D3DD5079D3D221979BE89C107938038"),
        ("ggml-base.dll", "7E838BA129672C59AD728F56FC5685CADEFDA56AD50A8FCBCCBB9C851ED90B4A"),
        ("ggml.dll", "7465A5E0B304255A62FB2E5D01C106CC39FC0F4362BFD99FF99ACB1278CACCCC"),
    ];

    // The engine serializes initialization. Keep the library resident for the process lifetime.
    private static bool _loaded;
    private static IntPtr _handle;
    private static string? _library;

    internal static bool IsLoaded
    {
        get { lock (LoadLock) return _loaded; }
    }

    internal static string OnDemandVulkanDirectory =>
        Path.Combine(AppPaths.LocalAppDataRoot, "translation-runtime", "win-x64", "native", "vulkan");

    internal static bool HasBundledVulkanRuntime() =>
        File.Exists(Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "runtimes", "win-x64", "native", "vulkan", "llama.dll"));

    internal static Task<bool> IsOnDemandVulkanValidAsync(CancellationToken ct) =>
        AreVulkanAssetsValidAsync(OnDemandVulkanDirectory, ct);

    internal static string Load()
    {
        lock (LoadLock)
        {
            if (_loaded && _handle != IntPtr.Zero && _library != null) return _library;
            if (TryLoadOnDemandVulkan(out string? loaded)) return loaded!;
            foreach (string path in Candidates(AppContext.BaseDirectory, Avx.IsSupported, Avx2.IsSupported, Avx512F.IsSupported))
            {
                if (TryLoadLibrary(path, out loaded)) return loaded!;
            }
            throw new TranslationException("translation.runtimeMissing");
        }
    }

    private static bool TryLoadOnDemandVulkan(out string? loaded)
    {
        loaded = null;
        string dir = OnDemandVulkanDirectory;
        var streams = new FileStream?[VulkanAssets.Length];
        try
        {
            for (int i = 0; i < VulkanAssets.Length; i++)
            {
                string assetPath = Path.Combine(dir, VulkanAssets[i].Name);
                if (!File.Exists(assetPath))
                {
                    RuntimeLog.Warn(LogCategory, $"On-demand Vulkan asset missing ({VulkanAssets[i].Name}); falling back to bundled runtime.");
                    return false;
                }
                streams[i] = new FileStream(assetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
            for (int i = 0; i < VulkanAssets.Length; i++)
            {
                byte[] expectedHash = Convert.FromHexString(VulkanAssets[i].Sha256);
                if (SHA256.HashData(streams[i]!, actualHash) != SHA256.HashSizeInBytes ||
                    expectedHash.Length != SHA256.HashSizeInBytes ||
                    !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    RuntimeLog.Warn(LogCategory, $"On-demand Vulkan asset failed SHA-256 verification ({VulkanAssets[i].Name}); falling back to bundled runtime.");
                    return false;
                }
            }

            return TryLoadLibraryCore(Path.Combine(dir, "llama.dll"), out loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Warn(LogCategory, $"On-demand Vulkan asset inaccessible ({ex.GetType().Name}); falling back to bundled runtime.");
            return false;
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream?.Dispose();
            }
        }
    }

    private static bool TryLoadLibrary(string path, out string? loaded)
    {
        loaded = null;
        return File.Exists(path) && TryLoadLibraryCore(path, out loaded);
    }

    private static bool TryLoadLibraryCore(string path, out string? loaded)
    {
        loaded = null;
        // Resolve ggml beside llama.dll even with App's hardened DLL search policy.
        if (!NativeLibrary.TryLoad(path, typeof(TranslationNativeRuntime).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32 | DllImportSearchPath.ApplicationDirectory,
            out var handle)) return false;
        _handle = handle;
        _loaded = true;
        loaded = _library = path;
        return true;
    }

    internal static IEnumerable<string> Candidates(string root, bool avx, bool avx2, bool avx512)
    {
        _ = avx;
        string native = Path.Combine(Path.GetFullPath(root), "runtimes", "win-x64", "native");
        yield return Path.Combine(native, "vulkan", "llama.dll");
        if (avx2 || avx512) yield return Path.Combine(native, "avx2", "llama.dll");
        yield return Path.Combine(native, "noavx", "llama.dll");
    }

    internal static async Task EnsureVulkanRuntimeAsync(HttpClient client, CancellationToken ct)
    {
        await VulkanInstallLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string targetDir = OnDemandVulkanDirectory;
            if (await AreVulkanAssetsValidAsync(targetDir, ct).ConfigureAwait(false)) return;

            Directory.CreateDirectory(targetDir);
            using var lease = AppFileCleanupService.Protect(targetDir);
            string tempPkg = Path.Combine(targetDir, "vulkan-runtime.nupkg.part");
            try
            {
                using var response = await SendWithRedirectsAsync(client, new Uri(VulkanNupkgUrl), ct).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength is > MaxVulkanPackageBytes)
                {
                    throw new InvalidDataException("Vulkan runtime package exceeds maximum allowed size.");
                }

                await using (var netStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var pkgFile = new FileStream(tempPkg, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                {
                    await CopyNetworkStreamBoundedAsync(netStream, pkgFile, MaxVulkanPackageBytes, ct).ConfigureAwait(false);
                }

                long totalExtracted = 0;
                await using var zipStream = new FileStream(tempPkg, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                foreach (var (name, expectedSha256) in VulkanAssets)
                {
                    var entry = archive.GetEntry(VulkanNupkgPrefix + name) ?? archive.GetEntry(LegacyVulkanNupkgPrefix + name)
                        ?? throw new InvalidDataException($"Missing {name} in Vulkan runtime package.");
                    if (entry.Length > MaxVulkanExtractBytes || totalExtracted + entry.Length > MaxVulkanExtractBytes)
                    {
                        throw new InvalidDataException($"Extracted Vulkan runtime exceeds {MaxVulkanExtractBytes} bytes.");
                    }

                    string partPath = Path.Combine(targetDir, name + ".part");
                    await using (var entryStream = entry.Open())
                    await using (var outStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        totalExtracted = await CopyEntryBoundedAsync(entryStream, outStream, totalExtracted, MaxVulkanExtractBytes, ct).ConfigureAwait(false);
                    }
                    if (!await VerifyFileSha256Async(partPath, expectedSha256, ct).ConfigureAwait(false))
                    {
                        File.Delete(partPath);
                        throw new InvalidDataException($"Hash mismatch for {name}.");
                    }
                }

                foreach (var (name, _) in VulkanAssets)
                {
                    File.Move(Path.Combine(targetDir, name + ".part"), Path.Combine(targetDir, name), overwrite: true);
                }
            }
            finally
            {
                try { File.Delete(tempPkg); } catch { /* Best-effort temp cleanup */ }
                foreach (var (name, _) in VulkanAssets)
                {
                    try { File.Delete(Path.Combine(targetDir, name + ".part")); } catch { /* Best-effort temp cleanup */ }
                }
            }
        }
        finally
        {
            VulkanInstallLock.Release();
        }
    }

    private static async Task CopyNetworkStreamBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            long totalRead = 0;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                readTimeout.CancelAfter(NetworkTimeout);
                int count;
                try
                {
                    count = await source.ReadAsync(buffer.AsMemory(0, 131072), readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TranslationException("translation.downloadTimeout");
                }
                finally
                {
                    readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                if (count == 0) break;
                totalRead += count;
                if (totalRead > maxBytes)
                {
                    throw new InvalidDataException("Vulkan runtime package exceeds maximum allowed size.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<long> CopyEntryBoundedAsync(Stream source, Stream destination, long totalExtracted, long maxTotalBytes, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            while (true)
            {
                int count = await source.ReadAsync(buffer.AsMemory(0, 131072), ct).ConfigureAwait(false);
                if (count == 0) break;
                totalExtracted += count;
                if (totalExtracted > maxTotalBytes)
                {
                    throw new InvalidDataException("Extracted Vulkan runtime exceeds maximum allowed size.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            return totalExtracted;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<HttpResponseMessage> SendWithRedirectsAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetworkTimeout);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TranslationException("translation.downloadTimeout");
            }
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                if (!IsAllowedRuntimeUri(next))
                {
                    throw new TranslationException("translation.downloadFailed");
                }
                uri = next;
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new TranslationException("translation.downloadFailed");
    }

    private static async Task<bool> AreVulkanAssetsValidAsync(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var (name, expectedSha256) in VulkanAssets)
        {
            if (!await VerifyFileSha256Async(Path.Combine(dir, name), expectedSha256, ct).ConfigureAwait(false))
                return false;
        }
        return true;
    }

    private static async Task<bool> VerifyFileSha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        byte[] actualHash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        byte[] expectedHash = Convert.FromHexString(expectedSha256);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    internal static bool IsLoadFailure(Exception error) =>
        error is DllNotFoundException or BadImageFormatException ||
        error is TypeInitializationException { TypeName: "LLama.Native.NativeApi" } ||
        error.InnerException != null && IsLoadFailure(error.InnerException);
}
