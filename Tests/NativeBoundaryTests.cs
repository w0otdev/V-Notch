using VNotch.Models;
using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Interop;
using Xunit;

namespace VNotch.Tests;

public sealed partial class NativeBoundaryTests
{
    [Fact]
    public void LegacyLibraryWithoutDescriptorIsRejected() =>
        Assert.Throws<BadImageFormatException>(() => NativeAbi.LoadCheckedModule(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll")));

    [Fact]
    public void MediaPoliciesDoNotOwnNativeImportsOrMutableState()
    {
        foreach (var type in new[] { typeof(BrowserMediaTransitionPolicy), typeof(MediaTimelinePolicy) })
        {
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.Null(method.GetCustomAttribute<LibraryImportAttribute>());
                Assert.Null(method.GetCustomAttribute<DllImportAttribute>());
            }
            Assert.All(type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                field => Assert.True(field.IsInitOnly || field.IsLiteral, $"Mutable policy state: {field.Name}"));
        }
    }

    [LibraryImport(NativeAbi.LibraryName)]
    private static unsafe partial int vn_abi_query(uint capacity, NativeAbi.Info* info);

    [Fact]
    public unsafe void ShippedDescriptorMatchesManagedLayoutAndRejectsShortBuffersWithoutWriting()
    {
        NativeAbi.Info info = default;
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_abi_query(24, null));
        info.Major = 99;
        Assert.Equal((int)NativeStatus.BufferTooSmall, vn_abi_query(23, &info));
        Assert.Equal(99u, info.Major);
        Assert.Equal((int)NativeStatus.Ok, vn_abi_query((uint)sizeof(NativeAbi.Info), &info));
        NativeAbi.Validate(info);
        Assert.Equal(24, sizeof(NativeAbi.Info));
        Assert.Equal(2u, info.Major);
        var oldDescriptor = info;
        oldDescriptor.Major = 1;
        Assert.Throws<BadImageFormatException>(() => NativeAbi.Validate(oldDescriptor));
        info.Minor = 100; // Additive releases retain this fixed descriptor and existing exports.
        NativeAbi.Validate(info);
    }

    [Theory]
    [InlineData(20, 2, 8, 1, 1)]
    [InlineData(24, 1, 8, 1, 1)]
    [InlineData(24, 2, 4, 1, 1)]
    [InlineData(24, 2, 8, 2, 1)]
    [InlineData(24, 2, 8, 1, 2)]
    public void IncompatibleDescriptorsFailBeforeAnyFeatureCall(uint size, uint major, uint pointer, uint handles, uint status) =>
        Assert.Throws<BadImageFormatException>(() => NativeAbi.Validate(new NativeAbi.Info
        { Size = size, Major = major, Minor = 1, PointerSize = pointer, RegistryHandleVersion = handles, StatusVersion = status }));

    [Theory]
    [InlineData(-1, "InvalidArgument")]
    [InlineData(-2, "BufferTooSmall")]
    [InlineData(-3, "Failure")]
    [InlineData(-99, "-99")]
    public void StatusFailuresCannotMasqueradeAsPolicyDecisions(int status, string name)
    {
        var error = Assert.Throws<InvalidOperationException>(() => NativeResult.RequireSuccess(status, "test"));
        Assert.Contains(name, error.Message);
        NativeResult.RequireSuccess(0, "test");
    }

    [Fact]
    public void TimelineBoundaryAgreesWithNativeIncludingLongRecordings()
    {
        Assert.False(MediaTimelinePolicy.HasFiniteDuration(TimeSpan.Zero));
        Assert.False(MediaTimelinePolicy.HasFiniteDuration(TimeSpan.FromTicks(-1)));
        Assert.True(MediaTimelinePolicy.HasFiniteDuration(TimeSpan.FromHours(13)));
        Assert.True(MediaTimelinePolicy.HasFiniteDuration(MediaTimelinePolicy.MaxReasonableDuration));
        Assert.False(MediaTimelinePolicy.HasFiniteDuration(MediaTimelinePolicy.MaxReasonableDuration + TimeSpan.FromTicks(1)));
        Assert.False(MediaTimelinePolicy.HasFiniteDuration(TimeSpan.MaxValue));
        Assert.True(MediaTimelinePolicy.IsIndeterminate(TimeSpan.FromMinutes(3), true));
        Assert.False(MediaTimelinePolicy.IsIndeterminate(TimeSpan.FromMinutes(3), false));
    }

    [Theory]
    [InlineData(true, 0, (int)GlassGpuRenderOutcome.Submit)]
    [InlineData(false, 0, (int)GlassGpuRenderOutcome.Submit)]
    [InlineData(true, 1, (int)GlassGpuRenderOutcome.KeepPrevious)]
    [InlineData(false, 1, (int)GlassGpuRenderOutcome.KeepPrevious)]
    [InlineData(true, GlassGpuResultPolicy.Pending, (int)GlassGpuRenderOutcome.UseBackdropFallback)]
    [InlineData(false, GlassGpuResultPolicy.Pending, (int)GlassGpuRenderOutcome.Fail)]
    [InlineData(true, unchecked((int)0x887A0005), (int)GlassGpuRenderOutcome.Fail)]
    [InlineData(false, unchecked((int)0x887A0005), (int)GlassGpuRenderOutcome.Fail)]
    public void GlassFallbackOnlyAcceptsPendingPerWindowCapture(bool windows, int hr, int outcome) =>
        Assert.Equal((GlassGpuRenderOutcome)outcome, GlassGpuResultPolicy.Classify(windows, hr));
}
