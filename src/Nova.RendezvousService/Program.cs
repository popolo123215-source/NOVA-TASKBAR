using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.RateLimiting;
using Nova.RendezvousProtocol;

var statePath = Environment.GetEnvironmentVariable("NOVA_STATE_PATH") ?? "/var/lib/nova-rendezvous";
var pfxPath = Environment.GetEnvironmentVariable("NOVA_TLS_PFX_PATH");
var pfxPassword = Environment.GetEnvironmentVariable("NOVA_TLS_PFX_PASSWORD");
var connectsPerIpPerMinute = int.TryParse(Environment.GetEnvironmentVariable("NOVA_CONNECTS_PER_IP_PER_MINUTE"), out var connectionRate)
    ? Math.Clamp(connectionRate, 1, 10_000) : 30;
if (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath) || string.IsNullOrWhiteSpace(pfxPassword))
    throw new InvalidOperationException("NOVA_TLS_PFX_PATH and NOVA_TLS_PFX_PASSWORD must point to the production WSS certificate.");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(int.TryParse(Environment.GetEnvironmentVariable("NOVA_HTTPS_PORT"), out var port) ? port : 8443,
    listen => listen.UseHttps(pfxPath, pfxPassword)));
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = connectsPerIpPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
builder.Services.AddSingleton(new RendezvousRegistry(statePath));
builder.Services.AddSingleton<IceCredentialIssuer>();
var app = builder.Build();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
app.MapGet("/healthz", () => Results.Text("ok"));
app.Map("/v1/connect", HandleSocketAsync);
await app.RunAsync();

static async Task HandleSocketAsync(HttpContext context)
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var registry = context.RequestServices.GetRequiredService<RendezvousRegistry>();
    var issuer = context.RequestServices.GetRequiredService<IceCredentialIssuer>();
    RendezvousConnection? connection = null;
    try
    {
        var helloFrame = await ReceiveAsync(socket, context.RequestAborted).WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted);
        if (helloFrame.Type != "hello") throw new InvalidDataException("Expected rendezvous hello.");
        var hello = helloFrame.ReadPayload<RendezvousHello>();
        if (string.IsNullOrWhiteSpace(hello.CertificateDer) || string.IsNullOrWhiteSpace(hello.DeviceId) || string.IsNullOrWhiteSpace(hello.DisplayName))
            throw new InvalidDataException("Device registration fields are incomplete.");
        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(hello.CertificateDer));
        var idFromCert = DeviceSignalCryptography.DeviceIdFromCertificate(certificate);
        var now = DateTime.UtcNow;
        if (!Guid.TryParseExact(hello.DeviceId, "N", out _) || idFromCert != hello.DeviceId || hello.DisplayName.Length > 80 ||
            certificate.NotBefore.ToUniversalTime() > now.AddMinutes(1) || certificate.NotAfter.ToUniversalTime() <= now)
            throw new AuthenticationException("Device id or display name is invalid.");
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await SendAsync(socket, RendezvousFrame.Create("challenge", new RendezvousChallenge(challenge)), context.RequestAborted);
        var registerFrame = await ReceiveAsync(socket, context.RequestAborted).WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted);
        if (registerFrame.Type != "register") throw new InvalidDataException("Expected challenge response.");
        var register = registerFrame.ReadPayload<RendezvousRegister>();
        var fingerprint = DeviceSignalCryptography.Fingerprint(certificate);
        var proofBytes = System.Text.Encoding.UTF8.GetBytes($"NOVA-RENDEZVOUS-v1\n{challenge}\n{hello.DeviceId}\n{fingerprint}");
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is null || !rsa.VerifyData(proofBytes, Convert.FromBase64String(register.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new AuthenticationException("Device private-key proof failed.");
        }
        connection = await registry.RegisterAsync(hello.DeviceId, hello.DisplayName, fingerprint, hello.CertificateDer, register.Contacts, socket);
        await connection.SendAsync(RendezvousFrame.Create("registered", new { deviceId = hello.DeviceId, serverTime = DateTimeOffset.UtcNow }), context.RequestAborted);
        // Queue the initial roster before publishing the connection. Otherwise a concurrent
        // registration/broadcast could put a signal ahead of the roster during client auth.
        await connection.SendAsync(RendezvousFrame.Create("devices", registry.GetVisibleDevices(connection)), context.RequestAborted);
        await registry.ActivateAsync(connection);
        await registry.FlushAsync(connection, context.RequestAborted);
        await registry.BroadcastListsAsync(context.RequestAborted);

        var sender = connection.SendLoopAsync(context.RequestAborted);
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(socket, context.RequestAborted).WaitAsync(TimeSpan.FromSeconds(45), context.RequestAborted);
                await connection.LimitAsync(context.RequestAborted);
                switch (frame.Type)
                {
                    case "heartbeat":
                        connection.UpdateContacts(frame.ReadPayload<RendezvousHeartbeat>().Contacts);
                        await connection.SendAsync(RendezvousFrame.Create("devices", registry.GetVisibleDevices(hello.DeviceId)), context.RequestAborted);
                        break;
                    case "signal":
                        var request = frame.ReadPayload<RendezvousSignalRequest>();
                        await registry.RouteSignalAsync(connection, frame.RequestId, request, context.RequestAborted);
                        break;
                    case "signal-received":
                        registry.AcknowledgeDelivery(connection.DeviceId, frame.ReadPayload<RendezvousSignalReceipt>().MessageId);
                        break;
                    case "ice-credentials":
                        await connection.SendAsync(RendezvousFrame.Create("ice-credentials", issuer.Issue(connection.DeviceId), frame.RequestId), context.RequestAborted);
                        break;
                    case "ping":
                        await connection.SendAsync(RendezvousFrame.Create("pong", new { at = DateTimeOffset.UtcNow }, frame.RequestId), context.RequestAborted);
                        break;
                    default:
                        await connection.SendAsync(RendezvousFrame.Create("error", new RendezvousError("unsupported", "Unsupported rendezvous request."), frame.RequestId), context.RequestAborted);
                        break;
                }
            }
        }
        finally
        {
            connection.Dispose();
            try { await sender.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebSocketException) { }
        }
    }
    catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or AuthenticationException or CryptographicException or FormatException or JsonException or OperationCanceledException or TimeoutException)
    {
        if (socket.State == WebSocketState.Open)
        {
            try { await SendAsync(socket, RendezvousFrame.Create("error", new RendezvousError("rejected", ex.Message)), context.RequestAborted); } catch { }
            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Registration or request rejected", CancellationToken.None); } catch { }
        }
    }
    finally
    {
        if (connection is not null) await registry.RemoveAsync(connection);
    }
}

