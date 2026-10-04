using System.Globalization;
using System.Text;

namespace VNotch.Services;

internal static class SpotifyWebPlayerProtocol
{
    internal const string TotpVersion = "61";

    internal static byte[] CreateTotpSecret()
    {
        ReadOnlySpan<byte> values =
        [44, 55, 47, 42, 70, 40, 34, 114, 76, 74, 50, 111, 120, 97, 75, 76,
         94, 102, 43, 69, 49, 120, 118, 80, 64, 78];
        var decoded = new StringBuilder(values.Length * 3);
        for (int i = 0; i < values.Length; i++)
            decoded.Append((values[i] ^ ((i % 33) + 9)).ToString(CultureInfo.InvariantCulture));
        return Encoding.UTF8.GetBytes(decoded.ToString());
    }
}
