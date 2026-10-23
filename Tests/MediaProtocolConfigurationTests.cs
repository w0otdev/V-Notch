using System.IO;
using System.Text.Json.Nodes;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaProtocolConfigurationTests
{
    private static JsonObject Defaults()
    {
        using var stream = typeof(MediaMetadataLookupService).Assembly.GetManifestResourceStream("VNotch.MediaProtocols.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    [Fact]
    public void ShippedCompatibilityDataCanBeUpdatedAsOneValidatedSnapshot()
    {
        var json = Defaults();
        json["SpotifyTotpVersion"] = "62";
        json["SpotifyTotpSecretBase64"] = Convert.ToBase64String("replacement-protocol-secret"u8.ToArray());
        json["PipedInstances"] = new JsonArray("piped.example.org");
        json["InvidiousInstances"] = new JsonArray();
        var configuration = MediaProtocolConfiguration.Parse(json.ToJsonString());
        Assert.Equal("62", configuration.SpotifyTotpVersion);
        Assert.Equal("replacement-protocol-secret"u8.ToArray(), configuration.CreateSpotifyTotpSecret());
        Assert.Equal("piped.example.org", Assert.Single(configuration.PipedInstances));
        Assert.Empty(configuration.InvidiousInstances);
    }

    [Theory]
    [InlineData("http://example.org")]
    [InlineData("127.0.0.1")]
    [InlineData("example.org/path")]
    [InlineData("example.org:443")]
    [InlineData("host.localhost")]
    public void InvalidHostRejectsTheWholeFileAndRetainsWorkingDefaults(string host)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var defaults = MediaProtocolConfiguration.Current;
        try
        {
            var json = Defaults();
            json["PipedInstances"] = new JsonArray(host);
            File.WriteAllText(path, json.ToJsonString());
            Assert.Same(defaults, MediaProtocolConfiguration.Load(path, defaults));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidOrOversizedCompatibilityFileFallsBackWithoutAffectingStartup()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var defaults = MediaProtocolConfiguration.Current;
        try
        {
            File.WriteAllText(path, "{broken");
            Assert.Same(defaults, MediaProtocolConfiguration.Load(path, defaults));
            File.WriteAllText(path, new string(' ', 65537));
            Assert.Same(defaults, MediaProtocolConfiguration.Load(path, defaults));
            File.Delete(path);
            Assert.Same(defaults, MediaProtocolConfiguration.Load(path, defaults));
        }
        finally { File.Delete(path); }
    }
}