static async Task<RendezvousFrame> ReceiveAsync(WebSocket socket, CancellationToken token)
{
    const int limit = 256 * 1024;
    var buffer = new byte[16 * 1024];
    using var output = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer, token);
        if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Client closed rendezvous connection.");
        if (result.MessageType != WebSocketMessageType.Text || output.Length + result.Count > limit) throw new InvalidDataException("Invalid or oversized rendezvous frame.");
        output.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonSerializer.Deserialize<RendezvousFrame>(output.ToArray(), RendezvousJson.Options) ?? throw new InvalidDataException("Empty rendezvous frame.");
}

static Task SendAsync(WebSocket socket, RendezvousFrame frame, CancellationToken token) =>
    socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame, RendezvousJson.Options), WebSocketMessageType.Text, true, token);

sealed class IceCredentialIssuer
{
    private readonly string[] _stun = Split("NOVA_STUN_URIS");
    private readonly string[] _turn = Split("NOVA_TURN_URIS");
    private readonly string _secret = Environment.GetEnvironmentVariable("NOVA_TURN_SHARED_SECRET") ?? "";
    private readonly int _ttl = int.TryParse(Environment.GetEnvironmentVariable("NOVA_TURN_CREDENTIAL_TTL_SECONDS"), out var value) ? Math.Clamp(value, 60, 3600) : 600;
    public RendezvousIceCredentials Issue(string deviceId)
    {
        if (_turn.Length > 0 && _secret.Length < 32) throw new InvalidOperationException("NOVA_TURN_SHARED_SECRET must contain at least 32 characters when TURN URIs are configured.");
        if (_turn.Length == 0) return new RendezvousIceCredentials(_stun, [], null, null, null);
        var expiry = DateTimeOffset.UtcNow.AddSeconds(_ttl);
        var username = $"{expiry.ToUnixTimeSeconds()}:{deviceId}";
        using var hmac = new HMACSHA1(System.Text.Encoding.UTF8.GetBytes(_secret));
        var password = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(username)));
        return new RendezvousIceCredentials(_stun, _turn, username, password, expiry);
    }

    private static string[] Split(string name) => (Environment.GetEnvironmentVariable(name) ?? "")
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

