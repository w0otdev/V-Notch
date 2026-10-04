namespace VNotch.Services;

internal static class SpotifyWebPlayerProtocol
{
    internal const string TotpVersion = "61";

    // Fixed protocol bytes, decoded from the version 61 configuration.
    internal static ReadOnlySpan<byte> TotpSecret => "376136387538459893883312310911992847112448894410210511297108"u8;

    internal static byte[] CreateTotpSecret() => TotpSecret.ToArray();
}
