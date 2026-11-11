using System.IO;
using System.Xml.Linq;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationNativeRuntimeTests
{
    [Fact]
    public void CandidatesAreAbsoluteAndStayInsideBundledRuntime()
    {
        string root = Path.Combine(Path.GetTempPath(), "VNotchRuntimeTest");
        var paths = TranslationNativeRuntime.Candidates(root, true, true, false).ToArray();
        Assert.Equal(new[] { "vulkan", "avx2", "noavx" }, paths.Select(p => new DirectoryInfo(Path.GetDirectoryName(p)!).Name));
        Assert.All(paths, p => Assert.StartsWith(Path.Combine(root, "runtimes", "win-x64", "native"), p));
        Assert.All(paths, p => Assert.True(Path.IsPathFullyQualified(p)));
        Assert.Equal(2, TranslationNativeRuntime.Candidates(root, false, false, false).Count());
    }

    [Fact]
    public void PoisonedNativeInitializerIsReportedAsRuntimeFailure()
    {
        var error = new TypeInitializationException("LLama.Native.NativeApi", new Exception("private payload"));
        Assert.True(TranslationNativeRuntime.IsLoadFailure(error));
        Assert.True(TranslationNativeRuntime.IsLoadFailure(new AggregateException(error)));
        Assert.True(TranslationNativeRuntime.IsLoadFailure(new DllNotFoundException()));
        Assert.False(TranslationNativeRuntime.IsLoadFailure(new InvalidOperationException()));
        Assert.False(TranslationNativeRuntime.IsLoadFailure(new TypeInitializationException("Other", null)));
    }

    [Fact]
    public void OnDemandVulkanPackageVersionMatchesManagedLLamaSharpVersion()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "V-Notch.csproj")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        XDocument csproj = XDocument.Load(Path.Combine(dir!, "V-Notch.csproj"));
        string? llamaVersion = csproj.Descendants("PackageReference")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("Include"), "LLamaSharp", StringComparison.Ordinal))
            ?.Attribute("Version")?.Value;
        string? vulkanPkgVersion = csproj.Descendants("PackageReference")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("Include"), "LLamaSharp.Backend.Vulkan.Windows", StringComparison.Ordinal))
            ?.Attribute("Version")?.Value;

        Assert.False(string.IsNullOrWhiteSpace(llamaVersion));
        Assert.Null(vulkanPkgVersion);
        Assert.Contains($"/{llamaVersion}/llamasharp.backend.vulkan.windows.{llamaVersion}.nupkg", TranslationNativeRuntime.VulkanNupkgUrl, StringComparison.Ordinal);
    }
}
