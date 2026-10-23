namespace VNotch.Services;

internal static class SpotifyWebPlayerProtocol
{
    private static readonly byte[] Secret = MediaProtocolConfiguration.Current.CreateSpotifyTotpSecret();
    internal static string TotpVersion => MediaProtocolConfiguration.Current.SpotifyTotpVersion;

    internal static ReadOnlySpan<byte> TotpSecret => Secret;

    internal static byte[] CreateTotpSecret() => Secret.ToArray();
}
