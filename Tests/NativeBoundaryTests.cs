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

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_win32_keyboard_hook_uninstall(int slot);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_win32_win_event_hook_uninstall(int slot);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_clipboard_hash_create(out long handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_clipboard_hash_close(long handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_clipboard_index_create(out long handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_clipboard_index_close(long handle);

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
        Assert.True(info.Minor >= 1);
        var oldDescriptor = info;
        oldDescriptor.Minor = 0;
        Assert.Throws<BadImageFormatException>(() => NativeAbi.Validate(oldDescriptor));
        oldDescriptor = info;
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

    [Fact]
    public void Win32HooksRecycleSlotsAcrossRepeatedInstallAndDispose()
    {
        for (int i = 0; i < 6; i++)
        {
            using var hook = new Win32Interop.LowLevelKeyboardHook((_, _) => false);
            if (!hook.Install()) return; // Restricted desktop/session without hook permission.
            Assert.True(hook.IsInstalled);
            hook.Dispose();
            hook.Dispose();
            Assert.False(hook.IsInstalled);
        }

        for (int i = 0; i < 10; i++)
        {
            using var winEventHook = new Win32Interop.WinEventHook(0x0003, 0x0003, (_, _) => { });
            if (!winEventHook.Install()) return;
            Assert.True(winEventHook.IsInstalled);
            winEventHook.Dispose();
            winEventHook.Dispose();
            Assert.False(winEventHook.IsInstalled);
        }

        Assert.Equal((int)NativeStatus.InvalidArgument, vn_win32_keyboard_hook_uninstall(-1));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_win32_keyboard_hook_uninstall(99));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_win32_win_event_hook_uninstall(-1));
        Assert.Equal((int)NativeStatus.InvalidArgument, vn_win32_win_event_hook_uninstall(99));
    }

    [Fact]
    public void HandleRegistriesRecycleSlotsAndRejectStaleHandles()
    {
        long previousHash = 0;
        long previousIndex = 0;
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal((int)NativeStatus.Ok, vn_clipboard_hash_create(out long hashHandle));
            Assert.True(hashHandle > 0);
            Assert.NotEqual(previousHash, hashHandle);
            Assert.Equal((int)NativeStatus.Ok, vn_clipboard_hash_close(hashHandle));
            Assert.Equal((int)NativeStatus.InvalidArgument, vn_clipboard_hash_close(hashHandle));
            previousHash = hashHandle;

            Assert.Equal((int)NativeStatus.Ok, vn_clipboard_index_create(out long indexHandle));
            Assert.True(indexHandle > 0);
            Assert.NotEqual(previousIndex, indexHandle);
            Assert.Equal((int)NativeStatus.Ok, vn_clipboard_index_close(indexHandle));
            Assert.Equal((int)NativeStatus.InvalidArgument, vn_clipboard_index_close(indexHandle));
            previousIndex = indexHandle;
        }
    }

    [Fact]
    public void MediaPolicyNativeRoundTripsEdgeAndNormalInputs()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var adDecision = MediaPolicyNative.EvaluateLikelyYouTubeAd(new MediaPolicyNative.YouTubeAdEvaluationRequest(
            IsBrowserSession: true,
            HasStableTrack: true,
            LastSource: "YouTube",
            LastPublishedSessionInstanceKey: "k1",
            CurrentSessionInstanceKey: "k2",
            CurrentTrack: "Sponsor Break",
            CurrentDuration: TimeSpan.FromSeconds(15),
            HasYouTubeWindow: true,
            CurrentTrackMatchesYouTubeWindow: false,
            TransitionStartedUtc: DateTime.MinValue,
            NowUtc: now));
        Assert.True(adDecision.ShouldHold);
        Assert.Equal(now, adDecision.TransitionStartedUtc);

        var nonCandidate = MediaPolicyNative.EvaluateLikelyYouTubeAd(
            isBrowserSession: true,
            hasStableTrack: true,
            lastSource: null,
            lastPublishedSessionInstanceKey: null,
            currentSessionInstanceKey: null,
            currentTrack: null,
            currentDuration: TimeSpan.Zero,
            hasYouTubeWindow: false,
            currentTrackMatchesYouTubeWindow: false,
            transitionStartedUtc: DateTime.MinValue,
            nowUtc: now);
        Assert.False(nonCandidate.ShouldHold);

        var junkDecision = MediaPolicyNative.EvaluateYouTubeJunkMetadata(
            isBrowserSession: true,
            hasStableTrack: false,
            lastSource: null,
            hasYouTubeWindow: false,
            transitionStartedUtc: DateTime.MinValue,
            nowUtc: now);
        Assert.False(junkDecision.ShouldHold);

        Assert.False(MediaPolicyNative.ShouldCarryYouTubeSource(
            isBrowserSession: false,
            currentBrowserPlatformHint: null,
            lastSource: null,
            stableSource: null,
            isCompletingAdTransition: false));
    }
}
