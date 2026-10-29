using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

internal enum NetworkFeature { Updates, Artwork, Lyrics, Subtitles, Canvas, Weather, Ai, ExternalLinks, TranslationModels, CurrencyRates }

// One policy for production HTTP clients and out-of-process network features.
// Deny until startup has loaded the user's persisted settings.
internal sealed partial class NetworkPrivacy
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_netprivacy_allows(
        byte enableLocalOnlyMode,
        int feature,
        byte enableOnlineArtworkLookup,
        byte enableOnlineLyrics,
        byte allowOnlineSubtitles,
        byte allowOnlineCanvas,
        byte enableSpotifyCanvas,
        byte canvasConsentAccepted,
        byte allowOnlineWeather,
        byte allowOnlineAi,
        byte enableCurrencyConversion,
        out byte outAllowed);

    internal static NetworkPrivacy Current { get; } = new();
    private readonly object _gate = new();
    private NotchSettings _settings = new() { EnableLocalOnlyMode = true };
    private readonly Dictionary<NetworkFeature, CancellationTokenSource> _lifetimes =
        Enum.GetValues<NetworkFeature>().ToDictionary(f => f, _ => new CancellationTokenSource());

    internal static bool Allows(NotchSettings s, NetworkFeature feature) =>
        vn_netprivacy_allows(
            s.EnableLocalOnlyMode ? (byte)1 : (byte)0,
            (int)feature,
            s.EnableOnlineArtworkLookup ? (byte)1 : (byte)0,
            s.EnableOnlineLyrics ? (byte)1 : (byte)0,
            s.AllowOnlineSubtitles ? (byte)1 : (byte)0,
            s.AllowOnlineCanvas ? (byte)1 : (byte)0,
            s.EnableSpotifyCanvas ? (byte)1 : (byte)0,
            SpotifyCanvasConsent.HasAccepted(s) ? (byte)1 : (byte)0,
            s.AllowOnlineWeather ? (byte)1 : (byte)0,
            s.AllowOnlineAi ? (byte)1 : (byte)0,
            s.EnableCurrencyConversion ? (byte)1 : (byte)0,
            out byte allowed) == 0 && allowed != 0;

    internal bool IsAllowed(NetworkFeature feature) { lock (_gate) return Allows(_settings, feature); }

    internal void Apply(NotchSettings settings)
    {
        var revoked = new List<CancellationTokenSource>();
        lock (_gate)
        {
            foreach (var feature in Enum.GetValues<NetworkFeature>())
                if (Allows(_settings, feature) && !Allows(settings, feature))
                {
                    revoked.Add(_lifetimes[feature]);
                    _lifetimes[feature] = new CancellationTokenSource();
                }
            _settings = settings.Clone();
        }
        // Never invoke third-party cancellation callbacks under the policy lock.
        foreach (var source in revoked)
        {
            try { source.Cancel(); } catch (AggregateException) { /* Ignore cancellation callback exception */ }
            source.Dispose();
        }
    }

    internal CancellationToken Acquire(NetworkFeature feature)
    {
        lock (_gate)
        {
            if (!Allows(_settings, feature)) throw new HttpRequestException("Network access disabled by privacy settings.");
            return _lifetimes[feature].Token;
        }
    }

    internal static HttpMessageHandler Handler(NetworkFeature feature, HttpMessageHandler? inner = null) =>
        new PrivacyHandler(Current, feature, inner ?? new NativeWinHttpHandler());

    internal sealed class PrivacyHandler(NetworkPrivacy policy, NetworkFeature feature, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var permission = policy.Acquire(feature);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, permission);
            var response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (permission.IsCancellationRequested)
            {
                response.Dispose();
                permission.ThrowIfCancellationRequested();
            }
            response.Content = new PrivacyContent(response.Content, permission);
            return response;
        }
    }

    private sealed class PrivacyContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationToken _permission;
        internal PrivacyContent(HttpContent inner, CancellationToken permission)
        {
            _inner = inner;
            _permission = permission;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, _permission);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => CopyAsync(stream, cancellationToken);
        private async Task CopyAsync(Stream stream, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _permission);
            await _inner.CopyToAsync(stream, linked.Token).ConfigureAwait(false);
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(_permission);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _permission);
            return new PrivacyStream(await _inner.ReadAsStreamAsync(linked.Token).ConfigureAwait(false), _permission);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class PrivacyStream(Stream inner, CancellationToken permission) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { permission.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, permission);
            return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
