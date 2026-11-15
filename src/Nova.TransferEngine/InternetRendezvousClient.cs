using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Nova.Core.Contracts;
using Nova.RendezvousProtocol;

namespace Nova.TransferEngine;

public enum InternetConnectionState { Disabled, Connecting, Connected, Reconnecting, Failed }
public sealed record InternetConnectionStatus(InternetConnectionState State, string? Detail = null);

/// <summary>Authenticated WSS control plane client. It carries presence and signaling only, never file data.</summary>
public sealed class InternetRendezvousClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private const int MaxFrameLength = 256 * 1024;
    private readonly DeviceIdentity _identity;
    private readonly PeerTrustStore _trust;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RendezvousFrame>> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _seenNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _seenMessages = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private Uri? _endpoint;
    private string? _serverCertificateFingerprint;
    private Task? _worker;
    private IReadOnlyList<RendezvousDevice> _devices = [];
    private InternetConnectionStatus _status = new(InternetConnectionState.Disabled);
    private bool _disposed;

    public InternetRendezvousClient(DeviceIdentity identity, PeerTrustStore trust) { _identity = identity; _trust = trust; }
    public event Action<InternetConnectionStatus>? StatusChanged;
    public event Action<IReadOnlyList<RendezvousDevice>>? DevicesChanged;
    public event Action<string, WebRtcSignal>? SignalReceived;
    public InternetConnectionStatus Status { get { lock (_stateGate) return _status; } }
    public IReadOnlyList<RendezvousDevice> Devices { get { lock (_stateGate) return _devices.ToArray(); } }

    public Task ConnectAsync(Uri endpoint, string? pinnedServerCertificateSha256 = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (endpoint.Scheme != Uri.UriSchemeWss || !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/v1/connect")
            throw new ArgumentException("Internet mode requires a wss:// endpoint ending in /v1/connect.", nameof(endpoint));
        if (_worker is { IsCompleted: false }) throw new InvalidOperationException("Internet rendezvous is already running.");
        if (pinnedServerCertificateSha256 is not null && !PeerTrustStore.IsValidFingerprint(pinnedServerCertificateSha256.Replace(":", "", StringComparison.Ordinal)))
            throw new ArgumentException("A pinned server certificate must be a SHA-256 fingerprint.", nameof(pinnedServerCertificateSha256));
        _serverCertificateFingerprint = pinnedServerCertificateSha256?.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
        _endpoint = endpoint;
        _worker = RunAsync(endpoint, _lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task SendSignalAsync(DiscoveredDevice recipient, WebRtcSignal signal, CancellationToken token)
    {
        var signed = WebRtcSignalSecurity.Sign(_identity, signal);
        await RequestAsync("signal", new RendezvousSignalRequest(recipient.DeviceId, signed), "signal-ack", token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetIceServerUrisAsync(CancellationToken token)
    {
        var frame = await RequestAsync("ice-credentials", new RendezvousIceRequest(), "ice-credentials", token).ConfigureAwait(false);
        var credentials = frame.ReadPayload<RendezvousIceCredentials>();
        if (credentials.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidDataException("TURN credentials are already expired.");
        var uris = credentials.StunUris.ToList();
        foreach (var raw in credentials.TurnUris)
        {
            if (string.IsNullOrWhiteSpace(credentials.TurnUsername) || string.IsNullOrWhiteSpace(credentials.TurnPassword))
                throw new InvalidDataException("The rendezvous service returned TURN URLs without temporary credentials.");
            var prefix = raw.StartsWith("turns:", StringComparison.OrdinalIgnoreCase) ? "turns:" : "turn:";
            var rest = raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? raw[prefix.Length..] : raw;
            uris.Add($"{prefix}{Uri.EscapeDataString(credentials.TurnUsername)}:{Uri.EscapeDataString(credentials.TurnPassword)}@{rest}");
        }
        if (uris.Count > 32 || uris.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("The ICE server list is invalid or too large.");
        return uris;
    }

    private async Task<RendezvousFrame> RequestAsync<T>(string type, T payload, string expectedType, CancellationToken token)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) throw new IOException("NOVA Network is not connected.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<RendezvousFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(id, completion)) throw new InvalidOperationException("Rendezvous request id collision.");
        try
        {
            await SendFrameAsync(socket, RendezvousFrame.Create(type, payload, id), token).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (response.Type == "error")
            {
                var error = response.ReadPayload<RendezvousError>();
                throw new IOException($"NOVA Network rejected the request ({error.Code}): {error.Message}");
            }
            if (response.Type != expectedType) throw new InvalidDataException($"Expected {expectedType}, received {response.Type}.");
            return response;
        }
        finally { _requests.TryRemove(id, out _); }
    }

    private async Task RunAsync(Uri endpoint, CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            SetStatus(attempt == 0 ? InternetConnectionState.Connecting : InternetConnectionState.Reconnecting,
                attempt == 0 ? "Connecting to NOVA Network" : "Reconnecting to NOVA Network");
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (_serverCertificateFingerprint is null) return errors == System.Net.Security.SslPolicyErrors.None;
                if (certificate is null || (errors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch |
                    System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                return string.Equals(fingerprint, _serverCertificateFingerprint, StringComparison.OrdinalIgnoreCase);
            };
            try
            {
                await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
                await AuthenticateAsync(socket, token).ConfigureAwait(false);
                _socket = socket;
                attempt = 0;
                SetStatus(InternetConnectionState.Connected, "Connected to NOVA Network");
                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var heartbeat = HeartbeatLoopAsync(socket, connectionCts.Token);
                try { await ReceiveLoopAsync(socket, connectionCts.Token).ConfigureAwait(false); }
                finally
                {
                    connectionCts.Cancel();
                    try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                    FailPending(new IOException("NOVA Network connection was lost."));
                    MarkDevicesOffline();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or CryptographicException or TimeoutException or FormatException or AuthenticationException)
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
                FailPending(ex);
                MarkDevicesOffline();
                SetStatus(attempt > 7 ? InternetConnectionState.Failed : InternetConnectionState.Reconnecting, ex.Message);
                var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt++, 5)));
                try { await Task.Delay(delay, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        _socket = null;
        MarkDevicesOffline();
        if (!_disposed) SetStatus(InternetConnectionState.Disabled, "Internet mode disconnected");
    }

    private async Task AuthenticateAsync(ClientWebSocket socket, CancellationToken token)
    {
        var hello = new RendezvousHello(_identity.Device.DeviceId, _identity.Device.DisplayName, Convert.ToBase64String(_identity.Certificate.RawData));
        await SendFrameAsync(socket, RendezvousFrame.Create("hello", hello), token).ConfigureAwait(false);
        var challengeFrame = await ReceiveFrameAsync(socket, token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (challengeFrame.Type != "challenge") throw new AuthenticationException("Rendezvous service did not issue an authentication challenge.");
        var challenge = challengeFrame.ReadPayload<RendezvousChallenge>().Nonce;
        var fingerprint = DeviceSignalCryptography.Fingerprint(_identity.Certificate);
        var proof = System.Text.Encoding.UTF8.GetBytes($"NOVA-RENDEZVOUS-v1\n{challenge}\n{_identity.Device.DeviceId}\n{fingerprint}");
        using var rsa = _identity.Certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Device identity has no private signing key.");
        var signature = Convert.ToBase64String(rsa.SignData(proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var contacts = CurrentContacts();
        await SendFrameAsync(socket, RendezvousFrame.Create("register", new RendezvousRegister(signature, contacts)), token).ConfigureAwait(false);
        var registered = await ReceiveFrameAsync(socket, token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (registered.Type == "error") throw new AuthenticationException(registered.ReadPayload<RendezvousError>().Message);
        if (registered.Type != "registered") throw new AuthenticationException("Rendezvous registration was not accepted.");
        var roster = await ReceiveFrameAsync(socket, token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (roster.Type == "devices") UpdateDevices(roster.ReadPayload<RendezvousDeviceList>().Devices);
        else if (roster.Type != "signal") throw new InvalidDataException("Rendezvous service did not provide the initial device roster.");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var frame = await ReceiveFrameAsync(socket, token).ConfigureAwait(false);
            switch (frame.Type)
            {
                case "devices": UpdateDevices(frame.ReadPayload<RendezvousDeviceList>().Devices); break;
                case "signal":
                    var delivery = frame.ReadPayload<RendezvousSignalDelivery>();
                    AcceptSignal(delivery);
                    await SendFrameAsync(socket, RendezvousFrame.Create("signal-received", new RendezvousSignalReceipt(delivery.MessageId)), token).ConfigureAwait(false);
                    break;
                case "signal-ack":
                case "ice-credentials":
                case "pong":
                    if (frame.RequestId is not null && _requests.TryGetValue(frame.RequestId, out var completion)) completion.TrySetResult(frame);
                    break;
                case "error":
                    if (frame.RequestId is not null && _requests.TryGetValue(frame.RequestId, out var failed)) failed.TrySetResult(frame);
                    else throw new IOException(frame.ReadPayload<RendezvousError>().Message);
                    break;
                default: throw new InvalidDataException($"Unexpected NOVA Network message: {frame.Type}.");
            }
        }
    }

    private void AcceptSignal(RendezvousSignalDelivery delivery)
    {
        if (!Guid.TryParseExact(delivery.MessageId, "N", out _) || !Guid.TryParseExact(delivery.Signal.Nonce, "N", out _))
            throw new InvalidDataException("Rendezvous delivered a non-canonical signal id.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var stale in _seenMessages.Where(x => x.Value < now - 180_000).Select(x => x.Key).ToArray()) _seenMessages.TryRemove(stale, out _);
        if (_seenMessages.ContainsKey(delivery.MessageId)) return;
        using var embedded = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(delivery.Signal.CertificateDer));
        var fingerprint = DeviceSignalCryptography.Fingerprint(embedded);
        var trusted = _trust.Find(fingerprint);
        if (trusted is null || trusted.Device.DeviceId != delivery.SenderDeviceId)
            throw new AuthenticationException("Rendezvous signal sender is not a trusted device.");
        var signal = WebRtcSignalSecurity.VerifyAndConvert(delivery.Signal, embedded, delivery.SenderDeviceId, trusted.Fingerprint);
        foreach (var stale in _seenNonces.Where(x => x.Value < now - 180_000).Select(x => x.Key).ToArray()) _seenNonces.TryRemove(stale, out _);
        if (!_seenNonces.TryAdd(delivery.Signal.Nonce, now)) throw new AuthenticationException("Repeated Internet signaling nonce.");
        SignalReceived?.Invoke(delivery.SenderDeviceId, signal);
        _seenMessages.TryAdd(delivery.MessageId, now);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            await SendFrameAsync(socket, RendezvousFrame.Create("heartbeat", new RendezvousHeartbeat(CurrentContacts())), token).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<RendezvousContact> CurrentContacts() => _trust.GetAll()
        .Select(x => new RendezvousContact(x.Device.DeviceId, x.Device.DisplayName, x.Fingerprint)).ToArray();

    private async Task SendFrameAsync(ClientWebSocket socket, RendezvousFrame frame, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        if (bytes.Length > MaxFrameLength) throw new InvalidDataException("Rendezvous frame exceeds its limit.");
        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    private static async Task<RendezvousFrame> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var output = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("NOVA Network closed the connection.");
            if (result.MessageType != WebSocketMessageType.Text || output.Length + result.Count > MaxFrameLength)
                throw new InvalidDataException("Invalid or oversized rendezvous frame.");
            output.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<RendezvousFrame>(output.ToArray(), JsonOptions) ?? throw new InvalidDataException("Empty rendezvous frame.");
    }

    private void UpdateDevices(IReadOnlyList<RendezvousDevice> devices)
    {
        var validated = devices.Where(x => Guid.TryParseExact(x.DeviceId, "N", out _) && PeerTrustStore.IsValidFingerprint(x.Fingerprint) && _trust.Find(x.Fingerprint)?.Device.DeviceId == x.DeviceId)
            .GroupBy(x => x.DeviceId, StringComparer.Ordinal).Select(x => x.First()).ToArray();
        lock (_stateGate) _devices = validated;
        DevicesChanged?.Invoke(validated);
    }

    private void MarkDevicesOffline()
    {
        IReadOnlyList<RendezvousDevice> offline;
        lock (_stateGate)
        {
            offline = _devices.Select(device => device with { Online = false }).ToArray();
            _devices = offline;
        }
        DevicesChanged?.Invoke(offline);
    }

    private void SetStatus(InternetConnectionState state, string? detail)
    {
        var next = new InternetConnectionStatus(state, detail);
        lock (_stateGate) _status = next;
        StatusChanged?.Invoke(next);
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _requests.Values) pending.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client shutdown", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) when (ex is WebSocketException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
        }
        if (_worker is not null) try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        FailPending(new ObjectDisposedException(nameof(InternetRendezvousClient)));
        _sendGate.Dispose(); _lifetime.Dispose();
    }
}
