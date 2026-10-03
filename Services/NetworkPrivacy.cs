using System.IO;
using System.Net;
using System.Net.Http;
using VNotch.Models;

namespace VNotch.Services;

internal enum NetworkFeature { Updates, Artwork, Lyrics, Subtitles, Canvas, Weather, Ai, Copilot, ExternalLinks }

// One policy for production HTTP clients and out-of-process network features.
// Deny until startup has loaded the user's persisted settings.
internal sealed class NetworkPrivacy
{
    internal static NetworkPrivacy Current { get; } = new();
    private readonly object _gate = new();
    private NotchSettings _settings = new() { EnableLocalOnlyMode = true };
    private readonly Dictionary<NetworkFeature, CancellationTokenSource> _lifetimes =
        Enum.GetValues<NetworkFeature>().ToDictionary(f => f, _ => new CancellationTokenSource());

    internal static bool Allows(NotchSettings s, NetworkFeature feature) => !s.EnableLocalOnlyMode && feature switch
    {
        NetworkFeature.Artwork => s.EnableOnlineArtworkLookup,
        NetworkFeature.Lyrics => s.EnableOnlineLyrics,
        NetworkFeature.Subtitles => s.AllowOnlineSubtitles,
        NetworkFeature.Canvas => s.AllowOnlineCanvas && s.EnableSpotifyCanvas && SpotifyCanvasConsent.HasAccepted(s),
        NetworkFeature.Weather => s.AllowOnlineWeather,
        NetworkFeature.Ai => s.AllowOnlineAi,
        NetworkFeature.Copilot => s.AllowOnlineAi && s.AllowCopilot,
        _ => true
    };

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
            try { source.Cancel(); } catch (AggregateException) { }
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
        new PrivacyHandler(Current, feature, inner ?? new HttpClientHandler());

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
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, default);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => CopyAsync(stream, token);
        private async Task CopyAsync(Stream stream, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _permission);
            await _inner.CopyToAsync(stream, linked.Token).ConfigureAwait(false);
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(default);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _permission);
            return new PrivacyStream(await _inner.ReadAsStreamAsync(linked.Token).ConfigureAwait(false), _permission);
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class PrivacyStream(Stream inner, CancellationToken permission) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { permission.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, permission);
            return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
