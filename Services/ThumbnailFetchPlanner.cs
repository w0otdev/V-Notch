using System.Runtime.InteropServices;

namespace VNotch.Services;

internal readonly struct ThumbnailSourceInputs
{
    public bool PlatformIsYouTube { get; init; }
    public bool PlatformIsBrowser { get; init; }
    public bool PlatformIsSoundCloud { get; init; }
    public bool HasSourceApp { get; init; }
    public bool IsLikelyYouTube { get; init; }
    public bool HasSoundCloudSessionOverride { get; init; }
    public bool BrowserHasYouTubeTabOpen { get; init; }
    public bool HasTrack { get; init; }
    public bool ThumbnailIsNullOrPlaceholder { get; init; }
}

internal readonly struct ThumbnailSourcePlan
{
    public bool IsPotentialYouTube { get; init; }
    public bool IsPotentialSoundCloud { get; init; }
}

internal readonly struct SoundCloudFetchInputs
{
    public bool IsNewSoundCloudTrack { get; init; }
    public bool SameTrackFetchRunning { get; init; }
    public bool ThumbnailIsNullOrPlaceholder { get; init; }
    public bool HasMismatchedThumbSource { get; init; }
    public bool RetryIntervalElapsed { get; init; }
}

internal readonly struct YouTubeFetchInputs
{
    public bool IsNewTrack { get; init; }
    public bool SameTrackFetchRunning { get; init; }
    public bool NeedsBetterThumbnail { get; init; }
    public bool RetryIntervalElapsed { get; init; }
}

internal static partial class ThumbnailFetchPlanner
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnThumbnailSourceInputs
    {
        public byte PlatformIsYouTube;
        public byte PlatformIsBrowser;
        public byte PlatformIsSoundCloud;
        public byte HasSourceApp;
        public byte IsLikelyYouTube;
        public byte HasSoundCloudSessionOverride;
        public byte BrowserHasYouTubeTabOpen;
        public byte HasTrack;
        public byte ThumbnailIsNullOrPlaceholder;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnThumbnailSourcePlan
    {
        public byte IsPotentialYouTube;
        public byte IsPotentialSoundCloud;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSoundCloudFetchInputs
    {
        public byte IsNewSoundCloudTrack;
        public byte SameTrackFetchRunning;
        public byte ThumbnailIsNullOrPlaceholder;
        public byte HasMismatchedThumbSource;
        public byte RetryIntervalElapsed;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnYouTubeFetchInputs
    {
        public byte IsNewTrack;
        public byte SameTrackFetchRunning;
        public byte NeedsBetterThumbnail;
        public byte RetryIntervalElapsed;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_thumbnail_classify_sources(in VnThumbnailSourceInputs x, out VnThumbnailSourcePlan outPlan);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_thumbnail_should_start_soundcloud(in VnSoundCloudFetchInputs x, out byte outShouldStart);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_thumbnail_should_start_youtube(in VnYouTubeFetchInputs x, out byte outShouldStart);

    public static ThumbnailSourcePlan ClassifySources(in ThumbnailSourceInputs x)
    {
        var nativeIn = new VnThumbnailSourceInputs
        {
            PlatformIsYouTube = x.PlatformIsYouTube ? (byte)1 : (byte)0,
            PlatformIsBrowser = x.PlatformIsBrowser ? (byte)1 : (byte)0,
            PlatformIsSoundCloud = x.PlatformIsSoundCloud ? (byte)1 : (byte)0,
            HasSourceApp = x.HasSourceApp ? (byte)1 : (byte)0,
            IsLikelyYouTube = x.IsLikelyYouTube ? (byte)1 : (byte)0,
            HasSoundCloudSessionOverride = x.HasSoundCloudSessionOverride ? (byte)1 : (byte)0,
            BrowserHasYouTubeTabOpen = x.BrowserHasYouTubeTabOpen ? (byte)1 : (byte)0,
            HasTrack = x.HasTrack ? (byte)1 : (byte)0,
            ThumbnailIsNullOrPlaceholder = x.ThumbnailIsNullOrPlaceholder ? (byte)1 : (byte)0,
        };
        _ = vn_thumbnail_classify_sources(in nativeIn, out VnThumbnailSourcePlan nativeOut);
        return new ThumbnailSourcePlan
        {
            IsPotentialYouTube = nativeOut.IsPotentialYouTube != 0,
            IsPotentialSoundCloud = nativeOut.IsPotentialSoundCloud != 0,
        };
    }

    public static bool ShouldStartSoundCloudFetch(in SoundCloudFetchInputs x)
    {
        var nativeIn = new VnSoundCloudFetchInputs
        {
            IsNewSoundCloudTrack = x.IsNewSoundCloudTrack ? (byte)1 : (byte)0,
            SameTrackFetchRunning = x.SameTrackFetchRunning ? (byte)1 : (byte)0,
            ThumbnailIsNullOrPlaceholder = x.ThumbnailIsNullOrPlaceholder ? (byte)1 : (byte)0,
            HasMismatchedThumbSource = x.HasMismatchedThumbSource ? (byte)1 : (byte)0,
            RetryIntervalElapsed = x.RetryIntervalElapsed ? (byte)1 : (byte)0,
        };
        _ = vn_thumbnail_should_start_soundcloud(in nativeIn, out byte shouldStart);
        return shouldStart != 0;
    }

    public static bool ShouldStartYouTubeFetch(in YouTubeFetchInputs x)
    {
        var nativeIn = new VnYouTubeFetchInputs
        {
            IsNewTrack = x.IsNewTrack ? (byte)1 : (byte)0,
            SameTrackFetchRunning = x.SameTrackFetchRunning ? (byte)1 : (byte)0,
            NeedsBetterThumbnail = x.NeedsBetterThumbnail ? (byte)1 : (byte)0,
            RetryIntervalElapsed = x.RetryIntervalElapsed ? (byte)1 : (byte)0,
        };
        _ = vn_thumbnail_should_start_youtube(in nativeIn, out byte shouldStart);
        return shouldStart != 0;
    }
}
