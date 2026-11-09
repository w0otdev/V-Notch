using System.Text.Json;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class LegacyVisualSettingsTests
{
    [Fact]
    public void RetiredFlagsAreIgnoredWhenLoadingOldSettingsAndOmittedOnSave()
    {
        var settings = JsonSerializer.Deserialize<NotchSettings>("""
            { "Width": 345, "EnableCursorBypass": true, "EnableAnimations": false,
              "EnableBounceEffect": false, "EnableGlowOnHover": false }
            """)!;
        Assert.Equal(345, settings.Width);
        string saved = JsonSerializer.Serialize(settings);
        foreach (string name in new[] { "EnableCursorBypass", "EnableAnimations", "EnableBounceEffect", "EnableGlowOnHover" })
            Assert.DoesNotContain(name, saved);
    }
}
