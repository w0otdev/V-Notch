using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyProtocolTests
{
    [Fact]
    public void FixedTotpSecretMatchesTheProtocolAndCannotBeMutatedThroughACopy()
    {
        const string expected = "376136387538459893883312310911992847112448894410210511297108";
        var copy = SpotifyWebPlayerProtocol.CreateTotpSecret();
        Assert.Equal(expected, Encoding.UTF8.GetString(copy));
        copy[0] = 0;
        Assert.Equal(expected, Encoding.UTF8.GetString(SpotifyWebPlayerProtocol.TotpSecret));
        Assert.Equal(expected, Encoding.UTF8.GetString(SpotifyWebPlayerProtocol.CreateTotpSecret()));
    }

    // RFC 6238, Appendix B, SHA-1 vectors reduced to the protocol's six digits.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void GenerateTotp_MatchesKnownVectors(long seconds, string expected)
    {
        Assert.Equal(expected, SpotifyTokenProvider.GenerateTotp(
            "12345678901234567890"u8, seconds * 1000));
    }

    [Theory]
    [InlineData(22, "0A260A24")]
    [InlineData(111, "0A7F0A7D")]
    [InlineData(112, "0A80010A7E")]
    [InlineData(113, "0A81010A7F")]
    [InlineData(114, "0A83010A8001")]
    [InlineData(16370, "0A8480010A808001")]
    public void BuildCanvasRequest_EncodesNestedLengths(int idLength, string header)
    {
        string trackId = new('x', idLength);
        byte[] expected = [.. Convert.FromHexString(header), .. Encoding.UTF8.GetBytes("spotify:track:" + trackId)];

        Assert.Equal(expected, SpotifyCanvasProtocol.BuildCanvasRequest(trackId));
    }

    [Fact]
    public void BuildCanvasRequest_LengthsCountUtf8Bytes()
    {
        byte[] expected = [0x0A, 0x16, 0x0A, 0x14, .. Encoding.UTF8.GetBytes("spotify:track:é🎵")];
        Assert.Equal(expected, SpotifyCanvasProtocol.BuildCanvasRequest("é🎵"));
    }

    [Fact]
    public void BuildCanvasRequest_AllocatesOnlyTheResultBuffer()
    {
        const string trackId = "3OHfY25tqY28d16oZczHc8";
        _ = SpotifyCanvasProtocol.BuildCanvasRequest(trackId);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[]? last = null;
        for (int i = 0; i < 1000; i++)
            last = SpotifyCanvasProtocol.BuildCanvasRequest(trackId);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(40, last!.Length);
        Assert.InRange(allocated, 1, 80_000);
    }
}
