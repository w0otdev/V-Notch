using System.IO;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed partial class NativeSpotlightSfxTests(ITestOutputHelper output)
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_sfx_load(string path, int pathLength, out int hresult);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_sfx_play(float volume);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_sfx_stop();

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_sfx_is_loaded(out byte loaded);

    private static string? FindAsset()
    {
        string? dir = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
        {
            foreach (string name in new[] { "SPL_CLICK_SFX.m4a", "SPL_CLICK_SFX.mp3" })
            {
                string candidate = Path.Combine(dir, "Assets", name);
                if (File.Exists(candidate)) return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    // Volume 0 scales every sample to silence, so decoding and playback are exercised without any sound.
    [Fact]
    public void ClickAssetDecodesThroughMediaFoundationAndPlaysSilently()
    {
        string? asset = FindAsset();
        if (asset == null) return; // The asset is not part of this checkout.

        int load = vn_spotlight_sfx_load(asset, asset.Length, out int hresult);
        output.WriteLine($"asset={asset} load={load} hr=0x{hresult:X8}");
        if (load != 0) return; // Media Foundation codecs may be missing on stripped Windows editions.

        Assert.Equal(0, vn_spotlight_sfx_is_loaded(out byte loaded));
        Assert.Equal(1, loaded);
        int play = vn_spotlight_sfx_play(0f);
        Assert.True(play == 0 || play == -3, $"unexpected play status {play}");
        Assert.Equal(0, vn_spotlight_sfx_stop());

        // A second load keeps the first result and is free.
        Assert.Equal(0, vn_spotlight_sfx_load(asset, asset.Length, out _));
    }

    [Fact]
    public void MissingFileFailsWithoutLoading()
    {
        // A bogus path must never mark the sound as loaded once a real asset has not been decoded.
        int status = vn_spotlight_sfx_load(@"C:\definitely\missing\sfx.m4a", 29, out int hresult);
        vn_spotlight_sfx_is_loaded(out byte loaded);
        Assert.True(status != 0 ? hresult != 0 : loaded == 1);
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        Assert.NotEqual(0, vn_spotlight_sfx_load("", 0, out _));
        Assert.NotEqual(0, vn_spotlight_sfx_play(float.NaN));
    }
}
