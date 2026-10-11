using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

// App-owned hardlinks avoid duplicate storage; removal never deletes the original path.
internal sealed class CustomTranslationModels
{
    internal static CustomTranslationModels Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VNotch", "translation-models"));
    private readonly string _root;
    private readonly object _sync = new();
    private readonly Func<string, string, bool> _tryCreateLink;
    private readonly Dictionary<string, TranslationModelProfile> _profiles = new();
    internal TranslationModelProfile[] Profiles { get { lock (_sync) return _profiles.Values.ToArray(); } }
    private sealed record Manifest(string Name, long Size, string Hash, TranslationPromptKind PromptKind, string? ModelPath = null);

    internal CustomTranslationModels(string root, Func<string, string, bool>? tryCreateLink = null)
    {
        _root = Path.GetFullPath(root);
        _tryCreateLink = tryCreateLink ?? TryCreateHardLink;
        if (!Directory.Exists(_root)) return;
        foreach (string directory in Directory.EnumerateDirectories(_root, "custom-*"))
        {
            string id = Path.GetFileName(directory);
            if (!Regex.IsMatch(id, "^custom-[a-f0-9]{32}$")) continue;
            try
            {
                string path = Path.Combine(directory, "custom.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 8192) continue;
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128 ||
                    manifest.Size <= 24 || !Regex.IsMatch(manifest.Hash ?? "", "^[A-Fa-f0-9]{64}$") ||
                    !Enum.IsDefined(manifest.PromptKind) ||
                    (manifest.ModelPath != null && (!Path.IsPathFullyQualified(manifest.ModelPath) ||
                        !Path.GetExtension(manifest.ModelPath).Equals(".gguf", StringComparison.OrdinalIgnoreCase)))) continue;
                _profiles[id] = Profile(id, manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private static TranslationModelProfile Profile(string id, Manifest manifest) => new(id, "", manifest.Hash,
        [new(manifest.ModelPath ?? "model.gguf", "", manifest.Size, manifest.Hash)])
    {
        DisplayName = manifest.Name,
        IsCustom = true,
        PromptKind = manifest.PromptKind,
        MinimumRamGiB = 0,
        GpuGiB = 0,
        License = "",
        TranslationRating = 0
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static bool TryCreateHardLink(string destination, string source) =>
        CreateHardLink(destination, source, IntPtr.Zero);

    internal void Forget(string id) { lock (_sync) _profiles.Remove(id); }

    internal async Task<TranslationModelProfile> ImportAsync(string source, TranslationPromptKind kind,
        CancellationToken ct, IProgress<double>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind) || !Path.GetExtension(source).Equals(".gguf", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(Path.GetFileName(source), @"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase))
            throw new TranslationException("translation.customInvalid");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] header = new byte[24];
        if (input.Length <= header.Length) throw new TranslationException("translation.customInvalid");
        await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("GGUF"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) is not (2 or 3) ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)) == 0)
            throw new TranslationException("translation.customInvalid");
        input.Position = 0;
        string id = "custom-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_root, id);
        using var lease = AppFileCleanupService.Protect(directory);
        Directory.CreateDirectory(directory);
        bool committed = false;
        try
        {
            string sourcePath = Path.GetFullPath(source);
            string destination = Path.Combine(directory, "model.gguf");
            bool linked = _tryCreateLink(destination, sourcePath);
            string hash;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[131072];
                int count;
                long read = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    hasher.AppendData(buffer, 0, count);
                    read += count;
                    if (clock.ElapsedMilliseconds >= 100 || read == input.Length)
                    { progress?.Report(read * 100d / input.Length); clock.Restart(); }
                }
                hash = Convert.ToHexString(hasher.GetHashAndReset());
            }
            string name = Path.GetFileNameWithoutExtension(source);
            // A hardlink owns no additional file data. If the volumes differ (or the
            // filesystem rejects links), keep the selected source path instead of
            // silently duplicating a multi-gigabyte model into AppData.
            var manifest = new Manifest(name[..Math.Min(128, name.Length)], input.Length, hash, kind,
                linked ? null : sourcePath);
            await File.WriteAllTextAsync(Path.Combine(directory, "verified.txt"), hash, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "custom.json.part"), JsonSerializer.Serialize(manifest), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(Path.Combine(directory, "custom.json.part"), Path.Combine(directory, "custom.json"));
            var profile = Profile(id, manifest);
            lock (_sync) _profiles[id] = profile;
            committed = true;
            return profile;
        }
        finally
        {
            if (!committed)
            {
                // Only the freshly generated app-owned directory is eligible for cleanup.
                foreach (string name in new[] { "model.gguf", "verified.txt", "custom.json.part", "custom.json" })
                    try { File.Delete(Path.Combine(directory, name)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                try { Directory.Delete(directory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
