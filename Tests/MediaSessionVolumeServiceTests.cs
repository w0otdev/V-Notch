using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaSessionVolumeServiceTests
{
    [Theory]
    [InlineData("Chrome.Profile.1", "chrome")]
    [InlineData("MSEdge.Dev", "msedge")]
    [InlineData("Brave.Browser", "brave")]
    [InlineData("Firefox.Profile", "firefox")]
    [InlineData("Opera GXStable", "opera")]
    [InlineData("Vivaldi.Profile", "vivaldi")]
    [InlineData("Chromium.Profile", "chromium")]
    [InlineData("CocCoc", "browser")]
    [InlineData("Arc", "arc")]
    [InlineData("Sidekick", "sidekick")]
    [InlineData("Zen", "zen")]
    [InlineData("Floorp", "floorp")]
    [InlineData("LibreWolf", "librewolf")]
    [InlineData("Waterfox", "waterfox")]
    [InlineData("MullvadBrowser", "mullvadbrowser")]
    [InlineData("Thorium", "thorium")]
    [InlineData("custom-browser", "custom-browser")]
    public void BrowserAppIdsResolveTheirAudioExecutableWhenProcessIdsAreUnavailable(string source, string executable)
    {
        var state = new VolumeState { Value = .65f };
        var unrelated = new VolumeState { Value = .9f };
        var fixture = new Fixture(Session(unrelated, 20, @"C:\apps\unrelated.exe"),
            Session(state, 21, $@"{{endpoint}}.\\Device\\HarddiskVolume3\Browsers\{executable}.exe%b{{session}}"))
        { ProcessIds = [] };
        using var service = fixture.Service;
        Assert.True(service.TryGetVolume(source, out float volume, out _));
        Assert.Equal(.65f, volume);
        Assert.True(service.TrySetVolume(source, .4f));
        Assert.Equal(.4f, state.Value);
        Assert.Equal(.9f, unrelated.Value);
    }

    [Fact]
    public void PlaybackMovingToAnotherSessionBypassesTheInactiveCachedVolumeImmediately()
    {
        var old = new VolumeState { Value = .3f };
        var current = new VolumeState { Value = .6f };
        var fixture = new Fixture(Session(old));
        using var service = fixture.Service;
        const string source = "fixture.opaque.identity";
        Assert.True(service.TrySetVolume(source, .4f));
        old.Active = false;
        fixture.ProcessIds = [22];
        fixture.Sessions = [Session(old) with { State = 0 }, Session(current, process: 22, identifier: "new-session")];
        Assert.True(service.TrySetVolume(source, .7f));
        Assert.Equal(.7f, current.Value);
        Assert.Equal(.4f, old.Value);
        Assert.Equal(1, old.Disposals);
    }

    [Fact]
    public void ActiveSessionOutranksStickyInactiveSessionEvenWhenMutedAndExpiredSessionsAreIgnored()
    {
        var old = new VolumeState { Value = .3f };
        var current = new VolumeState { Value = 0, Muted = true };
        var fixture = new Fixture(Session(old));
        using var service = fixture.Service;
        Assert.True(service.TryGetVolume("fixture.exe", out _, out _));
        fixture.Sessions = [Session(old) with { State = 0 }, Session(current, identifier: "new-session")];
        Assert.True(service.TryGetVolume("fixture.exe", out float volume, out bool muted));
        Assert.Equal(0, volume);
        Assert.True(muted);
        fixture.Sessions = [Session(old) with { State = 2 }];
        Assert.False(service.TryGetVolume("fixture.exe", out _, out _));
        Assert.False(service.TrySetVolume("fixture.exe", .8f));
    }

    [Fact]
    public void NativeProcessLookupResolvesAnUnlistedExecutableAndRejectsAnUnknownSource()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Contains((uint)process.Id, MediaSessionVolumeService.GetProcessIds(process.ProcessName + ".exe"));
        Assert.Empty(MediaSessionVolumeService.GetProcessIds("VNotch.NonexistentBrowser.7594D76B"));
    }

    [Theory]
    [InlineData("Chrome", "chrome_proxy.exe")]
    [InlineData("Chrome", "notchrome.exe")]
    [InlineData("edge", "knowledge.exe")]
    public void BrowserNamesCannotMatchAnUnrelatedExecutableBySubstring(string source, string executable)
    {
        var fixture = new Fixture(Session(new VolumeState(), identifier: @"C:\apps\" + executable)) { ProcessIds = [] };
        using var service = fixture.Service;
        Assert.False(service.TrySetVolume(source, .4f));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0.4, 0.4)]
    [InlineData(2, 1)]
    public void ReadsClampTheSessionVolumeAndReturnItsMuteState(float input, float expected)
    {
        var state = new VolumeState { Value = input, Muted = true };
        var fixture = new Fixture(Session(state));
        Assert.True(fixture.Service.TryGetVolume("fixture.exe", out float volume, out bool muted));
        Assert.Equal(expected, volume);
        Assert.True(muted);
        Assert.Equal(1, state.Disposals);
        Assert.Equal(1, fixture.SnapshotDisposals);
    }

    [Theory]
    [InlineData(-1, 0, true)]
    [InlineData(0.001, 0.001, true)]
    [InlineData(0.002, 0.002, false)]
    [InlineData(2, 1, false)]
    public void WritesClampVolumeAndUnmuteOnlyAboveTheSilenceThreshold(float input, float expected, bool muted)
    {
        var state = new VolumeState { Muted = true };
        var fixture = new Fixture(Session(state));
        Assert.True(fixture.Service.TrySetVolume("fixture.exe", input));
        Assert.Equal(expected, state.Value);
        Assert.Equal(muted, state.Muted);
        Assert.True(fixture.Service.TrySetVolume("FIXTURE.EXE", input));
        Assert.Equal(1, fixture.Enumerations);
        fixture.Service.InvalidateVolumeSessionCache();
        Assert.Equal(1, state.Disposals);
        fixture.Service.InvalidateVolumeSessionCache();
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public void CachedVolumeExpiresAndChangingTheSourceInvalidatesTheOldLease()
    {
        var state = new VolumeState();
        var fixture = new Fixture(Session(state), Session(state, process: 22, identifier: "other.exe"));
        Assert.True(fixture.Service.TrySetVolume("fixture.exe", 0.2f));
        fixture.Now = fixture.Now.AddMilliseconds(2999);
        Assert.True(fixture.Service.TrySetVolume("fixture.exe", 0.3f));
        Assert.Equal(1, fixture.Enumerations);
        fixture.Now = fixture.Now.AddMilliseconds(1);
        Assert.True(fixture.Service.TrySetVolume("fixture.exe", 0.4f));
        Assert.Equal(2, fixture.Enumerations);
        Assert.True(fixture.Service.TrySetVolume("other.exe", 0.5f));
        Assert.Equal(3, fixture.Enumerations);
        Assert.Equal(2, state.Disposals);
        fixture.Service.InvalidateVolumeSessionCache();
    }

    [Fact]
    public void FailedCachedLeaseIsDisposedAndAReplacementSessionCanRecover()
    {
        var old = new VolumeState();
        var latest = new VolumeState();
        var fixture = new Fixture(Session(old));
        Assert.True(fixture.Service.TrySetVolume("fixture.exe", 0.2f));
        old.FailWrites = true;
        fixture.Sessions = [Session(latest)];
        Assert.True(fixture.Service.TrySetVolume("fixture.exe", 0.7f));
        Assert.Equal(0.7f, latest.Value);
        Assert.Equal(1, old.Disposals);
        Assert.Equal(2, fixture.Enumerations);
        fixture.Service.InvalidateVolumeSessionCache();
    }

    [Fact]
    public void MuteToggleInvalidatesTheCachedVolumeAndDisposesItsOwnLease()
    {
        var state = new VolumeState();
        var fixture = new Fixture(Session(state));
        Assert.True(fixture.Service.TrySetVolume("fixture.exe", 0.5f));
        Assert.True(fixture.Service.TryToggleMute("fixture.exe"));
        Assert.True(state.Muted);
        Assert.Equal(2, state.Disposals);
        Assert.True(fixture.Service.TryToggleMute("fixture.exe"));
        Assert.False(state.Muted);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("identifier")]
    [InlineData("instance")]
    public void MetadataCanResolveASessionWhenItsProcessHasAlreadyExited(string match)
    {
        var state = new VolumeState { Value = 0.8f };
        var session = new MediaAudioSessionSnapshot(0, match == "display" ? "fixture.exe" : "",
            match == "identifier" ? "fixture.exe" : "", match == "instance" ? "prefix|fixture.exe|instance" : "", false,
            () => 0, () => new VolumeLease(state));
        var fixture = new Fixture(session) { ProcessIds = [] };
        Assert.True(fixture.Service.TryGetVolume("fixture.exe", out float volume, out _));
        Assert.Equal(0.8f, volume);
    }

    [Fact]
    public void SystemSoundsAndUnrelatedSessionsNeverBecomeTheMediaVolumeTarget()
    {
        var state = new VolumeState();
        var fixture = new Fixture(Session(state) with { IsSystemSoundsSession = true }, Session(state, 55, "unrelated.exe")) { ProcessIds = [] };
        Assert.False(fixture.Service.TryGetVolume("fixture.exe", out float volume, out bool muted));
        Assert.Equal(0, volume);
        Assert.False(muted);
        Assert.Equal(0, state.Disposals);
    }

    [Fact]
    public void ProcessMatchOutranksMetadataAndTheLastSelectedSessionRemainsStableWhenPeakLevelsChange()
    {
        var selected = new VolumeState { Value = 0.2f };
        var other = new VolumeState { Value = 0.9f };
        float selectedPeak = 1;
        float otherPeak = 0;
        var first = Session(selected, 11, "first") with { ReadPeak = () => selectedPeak };
        var second = Session(other, 11, "second") with { ReadPeak = () => otherPeak };
        var fixture = new Fixture(first, second);
        Assert.True(fixture.Service.TryGetVolume("fixture.exe", out float volume, out _));
        Assert.Equal(0.2f, volume);
        selectedPeak = 0;
        otherPeak = 1;
        Assert.True(fixture.Service.TryGetVolume("fixture.exe", out volume, out _));
        Assert.Equal(0.2f, volume);
        Assert.Equal(1, fixture.ProcessLookups);
        fixture.Now = fixture.Now.AddMilliseconds(1200);
        Assert.True(fixture.Service.TryGetVolume("fixture.exe", out _, out _));
        Assert.Equal(2, fixture.ProcessLookups);
    }

    [Fact]
    public void ADisconnectedPeakMeterDoesNotPreventVolumeControl()
    {
        var state = new VolumeState { Value = 0.6f };
        var fixture = new Fixture(Session(state) with { ReadPeak = () => throw new InvalidOperationException("Disconnected meter") });
        Assert.True(fixture.Service.TryGetVolume("fixture.exe", out float volume, out _));
        Assert.Equal(0.6f, volume);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void EmptySourcesDoNotOpenTheDefaultAudioEndpoint(string? source)
    {
        var fixture = new Fixture();
        Assert.False(fixture.Service.TryGetVolume(source!, out _, out _));
        Assert.False(fixture.Service.TrySetVolume(source!, 0.5f));
        Assert.False(fixture.Service.TryToggleMute(source!));
        Assert.Equal(0, fixture.Enumerations);
    }

    [Fact]
    public void MissingEndpointsAndDisconnectedVolumesReturnFalse()
    {
        var state = new VolumeState { FailReads = true, FailWrites = true, FailDisposal = true };
        var fixture = new Fixture(Session(state));
        Assert.False(fixture.Service.TryGetVolume("fixture.exe", out _, out _));
        Assert.False(fixture.Service.TrySetVolume("fixture.exe", 0.5f));
        fixture.Service.InvalidateVolumeSessionCache();
        fixture.FailEnumeration = true;
        Assert.False(fixture.Service.TryGetVolume("fixture.exe", out _, out _));
        Assert.False(fixture.Service.TryToggleMute("fixture.exe"));
        var empty = new Fixture();
        Assert.False(empty.Service.TryGetVolume("fixture.exe", out _, out _));
    }

    [Theory]
    [InlineData("edge", "msedge")]
    [InlineData("coccoc", "browser")]
    [InlineData("apple music", "AppleMusic")]
    [InlineData("spotify", "Spotify")]
    [InlineData("C:\\apps\\custom-player.exe!custom-player.exe", "custom-player")]
    public void SessionOwnersResolveAliasesAndExecutableNamesWithoutDuplicates(string source, string expected)
    {
        var names = (HashSet<string>)typeof(MediaSessionVolumeService).GetMethod("GetProcessNameCandidates", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [source])!;
        Assert.Contains(expected, names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentVolumeOperationsProcessCacheLookupsAndScoreCalculationsAreThreadSafe()
    {
        var chromeState = new ConcurrentVolumeState(0.5f);
        var edgeState = new ConcurrentVolumeState(0.7f);
        var chromeSessionA = new MediaAudioSessionSnapshot(
            101, "Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome!1", false,
            () => 0.4f, () => new ConcurrentVolumeLease(chromeState));
        var chromeSessionB = new MediaAudioSessionSnapshot(
            102, "Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome!2", false,
            () => 0.2f, () => new ConcurrentVolumeLease(chromeState));
        var edgeSession = new MediaAudioSessionSnapshot(
            201, "Edge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "msedge!1", false,
            () => 0.6f, () => new ConcurrentVolumeLease(edgeState));

        MediaAudioSessionSnapshot[] sessions = [chromeSessionA, chromeSessionB, edgeSession];
        string[] sources = ["chrome.exe", "msedge.exe", "CHROME.EXE"];
        long virtualClockMs = 0;
        var startGate = new ManualResetEventSlim(false);
        var exceptions = new ConcurrentBag<Exception>();

        using var service = new MediaSessionVolumeService(
            () => new MediaAudioSessions(sessions, static () => { }),
            source =>
            {
                bool isChrome = source.StartsWith("chrome", StringComparison.OrdinalIgnoreCase);
                uint basePid = isChrome ? 101u : 201u;
                return [basePid, basePid + 1, basePid + 2];
            },
            () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(Interlocked.Add(ref virtualClockMs, 150)));

        const int workerCount = 8;
        const int iterationsPerWorker = 250;
        var tasks = new Task[workerCount];

        for (int w = 0; w < workerCount; w++)
        {
            int workerId = w;
            tasks[w] = Task.Run(() =>
            {
                startGate.Wait();
                try
                {
                    for (int i = 0; i < iterationsPerWorker; i++)
                    {
                        string source = sources[(workerId + i) % sources.Length];
                        float targetVolume = ((workerId * 31 + i) % 100) / 100f;

                        if (i % 9 == 0)
                        {
                            chromeState.SetActive(false);
                        }
                        else if (i % 9 == 3)
                        {
                            chromeState.SetActive(true);
                        }

                        Assert.True(service.TryGetVolume(source, out float readVol, out _));
                        Assert.InRange(readVol, 0f, 1f);

                        Assert.True(service.TrySetVolume(source, targetVolume));

                        if (i % 5 == 0)
                        {
                            Assert.True(service.TryToggleMute(source));
                        }

                        if (i % 7 == 0)
                        {
                            service.InvalidateVolumeSessionCache();
                        }

                        HashSet<uint> pids = service.GetCachedProcessIdsForSourceApp(source);
                        Assert.NotEmpty(pids);
                        uint sum = 0;
                        foreach (uint pid in pids)
                        {
                            sum += pid;
                        }
                        Assert.True(sum > 0);

                        var targetSession = sessions[(workerId + i) % sessions.Length];
                        double score = service.CalculateSessionScore(targetSession, source, pids);
                        Assert.False(double.IsNaN(score));
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        }

        startGate.Set();
        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);

        chromeState.SetActive(true);
        Assert.True(service.TrySetVolume("chrome.exe", 0.35f));
        Assert.True(service.TryGetVolume("chrome.exe", out float finalVolume, out bool finalMuted));
        Assert.Equal(0.35f, finalVolume, 3);
        Assert.False(finalMuted);
        Assert.Contains(101u, service.GetCachedProcessIdsForSourceApp("chrome.exe"));
    }

    private static MediaAudioSessionSnapshot Session(VolumeState state, uint process = 11, string identifier = "fixture.exe") =>
        new(process, "", identifier, identifier + "!instance", false, () => 0, () => new VolumeLease(state));

    private sealed class Fixture
    {
        internal DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal IReadOnlyList<MediaAudioSessionSnapshot> Sessions { get; set; }
        internal HashSet<uint> ProcessIds { get; set; } = [11];
        internal bool FailEnumeration { get; set; }
        internal int Enumerations { get; private set; }
        internal int ProcessLookups { get; private set; }
        internal int SnapshotDisposals { get; private set; }
        internal MediaSessionVolumeService Service { get; }
        internal Fixture(params MediaAudioSessionSnapshot[] sessions)
        {
            Sessions = sessions;
            Service = new(() =>
            {
                Enumerations++;
                if (FailEnumeration) throw new InvalidOperationException("Missing endpoint");
                return new MediaAudioSessions(Sessions, () => SnapshotDisposals++);
            }, _ => { ProcessLookups++; return ProcessIds; }, () => Now);
        }
    }

    private sealed class VolumeState
    {
        internal bool Active = true;
        internal float Value;
        internal bool Muted;
        internal bool FailReads;
        internal bool FailWrites;
        internal bool FailDisposal;
        internal int Disposals;
        internal void RecordDisposal()
        {
            Disposals++;
            if (FailDisposal) throw new InvalidOperationException("Already disconnected");
        }
    }
    private sealed class VolumeLease(VolumeState state) : IMediaAudioVolume
    {
        public bool IsActive => state.Active;
        public float Volume
        {
            get => state.FailReads ? throw new InvalidOperationException("Disconnected") : state.Value;
            set
            {
                if (state.FailWrites) throw new InvalidOperationException("Disconnected");
                state.Value = value;
            }
        }
        public bool Mute { get => state.Muted; set => state.Muted = value; }
        public void Dispose() => state.RecordDisposal();
    }

    private sealed class ConcurrentVolumeState(float initialVolume)
    {
        private int _active = 1;
        private float _volume = initialVolume;
        private int _muted;

        public bool IsActive => Volatile.Read(ref _active) != 0;
        public void SetActive(bool active) => Interlocked.Exchange(ref _active, active ? 1 : 0);

        public float Volume
        {
            get => Volatile.Read(ref _volume);
            set => Volatile.Write(ref _volume, value);
        }

        public bool Mute
        {
            get => Volatile.Read(ref _muted) != 0;
            set => Interlocked.Exchange(ref _muted, value ? 1 : 0);
        }
    }

    private sealed class ConcurrentVolumeLease(ConcurrentVolumeState state) : IMediaAudioVolume
    {
        public bool IsActive => state.IsActive;
        public float Volume
        {
            get => state.Volume;
            set => state.Volume = value;
        }
        public bool Mute
        {
            get => state.Mute;
            set => state.Mute = value;
        }
        public void Dispose() { }
    }
}