sealed class RendezvousRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DevicePin> _pins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RendezvousConnection> _online = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<PendingSignal>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _seenSignalNonces = new(StringComparer.Ordinal);
    private readonly string _pinPath;
    private readonly int _maxOnlineDevices = int.TryParse(Environment.GetEnvironmentVariable("NOVA_MAX_ONLINE_DEVICES"), out var maxOnline)
        ? Math.Clamp(maxOnline, 1, 100_000) : 5000;
    public RendezvousRegistry(string statePath)
    {
        Directory.CreateDirectory(statePath);
        _pinPath = Path.Combine(statePath, "device-pins.json");
        if (File.Exists(_pinPath))
        {
            try
            {
                var pins = JsonSerializer.Deserialize<DevicePin[]>(File.ReadAllText(_pinPath), JsonOptions) ?? [];
                foreach (var pin in pins) _pins[pin.DeviceId] = pin;
            }
            catch (JsonException ex) { throw new InvalidDataException("Persistent device pin file is corrupt; refusing to replace it.", ex); }
        }
    }

    public Task<RendezvousConnection> RegisterAsync(string id, string name, string fingerprint, string certificateDer,
        IReadOnlyList<RendezvousContact> contacts, WebSocket socket)
    {
        ValidateContacts(id, contacts);
        lock (_gate)
        {
            if (_pins.TryGetValue(id, out var pin) && pin.Fingerprint != fingerprint)
                throw new AuthenticationException("This device id is already pinned to another identity certificate.");
            if (!_pins.ContainsKey(id)) { _pins[id] = new DevicePin(id, name, fingerprint, certificateDer); SavePins(); }
        }
        return Task.FromResult(new RendezvousConnection(id, name, fingerprint, certificateDer, contacts, socket));
    }

    public Task ActivateAsync(RendezvousConnection connection)
    {
        lock (_gate)
        {
            if (_online.Remove(connection.DeviceId, out var previous)) previous.Close();
            if (_online.Count >= _maxOnlineDevices) throw new IOException("Rendezvous online-device capacity has been reached.");
            _online[connection.DeviceId] = connection;
        }
        return Task.CompletedTask;
    }

    public RendezvousDeviceList GetVisibleDevices(RendezvousConnection owner)
    {
        lock (_gate)
        {
            var devices = owner.Contacts.Select(contact =>
            {
                var online = _online.TryGetValue(contact.DeviceId, out var remote) &&
                    remote.Contacts.Any(x => x.DeviceId == owner.DeviceId && x.Fingerprint == owner.Fingerprint) && remote.Fingerprint == contact.Fingerprint;
                return new RendezvousDevice(contact.DeviceId, contact.DisplayName, contact.Fingerprint, online);
            }).OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return new RendezvousDeviceList(devices, DateTimeOffset.UtcNow);
        }
    }

    public RendezvousDeviceList GetVisibleDevices(string ownerId)
    {
        lock (_gate)
        {
            if (!_online.TryGetValue(ownerId, out var owner)) return new([], DateTimeOffset.UtcNow);
            var devices = owner.Contacts.Select(contact =>
            {
                var online = _online.TryGetValue(contact.DeviceId, out var remote) &&
                    remote.Contacts.Any(x => x.DeviceId == ownerId && x.Fingerprint == owner.Fingerprint) && remote.Fingerprint == contact.Fingerprint;
                return new RendezvousDevice(contact.DeviceId, contact.DisplayName, contact.Fingerprint, online);
            }).OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return new RendezvousDeviceList(devices, DateTimeOffset.UtcNow);
        }
    }

    internal static void ValidateContactsForConnection(string ownerId, IReadOnlyList<RendezvousContact> contacts) => ValidateContacts(ownerId, contacts);

    public async Task RouteSignalAsync(RendezvousConnection sender, string? requestId, RendezvousSignalRequest request, CancellationToken token)
    {
        if (request is null || request.Signal is null || string.IsNullOrWhiteSpace(request.TargetDeviceId))
            throw new InvalidDataException("Signaling request is incomplete.");
        if (!Guid.TryParseExact(request.Signal.SessionId, "N", out _) || !Guid.TryParseExact(request.Signal.Nonce, "N", out _) ||
            request.Signal.Type is not ("offer" or "sdp" or "candidate" or "accept" or "reject" or "cancel" or "closed" or "error"))
            throw new InvalidDataException("Signaling session, nonce, or message type is invalid.");
        if (JsonSerializer.SerializeToUtf8Bytes(request.Signal, RendezvousJson.Options).Length > 128 * 1024)
            throw new InvalidDataException("Signed signaling message exceeds its limit.");
        if (!Guid.TryParseExact(request.TargetDeviceId, "N", out _) || request.Signal.SenderDeviceId != sender.DeviceId ||
            !DeviceSignalCryptography.Verify(request.Signal, sender.Certificate, DateTimeOffset.UtcNow))
            throw new AuthenticationException("Signal sender proof is invalid.");
        var id = Guid.NewGuid().ToString("N");
        RendezvousConnection? recipient;
        var queued = false;
        lock (_gate)
        {
            if (!_online.TryGetValue(sender.DeviceId, out var current) || current != sender) throw new AuthenticationException("Sender connection is no longer active.");
            var now = DateTimeOffset.UtcNow;
            foreach (var expiredNonce in _seenSignalNonces.Where(x => x.Value < now.AddMinutes(-3)).Select(x => x.Key).ToArray()) _seenSignalNonces.Remove(expiredNonce);
            var replayKey = sender.DeviceId + ":" + request.Signal.Nonce;
            if (_seenSignalNonces.ContainsKey(replayKey)) throw new AuthenticationException("Repeated signaling nonce.");
            if (_seenSignalNonces.Count >= 65_536) throw new IOException("Rendezvous replay cache is at capacity.");
            _seenSignalNonces[replayKey] = now;
            var senderContact = sender.Contacts.FirstOrDefault(x => x.DeviceId == request.TargetDeviceId);
            if (senderContact is null) throw new AuthenticationException("Sender is not authorized to signal this device.");
            if (_pins.TryGetValue(request.TargetDeviceId, out var targetPin) && targetPin.Fingerprint != senderContact.Fingerprint)
                throw new AuthenticationException("Target identity does not match the sender's trusted fingerprint.");
            var receiverHasSender = _online.TryGetValue(request.TargetDeviceId, out recipient) &&
                recipient.Contacts.Any(x => x.DeviceId == sender.DeviceId && x.Fingerprint == sender.Fingerprint) && recipient.Fingerprint == senderContact.Fingerprint;
            if (!receiverHasSender)
            {
                if (request.Signal.Type is not ("offer" or "accept" or "reject" or "cancel" or "closed" or "error"))
                    throw new IOException("Recipient is offline; transient SDP/ICE signaling cannot be queued.");
                if (!_pending.TryGetValue(request.TargetDeviceId, out var queue)) _pending[request.TargetDeviceId] = queue = new Queue<PendingSignal>();
                while (queue.TryPeek(out var expired) && expired.ExpiresAt <= DateTimeOffset.UtcNow) queue.Dequeue();
                if (queue.Count >= 64) throw new IOException("Recipient signaling mailbox is full.");
                queue.Enqueue(new PendingSignal(id, sender.DeviceId, request.Signal, senderContact.Fingerprint, DateTimeOffset.UtcNow.AddMinutes(3)));
                queued = true;
            }
        }
        if (!queued) await recipient!.SendAsync(RendezvousFrame.Create("signal", new RendezvousSignalDelivery(id, sender.DeviceId, request.Signal)), token);
        await sender.SendAsync(RendezvousFrame.Create("signal-ack", new { accepted = true, queued }, requestId), token);
    }

    public void AcknowledgeDelivery(string receiverId, string messageId)
    {
        lock (_gate)
        {
            foreach (var entry in _pending)
            {
                if (entry.Key != receiverId) continue;
                var retained = entry.Value.Where(x => x.Id != messageId).ToArray();
                entry.Value.Clear();
                foreach (var item in retained) entry.Value.Enqueue(item);
                break;
            }
        }
    }

    public async Task RemoveAsync(RendezvousConnection connection)
    {
        lock (_gate)
        {
            if (_online.TryGetValue(connection.DeviceId, out var current) && current == connection) _online.Remove(connection.DeviceId);
        }
        connection.Dispose();
        await BroadcastListsAsync(CancellationToken.None);
    }

    public async Task FlushAsync(RendezvousConnection connection, CancellationToken token)
    {
        PendingSignal[] queue;
        lock (_gate)
        {
            if (!_pending.TryGetValue(connection.DeviceId, out var pending)) return;
            var now = DateTimeOffset.UtcNow;
            queue = pending.Where(x => x.ExpiresAt > now && x.TargetFingerprint == connection.Fingerprint &&
                _pins.TryGetValue(x.SenderDeviceId, out var senderPin) &&
                connection.Contacts.Any(c => c.DeviceId == x.SenderDeviceId && c.Fingerprint == senderPin.Fingerprint))
                .Take(32).ToArray();
        }
        foreach (var item in queue)
        {
            await connection.SendAsync(RendezvousFrame.Create("signal", new RendezvousSignalDelivery(item.Id, item.SenderDeviceId, item.Signal)), token);
        }
    }

    public async Task BroadcastListsAsync(CancellationToken token)
    {
        RendezvousConnection[] connections;
        lock (_gate) connections = _online.Values.ToArray();
        foreach (var connection in connections)
        {
            try { await connection.SendAsync(RendezvousFrame.Create("devices", GetVisibleDevices(connection.DeviceId)), token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
        }
    }

    private static void ValidateContacts(string ownerId, IReadOnlyList<RendezvousContact> contacts)
    {
        if (contacts is null || contacts.Count > 256 || contacts.Any(x => x is null || !Guid.TryParseExact(x.DeviceId, "N", out _) || x.DeviceId == ownerId ||
            !PeerFingerprintValid(x.Fingerprint) || x.DisplayName.Length is 0 or > 80) || contacts.Select(x => x.DeviceId).Distinct(StringComparer.Ordinal).Count() != contacts.Count)
            throw new InvalidDataException("Invalid trusted-contact list.");
    }

    private static bool PeerFingerprintValid(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private void SavePins()
    {
        var temp = _pinPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_pins.Values, JsonOptions));
        File.Move(temp, _pinPath, true);
    }

    private sealed record DevicePin(string DeviceId, string DisplayName, string Fingerprint, string CertificateDer);
    private sealed record PendingSignal(string Id, string SenderDeviceId, DeviceSignalEnvelope Signal, string TargetFingerprint, DateTimeOffset ExpiresAt);
    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

sealed class RendezvousConnection(string deviceId, string displayName, string fingerprint, string certificateDer,
    IReadOnlyList<RendezvousContact> contacts, WebSocket socket) : IDisposable
{
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly object _rateGate = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly object _contactsGate = new();
    private IReadOnlyList<RendezvousContact> _contacts = contacts;
    public string DeviceId { get; } = deviceId;
    public string DisplayName { get; } = displayName;
    public string Fingerprint { get; } = fingerprint;
    public X509Certificate2 Certificate { get; } = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateDer));
    public IReadOnlyList<RendezvousContact> Contacts { get { lock (_contactsGate) return _contacts; } }
    public void UpdateContacts(IReadOnlyList<RendezvousContact> contacts)
    {
        RendezvousRegistry.ValidateContactsForConnection(DeviceId, contacts);
        lock (_contactsGate) _contacts = contacts;
    }
    public Task SendAsync(RendezvousFrame frame, CancellationToken token) => _outgoing.Writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions), token).AsTask();
    public async Task SendLoopAsync(CancellationToken token)
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync(token)) await socket.SendAsync(message, WebSocketMessageType.Text, true, token);
    }
    public Task LimitAsync(CancellationToken token)
    {
        lock (_rateGate)
        {
            var now = DateTimeOffset.UtcNow;
            while (_requests.TryPeek(out var previous) && previous < now.AddMinutes(-1)) _requests.Dequeue();
            if (_requests.Count >= 120) throw new InvalidDataException("Rendezvous request rate limit exceeded.");
            _requests.Enqueue(now);
        }
        return Task.CompletedTask;
    }
    public void Close()
    {
        _outgoing.Writer.TryComplete();
        try { socket.Abort(); } catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        Close();
        Certificate.Dispose();
    }
    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
}

static class RendezvousJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
}
