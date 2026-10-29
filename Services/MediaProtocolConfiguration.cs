using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services;

// Maintainer-owned compatibility data ships with app updates. Reading it never
// performs a network request or asks the user to edit technical configuration.
internal sealed partial record MediaProtocolConfiguration(
    string SpotifyTotpVersion, string SpotifyTotpSecretBase64,
    string SpotifyFindTracksHash, string SpotifyCanvasHash,
    string YouTubeInnertubeApiKey, string YouTubeAndroidClientVersion,
    IReadOnlyList<string> PipedInstances, IReadOnlyList<string> InvidiousInstances)
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_protocol_is_hash(
        ReadOnlySpan<char> ptr,
        int len,
        out byte outValid);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_protocol_validate_scalars(
        int schemaVersion,
        ReadOnlySpan<char> totpVerPtr, int totpVerLen,
        int secretB64Len,
        int secretDecodedLen,
        ReadOnlySpan<char> findHashPtr, int findHashLen,
        ReadOnlySpan<char> canvasHashPtr, int canvasHashLen,
        ReadOnlySpan<char> ytKeyPtr, int ytKeyLen,
        ReadOnlySpan<char> ytVerPtr, int ytVerLen,
        out byte outValid);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_protocol_is_allowed_host_basic(
        ReadOnlySpan<char> hostPtr,
        int hostLen,
        out byte outValid);

    private const int MaxConfigurationBytes = 64 * 1024;
    private static readonly Lazy<MediaProtocolConfiguration> Configuration = new(() =>
    {
        using var resource = typeof(MediaProtocolConfiguration).Assembly.GetManifestResourceStream("VNotch.MediaProtocols.json")
            ?? throw new InvalidOperationException("Missing media protocol defaults.");
        using var reader = new StreamReader(resource);
        var defaults = Parse(reader.ReadToEnd());
        return Load(Path.Combine(AppContext.BaseDirectory, "Assets", "MediaProtocols.json"), defaults);
    });

    internal static MediaProtocolConfiguration Current => Configuration.Value;
    internal byte[] CreateSpotifyTotpSecret() => Convert.FromBase64String(SpotifyTotpSecretBase64);

    internal static MediaProtocolConfiguration Load(string path, MediaProtocolConfiguration defaults)
    {
        try
        {
            if (!File.Exists(path)) return defaults;
            using var file = File.OpenRead(path);
            if (file.Length > MaxConfigurationBytes) throw new InvalidDataException("Protocol configuration is too large.");
            using var limited = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (limited.Length + read > MaxConfigurationBytes) throw new InvalidDataException("Protocol configuration is too large.");
                limited.Write(buffer, 0, read);
            }
            return Parse(System.Text.Encoding.UTF8.GetString(limited.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            RuntimeLog.Warn("MEDIA-PROTOCOL", "Invalid or unreadable compatibility data; using bundled defaults.");
            return defaults;
        }
    }

    internal static MediaProtocolConfiguration Parse(string json)
    {
        if (json.Length > MaxConfigurationBytes) throw new InvalidDataException("Protocol configuration is too large.");
        var data = JsonSerializer.Deserialize<ProtocolDocument>(json) ?? throw new JsonException("Missing protocol configuration.");
        if (data.SpotifyTotpVersion is not string version ||
            data.SpotifyTotpSecretBase64 is not { Length: > 0 and <= 512 } secret ||
            data.SpotifyFindTracksHash is not string findHash ||
            data.SpotifyCanvasHash is not string canvasHash ||
            data.YouTubeInnertubeApiKey is not string key ||
            data.YouTubeAndroidClientVersion is not string clientVersion)
            throw new JsonException("Invalid protocol configuration.");

        int decodedLen = Convert.FromBase64String(secret).Length;
        _ = vn_media_protocol_validate_scalars(
            data.SchemaVersion,
            version.AsSpan(), version.Length,
            secret.Length, decodedLen,
            findHash.AsSpan(), findHash.Length,
            canvasHash.AsSpan(), canvasHash.Length,
            key.AsSpan(), key.Length,
            clientVersion.AsSpan(), clientVersion.Length,
            out byte validScalars);
        if (validScalars == 0)
            throw new JsonException("Invalid protocol configuration.");

        return new(version, secret, findHash, canvasHash, key, clientVersion,
            ValidateHosts(data.PipedInstances), ValidateHosts(data.InvidiousInstances));
    }

    private static IReadOnlyList<string> ValidateHosts(string[]? hosts)
    {
        if (hosts == null || hosts.Length > 16 || hosts.Any(host =>
            host == null ||
            vn_media_protocol_is_allowed_host_basic(host.AsSpan(), host.Length, out byte basicOk) != 0 ||
            basicOk == 0 ||
            Uri.CheckHostName(host) != UriHostNameType.Dns))
            throw new JsonException("Invalid fallback hosts.");
        return Array.AsReadOnly(hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal sealed class ProtocolDocument
    {
        public int SchemaVersion { get; set; }
        public string? SpotifyTotpVersion { get; set; }
        public string? SpotifyTotpSecretBase64 { get; set; }
        public string? SpotifyFindTracksHash { get; set; }
        public string? SpotifyCanvasHash { get; set; }
        public string? YouTubeInnertubeApiKey { get; set; }
        public string? YouTubeAndroidClientVersion { get; set; }
        public string[]? PipedInstances { get; set; }
        public string[]? InvidiousInstances { get; set; }
    }
}
