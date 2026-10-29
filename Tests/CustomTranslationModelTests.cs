using System.Buffers.Binary;
using System.IO;
using VNotch.Models;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class CustomTranslationModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vnotch-custom-tests-" + Guid.NewGuid().ToString("N"));
    private string Models => Path.Combine(_root, "models");
    private string WriteFixture(string name = "my-model.gguf")
    {
        Directory.CreateDirectory(_root);
        byte[] bytes = new byte[262144];
        "GGUF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 1);
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task ImportPersistsIndependentModelsAndRemovalKeepsOriginal()
    {
        string source = WriteFixture();
        var registry = new CustomTranslationModels(Models);
        var first = await registry.ImportAsync(source, TranslationPromptKind.General, default);
        var second = await registry.ImportAsync(source, TranslationPromptKind.TranslateGemma, default);
        Assert.NotEqual(first.Id, second.Id);
        var restored = new CustomTranslationModels(Models).Profiles;
        Assert.Equal(2, restored.Length);
        var profile = Assert.Single(restored, p => p.Id == first.Id);
        Assert.True(profile.IsCustom);
        Assert.Equal(0, profile.TranslationRating);
        Assert.Equal("my-model", profile.DisplayName);
        Assert.Equal(TranslationPromptKind.TranslateGemma, Assert.Single(restored, p => p.Id == second.Id).PromptKind);
        var store = new TranslationModelStore(Path.Combine(Models, first.Id), profile: profile);
        Assert.True(store.IsInstalled);
        await store.VerifyAsync(default);
        await Assert.ThrowsAsync<TranslationException>(() => store.DownloadAsync(null, default));
        await store.RemoveAsync(default);
        Assert.False(store.IsInstalled);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(Models, second.Id, "model.gguf")));
        Assert.Equal(second.Id, Assert.Single(new CustomTranslationModels(Models).Profiles).Id);
    }

    [Fact]
    public async Task SameVolumeImportSharesStorageAndSurvivesOriginalDeletion()
    {
        string source = WriteFixture();
        var registry = new CustomTranslationModels(Models);
        var profile = await registry.ImportAsync(source, TranslationPromptKind.General, default);
        string imported = Path.Combine(Models, profile.Id, "model.gguf");
        using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write))
        { stream.Position = 100; stream.WriteByte(42); }
        Assert.Equal(42, (await File.ReadAllBytesAsync(imported))[100]);
        File.Delete(source);
        Assert.True(File.Exists(imported));
    }

    [Fact]
    public async Task LinkFailureReferencesOriginalWithoutCopyingOrDeletingIt()
    {
        string source = WriteFixture();
        var registry = new CustomTranslationModels(Models, (_, _) => false);
        var profile = await registry.ImportAsync(source, TranslationPromptKind.General, default);
        string directory = Path.Combine(Models, profile.Id);
        Assert.False(File.Exists(Path.Combine(directory, "model.gguf")));
        Assert.Equal(Path.GetFullPath(source), Assert.Single(profile.Assets).Name);

        var restored = Assert.Single(new CustomTranslationModels(Models).Profiles);
        var store = new TranslationModelStore(directory, profile: restored);
        Assert.True(store.IsInstalled);
        await store.VerifyAsync(default);
        await store.RemoveAsync(default);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("bad.gguf", true)]
    [InlineData("model-00001-of-00002.gguf", false)]
    [InlineData("model.bin", false)]
    public async Task InvalidAndSplitImportsAreRejectedWithoutRegistration(string name, bool corrupt)
    {
        string source = WriteFixture(name);
        if (corrupt) await File.WriteAllTextAsync(source, new string('x', 32));
        var registry = new CustomTranslationModels(Models);
        await Assert.ThrowsAsync<TranslationException>(() => registry.ImportAsync(source, TranslationPromptKind.General, default));
        Assert.Empty(registry.Profiles);
        Assert.Empty(new CustomTranslationModels(Models).Profiles);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task CancellationDuringHashCleansIncompleteImport()
    {
        string source = WriteFixture();
        using var cancellation = new CancellationTokenSource();
        var registry = new CustomTranslationModels(Models);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ImportAsync(source,
            TranslationPromptKind.General, cancellation.Token, new CancelProgress(cancellation)));
        Assert.Empty(registry.Profiles);
        Assert.Empty(Directory.EnumerateDirectories(Models));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void FastPresetKeepsOutputAndGpuChoicesWhileReservingCpuCapacity()
    {
        var settings = new NotchSettings { TranslationOutputTokens = 768, TranslationGpuLayers = 0 };
        SettingsWindow.ApplyFastTranslationPreset(settings, 16);
        Assert.Equal(0, settings.TranslationModelIdleMinutes);
        Assert.Equal(4, settings.TranslationThreads);
        Assert.Equal(4, settings.TranslationBatchThreads);
        Assert.Equal(2048, settings.TranslationContextSize);
        Assert.Equal(768, settings.TranslationOutputTokens);
        Assert.Equal(0, settings.TranslationGpuLayers);
        SettingsWindow.ApplyFastTranslationPreset(settings, 1);
        Assert.Equal(1, settings.TranslationThreads);
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<double>
    { public void Report(double value) => cancellation.Cancel(); }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
