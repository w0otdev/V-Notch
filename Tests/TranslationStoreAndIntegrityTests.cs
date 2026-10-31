using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationStoreAndIntegrityTests
{
    private sealed class TruncatingStubTranslationEngine(int maxWords, Func<string, string>? translator = null) : ILocalTranslationEngine
    {
        private readonly Func<string, string> _translator = translator ?? (text => $"[vi] {text}");
        public TranslationStage Stage => default;
        public int TranslateCalls { get; private set; }

        public void Configure(TranslationOptions options) { }
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void ReleaseIdleResources() { }
        public void Dispose() { }

        public Task<string> FitInputAsync(string text, CancellationToken cancellationToken)
        {
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= maxWords) return Task.FromResult(text);
            return Task.FromResult(string.Join(' ', words.Take(maxWords)));
        }

        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        {
            TranslateCalls++;
            return Task.FromResult(_translator(text));
        }
    }

    [Fact]
    public async Task TranslateAsync_TruncatedInputDroppingTrailingUrl_FlagsMissingUrlAndDoesNotCache()
    {
        string prefix = string.Join(' ', Enumerable.Repeat("ab", 512));
        string fullText = $"{prefix} Visit https://example.com/important-link for details.";
        string translatedPrefix = $"[vi] {prefix}";

        var inspection = TranslationTextIntegrity.Inspect(fullText, translatedPrefix);
        Assert.False(inspection.LiteralsPreserved);
        Assert.False(inspection.ProtectedFactsPreserved);

        using var engine = new TruncatingStubTranslationEngine(512, _ => translatedPrefix);
        using var service = new LocalTranslationService(engine);

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            service.TranslateAsync(fullText, "en", "vi", CancellationToken.None));
        Assert.Equal("translation.literalsChanged", ex.Message);

        Assert.Null(service.TryGetCachedResult(fullText, "en", "vi", CancellationToken.None));
        Assert.Null(service.TryGetCachedResult(prefix, "en", "vi", CancellationToken.None));
    }

    [Fact]
    public async Task TranslateCoreAsync_CachesUnderFittedKeyOnly_AndInspectsFittedCacheAgainstOriginalText()
    {
        string prefix = string.Join(' ', Enumerable.Repeat("ab", 512));
        string fullProseOnly = $"{prefix} extra trailing prose sentence without literals.";

        using var engine = new TruncatingStubTranslationEngine(512, text => $"[vi] {text}");
        using var service = new LocalTranslationService(engine);

        var result = await service.TranslateAsync(fullProseOnly, "en", "vi", CancellationToken.None);
        Assert.True(result.InputTruncated);
        Assert.True(result.LiteralsPreserved);
        Assert.Equal(1, engine.TranslateCalls);

        // Must cache under fittedKey (prefix) and NEVER poison the full-text key.
        Assert.Null(service.TryGetCachedResult(fullProseOnly, "en", "vi", CancellationToken.None));
        var fittedCached = service.TryGetCachedResult(prefix, "en", "vi", CancellationToken.None);
        Assert.NotNull(fittedCached);
        Assert.Equal($"[vi] {prefix}", fittedCached!.Text);

        // A subsequent truncated input sharing the same fitted prefix but containing a trailing URL
        // must still inspect fittedCached against originalText and reject the dropped URL.
        string fullWithTrailingUrl = $"{prefix} See https://example.com/important-link now.";
        await Assert.ThrowsAsync<TranslationException>(() =>
            service.TranslateAsync(fullWithTrailingUrl, "en", "vi", CancellationToken.None));
        Assert.Equal(1, engine.TranslateCalls);
    }

    [Fact]
    public void QwenTranslationEngine_ReusesPreparedInputAcrossFitAndResolve_WithoutDoubleTokenizing()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "V-Notch-Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TranslationModelStore(root: tempRoot);
            int tokenizeCalls = 0;
            using var engine = new QwenTranslationEngine(store, preferGpu: false)
            {
                TokenCounterForTesting = text =>
                {
                    tokenizeCalls++;
                    return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                }
            };

            string sample = "Hello world from local translation engine.";
            var fitted = engine.PrepareInput(sample);
            Assert.Equal(1, engine.PrepareInputCallCount);
            int callsAfterPrepare = tokenizeCalls;
            Assert.True(callsAfterPrepare >= 1);

            var resolved = engine.ResolvePreparedInput(new TranslationEngineInput(fitted.Text));
            Assert.Equal(1, engine.PrepareInputCallCount);
            Assert.Equal(callsAfterPrepare, tokenizeCalls);
            Assert.Equal(fitted.TokenCount, resolved.TokenCount);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TranslationModelStore_RemoveAsync_CustomProfile_DeletesRootAndRemovesStoreInstance()
    {
        string customId = $"custom-{Guid.NewGuid():N}";
        var profile = CreateCustomProfile(customId);
        var store = TranslationModelStore.For(profile);
        try
        {
            Directory.CreateDirectory(store.Root);
            await File.WriteAllTextAsync(Path.Combine(store.Root, "custom.json"), "{}");
            Assert.True(Directory.Exists(store.Root));

            await store.RemoveAsync();

            Assert.False(Directory.Exists(store.Root));
            var replacement = TranslationModelStore.For(profile);
            Assert.False(ReferenceEquals(store, replacement));
            await replacement.RemoveAsync();
        }
        finally
        {
            if (Directory.Exists(store.Root)) Directory.Delete(store.Root, recursive: true);
        }
    }

    [Fact]
    public async Task TranslationModelStore_DownloadAsync_ProtectsRootWhileDownloadInProgress()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "V-Notch-Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = "test-model-bytes"u8.ToArray();
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var asset = new TranslationModelStore.Asset("test.gguf", "test.gguf", payload.Length, sha256);
        var profile = CloneProfileWithAssets(TranslationModelProfile.QwenInstruct, asset);

        var enteredDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new ControlledHttpMessageHandler(async ct =>
        {
            enteredDownload.TrySetResult();
            await releaseDownload.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        });
        using var client = new HttpClient(handler);
        var privacy = CreatePermissivePrivacy();
        var store = new TranslationModelStore(root: tempRoot, client: client, profile: profile, privacy: privacy);

        try
        {
            Assert.False(IsProtectedPath(store.Root));

            Task downloadTask = store.DownloadAsync(progress: null, CancellationToken.None);
            await enteredDownload.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(IsProtectedPath(store.Root));

            releaseDownload.TrySetResult();
            await downloadTask;

            Assert.False(IsProtectedPath(store.Root));
        }
        finally
        {
            releaseDownload.TrySetResult();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static bool IsProtectedPath(string path)
    {
        var method = typeof(AppFileCleanupService).GetMethod("IsProtected", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [path])!;
    }

    private static TranslationModelProfile CreateCustomProfile(string id)
    {
        var baseProfile = TranslationModelProfile.QwenInstruct;
        var ctor = typeof(TranslationModelProfile)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (string.Equals(param.Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                args[i] = id;
            }
            else if (string.Equals(param.Name, "isCustom", StringComparison.OrdinalIgnoreCase))
            {
                args[i] = true;
            }
            else if (param.ParameterType == typeof(TranslationModelStore.Asset[]))
            {
                args[i] = new[] { new TranslationModelStore.Asset("custom.gguf", "custom.gguf", 4, new string('0', 64)) };
            }
            else
            {
                var prop = typeof(TranslationModelProfile).GetProperty(
                    param.Name!,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                args[i] = prop != null ? prop.GetValue(baseProfile) : (param.HasDefaultValue ? param.DefaultValue : null);
            }
        }

        var profile = (TranslationModelProfile)ctor.Invoke(args);
        if (!profile.IsCustom)
        {
            var isCustomProp = typeof(TranslationModelProfile).GetProperty(
                nameof(TranslationModelProfile.IsCustom),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            isCustomProp?.GetSetMethod(nonPublic: true)?.Invoke(profile, [true]);
        }
        Assert.Equal(id, profile.Id);
        Assert.True(profile.IsCustom);
        return profile;
    }

    private static TranslationModelProfile CloneProfileWithAssets(TranslationModelProfile baseProfile, params TranslationModelStore.Asset[] assets)
    {
        var ctor = typeof(TranslationModelProfile)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.ParameterType == typeof(TranslationModelStore.Asset[]))
            {
                args[i] = assets;
            }
            else
            {
                var prop = typeof(TranslationModelProfile).GetProperty(
                    param.Name!,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                args[i] = prop != null ? prop.GetValue(baseProfile) : (param.HasDefaultValue ? param.DefaultValue : null);
            }
        }

        return (TranslationModelProfile)ctor.Invoke(args);
    }

    private static NetworkPrivacy CreatePermissivePrivacy()
    {
        var privacy = new NetworkPrivacy();
        privacy.Apply(new VNotch.Models.NotchSettings { EnableLocalOnlyMode = false });
        return privacy;
    }

    private static void EnableTranslationModelsIfNeeded(NetworkPrivacy privacy)
    {
        foreach (var method in typeof(NetworkPrivacy).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var p = method.GetParameters();
            if (p.Length == 2 && p[0].ParameterType == typeof(NetworkFeature) && p[1].ParameterType == typeof(bool))
            {
                try { method.Invoke(privacy, [NetworkFeature.TranslationModels, true]); }
                catch { /* Best effort */ }
            }
        }
    }

    private static bool CanAcquireTranslationModels(NetworkPrivacy privacy)
    {
        try
        {
            var token = privacy.Acquire(NetworkFeature.TranslationModels);
            return !token.IsCancellationRequested;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ControlledHttpMessageHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(cancellationToken);
    }
}
