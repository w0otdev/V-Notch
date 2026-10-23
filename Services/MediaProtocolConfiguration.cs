using System.IO;
using System.Text.Json;

namespace VNotch.Services;

// Maintainer-owned compatibility data ships with app updates. Reading it never
// performs a network request or asks the user to edit technical configuration.
internal sealed record MediaProtocolConfiguration(
    string SpotifyTotpVersion, string SpotifyTotpSecretBase64,
    string SpotifyFindTracksHash, string SpotifyCanvasHash,
    string YouTubeInnertubeApiKey, string YouTubeAndroidClientVersion,
    IReadOnlyList<string> PipedInstances, IReadOnlyList<string> InvidiousInstances)
{
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
        if (data.SchemaVersion != 1 || data.SpotifyTotpVersion is not { Length: > 0 and <= 8 } version ||
            version.Any(c => c is < '0' or > '9') ||
            data.SpotifyTotpSecretBase64 is not { Length: > 0 and <= 512 } secret ||
            Convert.FromBase64String(secret).Length is < 16 or > 256 ||
            !IsHash(data.SpotifyFindTracksHash) || !IsHash(data.SpotifyCanvasHash) ||
            data.YouTubeInnertubeApiKey is not { Length: > 0 and <= 256 } key ||
            key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-') ||
            data.YouTubeAndroidClientVersion is not { Length: > 0 and <= 32 } clientVersion ||
            clientVersion.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new JsonException("Invalid protocol configuration.");

        return new(version, secret, data.SpotifyFindTracksHash!, data.SpotifyCanvasHash!, key, clientVersion,
            ValidateHosts(data.PipedInstances), ValidateHosts(data.InvidiousInstances));
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static IReadOnlyList<string> ValidateHosts(string[]? hosts)
    {
        if (hosts == null || hosts.Length > 16 || hosts.Any(host =>
            string.IsNullOrWhiteSpace(host) || host.Length > 253 || host != host.Trim() ||
            !host.Contains('.') || host.EndsWith('.') || Uri.CheckHostName(host) != UriHostNameType.Dns ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)))
            throw new JsonException("Invalid fallback hosts.");
        return Array.AsReadOnly(hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed class ProtocolDocument
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
