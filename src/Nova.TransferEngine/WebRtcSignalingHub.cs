using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Nova.Core.Contracts;

namespace Nova.TransferEngine;

public sealed record WebRtcSignal(string SessionId, string Type, JsonElement Payload)
{
    public static WebRtcSignal Create<T>(string sessionId, string type, T payload) =>
        new(sessionId, type, JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}

public sealed class WebRtcSignalingHub : IDisposable
{
    private const int QueueCapacity = 128;
    private readonly ConcurrentDictionary<(string PeerId, string SessionId), Route> _routes = new();
    private readonly CancellationTokenSource _lifetime = new();

    public event Action<IncomingWebRtcSession>? IncomingSession;

    public Route Register(string peerId, string sessionId)
    {
        Validate(peerId, sessionId);
        var key = (peerId, sessionId);
        var route = _routes.GetOrAdd(key, _ => new Route());
        route.Touch();
        return route;
    }

    public Task PublishAsync(string peerId, WebRtcSignal signal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(peerId, signal.SessionId);
        if (signal.Type is not ("offer" or "sdp" or "candidate" or "accept" or "reject" or "cancel" or "closed" or "error"))
            throw new InvalidDataException("Unsupported WebRTC signaling message type.");
        var route = _routes.GetOrAdd((peerId, signal.SessionId), _ => new Route());
        route.Touch();
        if (!route.Messages.Writer.TryWrite(signal))
            throw new IOException("WebRTC signaling session queue is full.");
        if (signal.Type == "offer" && route.MarkIncomingRaised())
            IncomingSession?.Invoke(new IncomingWebRtcSession(peerId, signal.SessionId, route));
        return Task.CompletedTask;
    }

    public async Task<WebRtcSignal?> PollAsync(string peerId, string sessionId, TimeSpan timeout, CancellationToken token)
    {
        Validate(peerId, sessionId);
        if (!_routes.TryGetValue((peerId, sessionId), out var route)) return null;
        route.Touch();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeoutCts.CancelAfter(timeout);
        try { return await route.Messages.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested) { return null; }
    }

    public void Remove(string peerId, string sessionId)
    {
        if (_routes.TryRemove((peerId, sessionId), out var route)) route.Messages.Writer.TryComplete();
    }

    private static void Validate(string peerId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(peerId) || !Guid.TryParseExact(sessionId, "N", out _))
            throw new InvalidDataException("Invalid signaling peer or canonical session id.");
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var route in _routes.Values) route.Messages.Writer.TryComplete();
        _routes.Clear();
        _lifetime.Dispose();
    }

    public sealed class Route
    {
        private int _incomingRaised;
        internal DateTimeOffset LastTouched { get; private set; } = DateTimeOffset.UtcNow;
        internal Channel<WebRtcSignal> Messages { get; } = Channel.CreateBounded<WebRtcSignal>(new BoundedChannelOptions(QueueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
        internal void Touch() => LastTouched = DateTimeOffset.UtcNow;
        internal bool MarkIncomingRaised() => Interlocked.Exchange(ref _incomingRaised, 1) == 0;
        public IAsyncEnumerable<WebRtcSignal> ReadAllAsync(CancellationToken token = default) => Messages.Reader.ReadAllAsync(token);
        public ValueTask SendAsync(WebRtcSignal signal, CancellationToken token = default) => Messages.Writer.WriteAsync(signal, token);
    }
}

public sealed record IncomingWebRtcSession(string PeerDeviceId, string SessionId, WebRtcSignalingHub.Route Route);
