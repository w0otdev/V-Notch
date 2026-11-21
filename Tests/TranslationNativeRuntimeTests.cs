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
        var paths = TranslationNativeRuntime.Candidates(root, true, false).ToArray();
        Assert.Equal(new[] { "vulkan", "avx2", "noavx" }, paths.Select(p => new DirectoryInfo(Path.GetDirectoryName(p)!).Name));
        Assert.All(paths, p => Assert.StartsWith(Path.Combine(root, "runtimes", "win-x64", "native"), p));
        Assert.All(paths, p => Assert.True(Path.IsPathFullyQualified(p)));
        Assert.Equal(2, TranslationNativeRuntime.Candidates(root, false, false).Count());
    }

    [Theory]
    [InlineData(true, "avx2")]
    [InlineData(false, "noavx")]
    public void VulkanLoadsItsBaseBeforeTheBundledCpuDependency(bool avx2, string instructionSet)
    {
        string appRoot = Path.Combine(Path.GetTempPath(), "VNotchRuntimeTest");
        string vulkan = Path.Combine(Path.GetTempPath(), "downloaded-runtime", "vulkan", "llama.dll");
        string baseDll = Path.Combine(Path.GetDirectoryName(vulkan)!, "ggml-base.dll");
        string cpuDll = Path.Combine(appRoot, "runtimes", "win-x64", "native", instructionSet, "ggml-cpu.dll");
        var held = new HashSet<IntPtr>();
        var released = new List<IntPtr>();
        IntPtr Load(string path)
        {
            // Reproduce Windows' dependency resolution: Vulkan cannot load by itself,
            // and loading CPU first would select the wrong ggml-base.dll.
            IntPtr handle = path == baseDll ? 1 : path == cpuDll && held.Contains(1) ? 2 :
                path == vulkan && held.Contains(1) && held.Contains(2) ? 3 : 0;
            if (handle != IntPtr.Zero) held.Add(handle);
            return handle;
        }
        Assert.Equal(IntPtr.Zero, Load(vulkan));
        IntPtr result = TranslationNativeRuntime.LoadVulkanLibrary(vulkan, appRoot, avx2, Load,
            handle => { released.Add(handle); held.Remove(handle); });
        Assert.Equal((IntPtr)3, result);
        Assert.Equal(new IntPtr[] { 2, 1 }, released);
        Assert.Equal(new IntPtr[] { 3 }, held);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedVulkanLoadReleasesDependenciesBeforeCpuFallback(bool hasBase, bool hasCpu)
    {
        string appRoot = Path.Combine(Path.GetTempPath(), "VNotchRuntimeTest");
        string vulkan = Path.Combine(appRoot, "vulkan", "llama.dll");
        var held = new HashSet<IntPtr>();
        IntPtr Load(string path)
        {
            IntPtr handle = Path.GetFileName(path) switch
            {
                "ggml-base.dll" when hasBase => 1,
                "ggml-cpu.dll" when hasCpu => 2,
                _ => 0 // Driver/Vulkan dependency missing: the final library fails.
            };
            if (handle != IntPtr.Zero) held.Add(handle);
            return handle;
        }
        var result = TranslationNativeRuntime.LoadVulkanLibrary(vulkan, appRoot, true, Load, handle => held.Remove(handle));
        Assert.Equal(IntPtr.Zero, result);
        Assert.Empty(held);
    }

    [Fact]
    public void VulkanCanUseBaselineCpuDependencyWhenAvx2LibraryIsMissing()
    {
        string appRoot = Path.Combine(Path.GetTempPath(), "VNotchRuntimeTest");
        string vulkan = Path.Combine(appRoot, "vulkan", "llama.dll");
        var attempted = new List<string>();
        IntPtr Load(string path)
        {
            attempted.Add(path);
            return Path.GetFileName(Path.GetDirectoryName(path)) == "avx2" ? IntPtr.Zero : (IntPtr)1;
        }
        Assert.NotEqual(IntPtr.Zero, TranslationNativeRuntime.LoadVulkanLibrary(vulkan, appRoot, true, Load, _ => { }));
        Assert.Contains(Path.Combine(appRoot, "runtimes", "win-x64", "native", "noavx", "ggml-cpu.dll"), attempted);
        Assert.All(attempted, path => Assert.True(Path.IsPathFullyQualified(path)));
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
