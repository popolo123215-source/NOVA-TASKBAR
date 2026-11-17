using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Net.WebSockets;
using Nova.Core.Contracts;
using Nova.TransferProtocol;

namespace Nova.TransferEngine;

public enum WebRtcTransferSessionState { PendingApproval, Connecting, Transferring, Verifying, Completed, Rejected, Cancelled, Failed }
public sealed record WebRtcSessionProgress(
    string TransferId,
    string FileName,
    WebRtcTransferSessionState State,
    long BytesTransferred,
    long TotalBytes,
    string? Error = null,
    string? ConnectionPath = null,
    string PeerConnectionState = "new",
    string IceState = "new",
    string DataChannelState = "waiting",
    int LocalIceCandidates = 0,
    int RemoteIceCandidates = 0,
    int IceServerCount = 0,
    bool TurnConfigured = false,
    double BytesPerSecond = 0,
    bool IntegrityVerified = false);
public sealed class WebRtcTransferRejectedException(string fileName) : IOException($"The receiver rejected '{fileName}'.");
internal sealed class WebRtcRouteUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class TransferRemoteFailureException(string message) : IOException(message);

public sealed class WebRtcIncomingOfferRequest
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal WebRtcIncomingOfferRequest(TransferOffer offer) => Offer = offer;
    public TransferOffer Offer { get; }
    public bool Respond(bool accept) => _decision.TrySetResult(accept);
    internal Task<bool> WaitAsync(CancellationToken token) => _decision.Task.WaitAsync(TimeSpan.FromMinutes(3), token);
}

/// <summary>Owns per-transfer signaling, native peer lifetime and file stream coordination.</summary>
public sealed class WebRtcTransferCoordinator : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeviceIdentity _identity;
    private readonly PeerTrustStore _trust;
    private readonly TransferSettingsStore _settings;
    private readonly WebRtcSignalingHub _hub;
    private readonly Func<DiscoveredDevice, WebRtcSignal, CancellationToken, Task> _publish;
    private readonly Func<string, DiscoveredDevice?> _resolve;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>? _iceServers;
    private readonly TcpTlsFileRelayClient? _fileRelay;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public WebRtcTransferCoordinator(DeviceIdentity identity, PeerTrustStore trust, TransferSettingsStore settings,
        WebRtcSignalingHub hub, Func<DiscoveredDevice, WebRtcSignal, CancellationToken, Task> publish,
        Func<string, DiscoveredDevice?> resolve, Func<CancellationToken, Task<IReadOnlyList<string>>>? iceServers = null,
        TcpTlsFileRelayClient? fileRelay = null)
    {
        _identity = identity; _trust = trust; _settings = settings; _hub = hub; _publish = publish; _resolve = resolve;
        _iceServers = iceServers; _fileRelay = fileRelay;
        _hub.IncomingSession += OnIncomingSignal;
    }

    public event Action<WebRtcIncomingOfferRequest>? IncomingOffer;
    public event Action<WebRtcSessionProgress>? ProgressChanged;

    public async Task SendFilesAsync(DiscoveredDevice target, IEnumerable<string> paths, CancellationToken token = default)
    {
        var trusted = _trust.Find(target.Fingerprint);
        if (trusted is null || trusted.Device.DeviceId != target.DeviceId) throw new UnauthorizedAccessException("Recipient is not trusted.");
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!await SendFileAsync(target, trusted.Device, path, token).ConfigureAwait(false))
                throw new WebRtcTransferRejectedException(Path.GetFileName(path));
        }
    }

    public bool Cancel(string transferId)
    {
        if (!_sessions.TryGetValue(transferId, out var session)) return false;
        session.Cancel();
        return true;
    }

    private async Task<bool> SendFileAsync(DiscoveredDevice target, DeviceRef recipient, string path, CancellationToken callerToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Selected file no longer exists.", path);
        var preference = _settings.TransportPreference;
        if (!Enum.IsDefined(typeof(TransferTransportPreference), preference))
            throw new InvalidDataException("Transfer transport preference is invalid.");
        if (preference == TransferTransportPreference.DirectVpn)
            throw new InvalidOperationException("Direct VPN transfers require a manually selected IPv4 endpoint and must use the direct TCP service.");
        var transferId = Guid.NewGuid().ToString("N");
        var session = new Session(transferId, target, true, _lifetime.Token, callerToken);
        if (!_sessions.TryAdd(transferId, session)) throw new InvalidOperationException("Transfer id collision.");
        try
        {
            session.Route = _hub.Register(target.DeviceId, transferId);
            await using var hashStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await WebRtcFileBlockTransfer.ComputeSha256Async(hashStream, session.Token).ConfigureAwait(false);
            file.Refresh();
            if (!file.Exists || file.Length != hashStream.Length) throw new IOException("File changed while preparing the transfer offer.");
            var offer = new TransferOffer(transferId, _identity.Device, recipient, file.Name, hashStream.Length, null, hash,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3), false)
                { TransportPreference = preference };
            session.Offer = offer;
            session.SignalEvents = SuperviseSignalAsync(session, SignalLoopAsync(session));
            Report(session, WebRtcTransferSessionState.PendingApproval, 0);
            await PublishAsync(session, "offer", offer).ConfigureAwait(false);
            var accepted = await WaitForAsync(session.Acceptance.Task, session, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            if (!accepted) { Report(session, WebRtcTransferSessionState.Rejected, 0); return false; }
            if (preference == TransferTransportPreference.TcpTlsFileRelay)
            {
                await StopSignalLoopAsync(session).ConfigureAwait(false);
                await SendViaFileRelayAsync(session, file.FullName, offer).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await SendViaWebRtcAsync(session, file.FullName, offer, preference).ConfigureAwait(false);
                }
                catch (Exception ex) when (preference == TransferTransportPreference.Automatic &&
                    (ex is WebRtcRouteUnavailableException or TimeoutException))
                {
                    await StopSignalLoopAsync(session).ConfigureAwait(false);
                    await StopWebRtcAsync(session).ConfigureAwait(false);
                    session.ConnectionPath = "TCP/TLS File Relay";
                    session.Bytes = 0;
                    session.IntegrityVerified = false;
                    Report(session, WebRtcTransferSessionState.Connecting, 0);
                    await SendViaFileRelayAsync(session, file.FullName, offer).ConfigureAwait(false);
                }
            }
            await PublishAsync(session, "closed", new { }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (session.Failure.Task.IsCompleted)
        {
            var error = await session.Failure.Task.ConfigureAwait(false);
            Report(session, WebRtcTransferSessionState.Failed, session.Bytes, error.Message);
            try { await PublishTerminalAsync(session, "error", new { message = error.Message }).ConfigureAwait(false); } catch { }
            throw error;
        }
        catch (OperationCanceledException)
        {
            Report(session, WebRtcTransferSessionState.Cancelled, session.Bytes);
            try { await PublishTerminalAsync(session, "cancel", new { }).ConfigureAwait(false); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            Report(session, WebRtcTransferSessionState.Failed, session.Bytes, ex.Message);
            try { await PublishTerminalAsync(session, "error", new { message = ex.Message }).ConfigureAwait(false); } catch { }
            throw;
        }
        finally { await CloseSessionAsync(session).ConfigureAwait(false); }
    }

    private async Task SendViaWebRtcAsync(Session session, string path, TransferOffer offer, TransferTransportPreference preference)
    {
        var iceServers = FilterIceServers(await GetIceServersAsync(session.Device, session.Token).ConfigureAwait(false), preference);
        SetIceServerDiagnostics(session, iceServers);
        try
        {
            session.Peer = WebRtcPeer.Create(iceServers, IsTurnOnly(preference));
            session.Peer.CreateReliableChannel("nova-transfer-" + session.Id[..8]);
            session.PeerEvents = SupervisePeerAsync(session, PeerEventLoopAsync(session));
            Report(session, WebRtcTransferSessionState.Connecting, 0);
            session.Peer.CreateOffer();
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new WebRtcRouteUnavailableException("WebRTC could not initialize the selected route.", ex);
        }

        await WaitForWebRtcAsync(session.ChannelOpen.Task, session, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
        session.ConnectionPath = DescribeRoute(session.Peer.GetSelectedCandidatePair(), preference) ?? "unknown";
        Report(session, WebRtcTransferSessionState.Transferring, 0);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var send = WebRtcFileBlockTransfer.SendAsync(source, Guid.ParseExact(session.Id, "N"), offer.SizeBytes, offer.Sha256,
            (frame, ct) => SendWebRtcFrameAsync(session, frame, ct),
            async (durableOffset, ct) =>
            {
                var ack = await session.Acks.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                if (ack != durableOffset) throw new InvalidDataException("Receiver acknowledged an unexpected file offset.");
            }, new Progress<long>(bytes => Report(session, WebRtcTransferSessionState.Transferring, bytes)), session.Token);
        await WaitForWebRtcAsync(send, session, null).ConfigureAwait(false);
        Report(session, WebRtcTransferSessionState.Verifying, offer.SizeBytes);
        await WaitForWebRtcAsync(session.Completed.Task, session, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        Report(session, WebRtcTransferSessionState.Completed, offer.SizeBytes);
    }

    private async Task SendViaFileRelayAsync(Session session, string path, TransferOffer offer)
    {
        var relay = _fileRelay ?? throw new IOException("TCP/TLS File Relay is unavailable.");
        await using var connection = await relay.ConnectAsync(offer, session.Device.DeviceId, isSender: true, session.Token).ConfigureAwait(false);
        session.ConnectionPath = "TCP/TLS File Relay";
        session.DataChannelState = "open";
        session.PeerConnectionState = "connected";
        Report(session, WebRtcTransferSessionState.Transferring, 0);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WebRtcFileBlockTransfer.SendAsync(source, Guid.ParseExact(session.Id, "N"), offer.SizeBytes, offer.Sha256,
            connection.SendFrameAsync, connection.WaitForDurableAcknowledgementAsync,
            new Progress<long>(bytes => Report(session, WebRtcTransferSessionState.Transferring, bytes)), session.Token).ConfigureAwait(false);
        Report(session, WebRtcTransferSessionState.Verifying, offer.SizeBytes);
        await connection.WaitForCompletionAsync(session.Token).ConfigureAwait(false);
        session.IntegrityVerified = true;
        Report(session, WebRtcTransferSessionState.Completed, offer.SizeBytes);
    }

    private static IReadOnlyList<string> FilterIceServers(IReadOnlyList<string> iceServers, TransferTransportPreference preference) =>
        preference switch
        {
            TransferTransportPreference.DirectP2P => iceServers.Where(x => x.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToArray(),
            TransferTransportPreference.TurnUdp => iceServers.Where(IsTurnUdp).ToArray(),
            TransferTransportPreference.TurnTcp => iceServers.Where(IsTurnTcp).ToArray(),
            TransferTransportPreference.TurnTls => iceServers.Where(x => x.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)).ToArray(),
            _ => iceServers
        };

    private static bool IsTurnUdp(string uri) =>
        uri.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) &&
        !uri.Contains("transport=tcp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTurnTcp(string uri) =>
        uri.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) &&
        uri.Contains("transport=tcp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTurnOnly(TransferTransportPreference preference) =>
        preference is TransferTransportPreference.TurnUdp or TransferTransportPreference.TurnTcp or TransferTransportPreference.TurnTls;

    private static string? DescribeRoute(WebRtcPeer.SelectedCandidatePair? pair, TransferTransportPreference preference)
    {
        if (pair is null) return null;
        if (!pair.IsRelay) return "direct";
        return preference switch
        {
            TransferTransportPreference.TurnUdp => "TURN UDP",
            TransferTransportPreference.TurnTcp => "TURN TCP",
            TransferTransportPreference.TurnTls => "TURN TLS",
            _ => "TURN relay"
        };
    }

    private static async ValueTask SendWebRtcFrameAsync(Session session, ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        try { await session.Peer.SendAsync(frame, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { throw new WebRtcRouteUnavailableException("WebRTC DataChannel send failed.", ex); }
    }

    private async Task StopWebRtcAsync(Session session)
    {
        if (session.Peer is not null)
        {
            var peer = session.Peer;
            session.Peer = null!;
            await peer.DisposeAsync().ConfigureAwait(false);
        }
        if (session.PeerEvents is not null)
        {
            try { await session.PeerEvents.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebRtcRouteUnavailableException) { }
        }
    }

    private static async Task StopSignalLoopAsync(Session session)
    {
        session.StopSignalLoop();
        if (session.SignalEvents is not null)
        {
            try { await session.SignalEvents.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
        await DisposeSignalReaderAsync(session).ConfigureAwait(false);
    }

    private static async Task DisposeSignalReaderAsync(Session session)
    {
        var reader = Interlocked.Exchange(ref session.SignalReader, null);
        if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
    }

    private void OnIncomingSignal(IncomingWebRtcSession incoming)
    {
        if (_disposed) return;
        var task = HandleIncomingAsync(incoming, _lifetime.Token);
        _ = task.ContinueWith(t => Console.Error.WriteLine($"[NOVA WebRTC] Incoming session failed: {t.Exception}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task HandleIncomingAsync(IncomingWebRtcSession incoming, CancellationToken token)
    {
        var routeReader = incoming.Route.ReadAllAsync(token).GetAsyncEnumerator(token);
        await using (routeReader.ConfigureAwait(false))
        {
            if (!await routeReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false)) return;
            var first = routeReader.Current;
            if (first.Type != "offer" || first.SessionId != incoming.SessionId) throw new InvalidDataException("Invalid transfer request signal.");
            var offer = first.Payload.Deserialize<TransferOffer>(JsonOptions) ?? throw new InvalidDataException("Transfer offer payload is empty.");
            if (offer.Sender is null || offer.Recipient is null || string.IsNullOrWhiteSpace(offer.FileName) ||
                offer.FileName.Length > 255 || offer.Sha256 is null || offer.Sha256.Length != 64 ||
                !offer.Sha256.All(Uri.IsHexDigit) ||
                offer.TransferId != incoming.SessionId || offer.Sender.DeviceId != incoming.PeerDeviceId ||
                offer.Recipient.DeviceId != _identity.Device.DeviceId || offer.SizeBytes < 0)
                throw new InvalidDataException("Offer identity, session, or size does not match signaling.");
            if (offer.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(1) || offer.ExpiresAt <= DateTimeOffset.UtcNow ||
                offer.ExpiresAt <= offer.CreatedAt || offer.ExpiresAt > offer.CreatedAt.AddMinutes(5))
                throw new InvalidDataException("The incoming transfer offer is not currently valid.");
            _ = TransferFileSecurity.ValidateAndResolve(_settings.DestinationDirectory, offer.FileName);
            var peer = _resolve(incoming.PeerDeviceId) ?? throw new IOException("The trusted sender is no longer available in discovery.");
            if (!_trust.IsTrusted(peer.Fingerprint)) throw new UnauthorizedAccessException("Incoming signaling peer is no longer trusted.");
            var session = new Session(offer.TransferId, peer, false, _lifetime.Token, token) { Route = incoming.Route, Offer = offer, SignalReader = routeReader };
            if (!_sessions.TryAdd(offer.TransferId, session)) throw new InvalidDataException("Duplicate active transfer id.");
            routeReader = null!;
            try
            {
                if (!Enum.IsDefined(typeof(TransferTransportPreference), offer.TransportPreference))
                    throw new InvalidDataException("Incoming transfer transport preference is invalid.");
                session.SignalEvents = SuperviseSignalAsync(session, SignalLoopAsync(session));
                Report(session, WebRtcTransferSessionState.PendingApproval, 0);
                var request = new WebRtcIncomingOfferRequest(offer);
                IncomingOffer?.Invoke(request);
                bool accepted;
                try { accepted = await request.WaitAsync(session.Token).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    Report(session, WebRtcTransferSessionState.Rejected, 0, "Approval timed out.");
                    await PublishTerminalAsync(session, "reject", new { reason = "approval-timeout" }).ConfigureAwait(false);
                    return;
                }
                if (!accepted)
                {
                    Report(session, WebRtcTransferSessionState.Rejected, 0);
                    await PublishTerminalAsync(session, "reject", new { reason = "user-rejected" }).ConfigureAwait(false);
                    return;
                }
                await PublishAsync(session, "accept", new { accepted = true }).ConfigureAwait(false);

                if (offer.TransportPreference == TransferTransportPreference.TcpTlsFileRelay)
                {
                    await StopSignalLoopAsync(session).ConfigureAwait(false);
                    await ReceiveViaFileRelayAsync(session, offer).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await ReceiveViaWebRtcAsync(session, offer, offer.TransportPreference).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (offer.TransportPreference == TransferTransportPreference.Automatic &&
                        (ex is WebRtcRouteUnavailableException or TimeoutException))
                    {
                        await StopSignalLoopAsync(session).ConfigureAwait(false);
                        await StopWebRtcAsync(session).ConfigureAwait(false);
                        session.ConnectionPath = "TCP/TLS File Relay";
                        session.Bytes = 0;
                        session.IntegrityVerified = false;
                        Report(session, WebRtcTransferSessionState.Connecting, 0);
                        await ReceiveViaFileRelayAsync(session, offer).ConfigureAwait(false);
                    }
                }
                await PublishAsync(session, "closed", new { }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.Failure.Task.IsCompleted)
            {
                var error = await session.Failure.Task.ConfigureAwait(false);
                Report(session, WebRtcTransferSessionState.Failed, session.Bytes, error.Message);
                try { await PublishTerminalAsync(session, "error", new { message = error.Message }).ConfigureAwait(false); } catch { }
                throw error;
            }
            catch (OperationCanceledException)
            {
                Report(session, WebRtcTransferSessionState.Cancelled, session.Bytes);
                try { await PublishTerminalAsync(session, "cancel", new { }).ConfigureAwait(false); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                Report(session, WebRtcTransferSessionState.Failed, session.Bytes, ex.Message);
                try { await PublishTerminalAsync(session, "error", new { message = ex.Message }).ConfigureAwait(false); } catch { }
                throw;
            }
            finally { await CloseSessionAsync(session).ConfigureAwait(false); }
        }
        if (routeReader is not null) await routeReader.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReceiveViaWebRtcAsync(Session session, TransferOffer offer, TransferTransportPreference preference)
    {
        var iceServers = FilterIceServers(await GetIceServersAsync(session.Device, session.Token).ConfigureAwait(false), preference);
        SetIceServerDiagnostics(session, iceServers);
        try
        {
            session.Peer = WebRtcPeer.Create(iceServers, IsTurnOnly(preference));
            session.PeerEvents = SupervisePeerAsync(session, PeerEventLoopAsync(session));
            Report(session, WebRtcTransferSessionState.Connecting, 0);
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new WebRtcRouteUnavailableException("WebRTC could not initialize the selected route.", ex);
        }

        await WaitForWebRtcAsync(session.ChannelOpen.Task, session, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
        session.ConnectionPath = DescribeRoute(session.Peer.GetSelectedCandidatePair(), preference) ?? "unknown";
        Report(session, WebRtcTransferSessionState.Transferring, 0);
        var receive = WebRtcFileBlockTransfer.ReceiveAsync(ReadWebRtcFramesAsync(session, session.Token),
            Guid.ParseExact(offer.TransferId, "N"), _settings.DestinationDirectory, offer.FileName, offer.SizeBytes, offer.Sha256,
            async (offset, ct) =>
            {
                var ack = new TransferBlockAcknowledgementPayload(offset);
                await SendWebRtcControlAsync(session, TransferControlType.BlockAcknowledgement, offer.TransferId, ack, ct).ConfigureAwait(false);
            }, new Progress<long>(bytes => Report(session, WebRtcTransferSessionState.Transferring, bytes)),
            cancellationToken: session.Token);
        _ = await WaitForWebRtcAsync(receive, session, null).ConfigureAwait(false);
        session.IntegrityVerified = true;
        Report(session, WebRtcTransferSessionState.Verifying, offer.SizeBytes);
        await SendWebRtcControlAsync(session, TransferControlType.Complete, offer.TransferId,
            new TransferCompletePayload(offer.Sha256), session.Token).ConfigureAwait(false);
        Report(session, WebRtcTransferSessionState.Completed, offer.SizeBytes);
    }

    private async Task ReceiveViaFileRelayAsync(Session session, TransferOffer offer)
    {
        var relay = _fileRelay ?? throw new IOException("TCP/TLS File Relay is unavailable.");
        await using var connection = await relay.ConnectAsync(offer, session.Device.DeviceId, isSender: false, session.Token).ConfigureAwait(false);
        session.ConnectionPath = "TCP/TLS File Relay";
        session.DataChannelState = "open";
        session.PeerConnectionState = "connected";
        Report(session, WebRtcTransferSessionState.Transferring, 0);
        var received = WebRtcFileBlockTransfer.ReceiveAsync(connection.ReadDataFramesAsync(session.Token),
            Guid.ParseExact(offer.TransferId, "N"), _settings.DestinationDirectory, offer.FileName, offer.SizeBytes, offer.Sha256,
            (offset, ct) => connection.SendControlAsync(TransferControlType.BlockAcknowledgement,
                new TransferBlockAcknowledgementPayload(offset), ct),
            new Progress<long>(bytes => Report(session, WebRtcTransferSessionState.Transferring, bytes)),
            cancellationToken: session.Token);
        _ = await received.ConfigureAwait(false);
        session.IntegrityVerified = true;
        Report(session, WebRtcTransferSessionState.Verifying, offer.SizeBytes);
        await connection.SendControlAsync(TransferControlType.Complete, new TransferCompletePayload(offer.Sha256),
            session.Token).ConfigureAwait(false);
        Report(session, WebRtcTransferSessionState.Completed, offer.SizeBytes);
    }

    private static async ValueTask SendWebRtcControlAsync<T>(Session session, TransferControlType type,
        string transferId, T payload, CancellationToken token)
    {
        try { await session.Peer.SendControlAsync(type, transferId, payload, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { throw new WebRtcRouteUnavailableException("WebRTC DataChannel control send failed.", ex); }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadWebRtcFramesAsync(Session session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await using var reader = session.Frames.Reader.ReadAllAsync(token).GetAsyncEnumerator(token);
        while (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false))
            yield return reader.Current;
    }

    private async Task PeerEventLoopAsync(Session session)
    {
        await foreach (var ev in session.Peer.Events(session.Token).ConfigureAwait(false))
        {
            switch (ev.Kind)
            {
                case 1:
                    if (ev.Data.Length != sizeof(int)) throw new InvalidDataException("Invalid WebRTC peer-state event.");
                    var peerState = BitConverter.ToInt32(ev.Data);
                    session.PeerConnectionState = DescribePeerState(peerState);
                    Report(session, session.State, session.Bytes);
                    if (peerState is 4 or 5)
                        throw new WebRtcRouteUnavailableException("WebRTC peer connection failed or closed.");
                    break;
                case 2:
                    await PublishAsync(session, "sdp", new { sdp = System.Text.Encoding.UTF8.GetString(ev.Data), type = ev.Detail }).ConfigureAwait(false);
                    break;
                case 3:
                    session.LocalIceCandidates++;
                    Report(session, session.State, session.Bytes);
                    await PublishAsync(session, "candidate", new { candidate = System.Text.Encoding.UTF8.GetString(ev.Data), mid = ev.Detail }).ConfigureAwait(false);
                    break;
                case 4:
                    session.DataChannelState = "open";
                    Report(session, session.State, session.Bytes);
                    session.ChannelOpen.TrySetResult();
                    break;
                case 5:
                    if (ev.Data.AsSpan().StartsWith("NVAF"u8)) await session.Frames.Writer.WriteAsync(ev.Data, session.Token).ConfigureAwait(false);
                    else
                    {
                        var control = TransferProtocolCodec.DecodeControl<JsonElement>(ev.Data);
                        if (control.TransferId != session.Id) throw new InvalidDataException("DataChannel message belongs to a different transfer.");
                        switch (control.Type)
                        {
                            case TransferControlType.BlockAcknowledgement:
                                var ack = control.Payload.Deserialize<TransferBlockAcknowledgementPayload>(JsonOptions) ?? throw new InvalidDataException("Empty block acknowledgement.");
                                await session.Acks.Writer.WriteAsync(ack.DurableOffsetBytes, session.Token).ConfigureAwait(false);
                                break;
                            case TransferControlType.Complete:
                                var complete = control.Payload.Deserialize<TransferCompletePayload>(JsonOptions) ??
                                    throw new InvalidDataException("Empty transfer completion confirmation.");
                                if (session.Offer is null || !string.Equals(complete.Sha256, session.Offer.Sha256, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("Receiver confirmed a different SHA-256 hash.");
                                session.IntegrityVerified = true;
                                session.Completed.TrySetResult();
                                break;
                            case TransferControlType.Cancel: session.Cancel(); break;
                            case TransferControlType.Failure: throw new TransferRemoteFailureException("The receiver reported a transfer failure.");
                            default: throw new InvalidDataException($"Unexpected transfer control {control.Type}.");
                        }
                    }
                    break;
                case 6:
                    session.DataChannelState = "closed";
                    Report(session, session.State, session.Bytes);
                    var routeError = new WebRtcRouteUnavailableException("Remote DataChannel closed.");
                    session.ChannelOpen.TrySetException(routeError);
                    throw routeError;
                case 7: break;
                case 8:
                    if (ev.Data.Length != sizeof(int)) throw new InvalidDataException("Invalid WebRTC ICE-state event.");
                    session.IceState = DescribeIceState(BitConverter.ToInt32(ev.Data));
                    Report(session, session.State, session.Bytes);
                    if (session.IceState == "failed")
                        throw new WebRtcRouteUnavailableException("ICE connectivity failed.");
                    break;
            }
        }
    }

    private async Task SignalLoopAsync(Session session)
    {
        var pendingCandidates = new Queue<(string Candidate, string? Mid)>();
        var remoteDescriptionSet = false;
        var reader = Interlocked.Exchange(ref session.SignalReader, null) ??
            session.Route.ReadAllAsync(session.SignalToken).GetAsyncEnumerator(session.SignalToken);
        try
        {
            while (await reader.MoveNextAsync().ConfigureAwait(false))
            {
                var signal = reader.Current;
                if (signal.SessionId != session.Id) continue;
                switch (signal.Type)
                {
                    case "accept": session.Acceptance.TrySetResult(true); break;
                    case "reject": session.Acceptance.TrySetResult(false); break;
                    case "sdp":
                        var sdp = signal.Payload.Deserialize<SdpPayload>(JsonOptions) ?? throw new InvalidDataException("Empty SDP signaling payload.");
                        session.Peer.SetRemoteDescription(sdp.Sdp, sdp.Type);
                        remoteDescriptionSet = true;
                        while (pendingCandidates.TryDequeue(out var c)) session.Peer.AddIceCandidate(c.Candidate, c.Mid);
                        if (sdp.Type == "offer") session.Peer.CreateAnswer();
                        break;
                    case "candidate":
                        var candidate = signal.Payload.Deserialize<CandidatePayload>(JsonOptions) ?? throw new InvalidDataException("Empty ICE candidate.");
                        session.RemoteIceCandidates++;
                        Report(session, session.State, session.Bytes);
                        if (remoteDescriptionSet) session.Peer.AddIceCandidate(candidate.Candidate, candidate.Mid);
                        else pendingCandidates.Enqueue((candidate.Candidate, candidate.Mid));
                        break;
                    case "cancel": session.Cancel(); break;
                    case "closed": session.RemoteClosed.TrySetResult(); break;
                    case "error":
                        var error = signal.Payload.Deserialize<ErrorPayload>(JsonOptions);
                        throw new IOException(error?.Message ?? "Remote signaling error.");
                }
            }
        }
        finally { await reader.DisposeAsync().ConfigureAwait(false); }
    }

    private Task PublishAsync<T>(Session session, string type, T payload) =>
        _publish(session.Device, WebRtcSignal.Create(session.Id, type, payload), session.Token);

    private async Task<IReadOnlyList<string>> GetIceServersAsync(DiscoveredDevice remote, CancellationToken token)
    {
        if (_iceServers is null) return [];
        try { return await _iceServers(token).ConfigureAwait(false); }
        catch (Exception ex) when (remote.IsAvailableOnLan && ex is (IOException or TimeoutException or WebSocketException))
        {
            return [];
        }
    }

    private Task PublishTerminalAsync<T>(Session session, string type, T payload) =>
        _publish(session.Device, WebRtcSignal.Create(session.Id, type, payload), _lifetime.Token);

    private async Task SuperviseSignalAsync(Session session, Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) when (session.SignalToken.IsCancellationRequested) { }
        catch (InvalidDataException ex) { session.Fail(ex); }
        catch (IOException ex) when (session.Peer is not null)
        {
            MarkPeerRouteUnavailable(session, new WebRtcRouteUnavailableException("WebRTC signaling negotiation failed.", ex));
        }
        catch (Exception ex) { session.Fail(ex); }
    }

    private async Task SupervisePeerAsync(Session session, Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        catch (InvalidDataException ex) { session.Fail(ex); }
        catch (TransferRemoteFailureException ex) { session.Fail(ex); }
        catch (IOException ex)
        {
            MarkPeerRouteUnavailable(session, ex as WebRtcRouteUnavailableException ??
                new WebRtcRouteUnavailableException("WebRTC peer failed.", ex));
        }
        catch (Exception ex)
        {
            session.Fail(ex);
        }
    }

    private static void MarkPeerRouteUnavailable(Session session, WebRtcRouteUnavailableException exception)
    {
        if (!session.PeerFailure.TrySetResult(exception)) return;
        session.Frames.Writer.TryComplete(exception);
        session.Acks.Writer.TryComplete(exception);
        session.ChannelOpen.TrySetException(exception);
    }

    private static async Task WaitForAsync(Task operation, Session session, TimeSpan timeout)
    {
        var winner = await Task.WhenAny(operation, session.Failure.Task, Task.Delay(timeout, session.Token)).ConfigureAwait(false);
        if (winner == session.Failure.Task) throw await session.Failure.Task.ConfigureAwait(false);
        if (session.Token.IsCancellationRequested) throw new OperationCanceledException(session.Token);
        if (winner != operation) throw new TimeoutException("WebRTC session operation timed out.");
        await operation.ConfigureAwait(false);
    }

    private static async Task<T> WaitForAsync<T>(Task<T> operation, Session session, TimeSpan timeout)
    {
        await WaitForAsync((Task)operation, session, timeout).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }

    private static async Task WaitForWebRtcAsync(Task operation, Session session, TimeSpan? timeout)
    {
        var timer = timeout is null
            ? Task.Delay(Timeout.InfiniteTimeSpan, session.Token)
            : Task.Delay(timeout.Value, session.Token);
        var winner = await Task.WhenAny(operation, session.Failure.Task, session.PeerFailure.Task, timer).ConfigureAwait(false);
        if (winner == session.Failure.Task) throw await session.Failure.Task.ConfigureAwait(false);
        if (winner == session.PeerFailure.Task) throw await session.PeerFailure.Task.ConfigureAwait(false);
        if (session.Token.IsCancellationRequested) throw new OperationCanceledException(session.Token);
        if (winner != operation) throw new TimeoutException("WebRTC route operation timed out.");
        try { await operation.ConfigureAwait(false); }
        catch when (session.PeerFailure.Task.IsCompleted)
        { throw await session.PeerFailure.Task.ConfigureAwait(false); }
    }

    private static async Task<T> WaitForWebRtcAsync<T>(Task<T> operation, Session session, TimeSpan? timeout)
    {
        await WaitForWebRtcAsync((Task)operation, session, timeout).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }

    private void Report(Session session, WebRtcTransferSessionState state, long bytes, string? error = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (bytes <= 0 || bytes < session.LastRateBytes)
        {
            session.BytesPerSecond = 0;
            session.LastRateBytes = bytes;
            session.LastRateAt = now;
        }
        else if (bytes > session.LastRateBytes && session.LastRateAt is { } lastAt && now > lastAt)
        {
            var sample = (bytes - session.LastRateBytes) / (now - lastAt).TotalSeconds;
            session.BytesPerSecond = session.BytesPerSecond <= 0 ? sample : session.BytesPerSecond * 0.65 + sample * 0.35;
            session.LastRateBytes = bytes;
            session.LastRateAt = now;
        }
        if (state is WebRtcTransferSessionState.Completed or WebRtcTransferSessionState.Failed or
            WebRtcTransferSessionState.Cancelled or WebRtcTransferSessionState.Rejected)
            session.BytesPerSecond = 0;
        session.State = state;
        session.Bytes = bytes;
        ProgressChanged?.Invoke(new WebRtcSessionProgress(session.Id, session.Offer?.FileName ?? "", state, bytes,
            session.Offer?.SizeBytes ?? 0, error, session.ConnectionPath, session.PeerConnectionState, session.IceState,
            session.DataChannelState, session.LocalIceCandidates, session.RemoteIceCandidates, session.IceServerCount,
            session.TurnConfigured, session.BytesPerSecond, session.IntegrityVerified));
    }

    private static void SetIceServerDiagnostics(Session session, IReadOnlyList<string> iceServers)
    {
        session.IceServerCount = iceServers.Count;
        session.TurnConfigured = iceServers.Any(uri =>
            uri.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribePeerState(int state) => state switch
    {
        0 => "new",
        1 => "connecting",
        2 => "connected",
        3 => "disconnected",
        4 => "failed",
        5 => "closed",
        _ => "unknown"
    };

    private static string DescribeIceState(int state) => state switch
    {
        0 => "new",
        1 => "checking",
        2 => "connected",
        3 => "completed",
        4 => "failed",
        5 => "disconnected",
        6 => "closed",
        _ => "unknown"
    };

    private async Task CloseSessionAsync(Session session)
    {
        if (!session.TryBeginClose())
        {
            await session.CloseCompletion.Task.ConfigureAwait(false);
            return;
        }
        try
        {
        _sessions.TryRemove(session.Id, out _);
        session.Frames.Writer.TryComplete(); session.Acks.Writer.TryComplete();
        if (session.Route is not null) _hub.Remove(session.Device.DeviceId, session.Id);
        session.Cancel();
        session.StopSignalLoop();
        if (session.SignalEvents is not null)
        {
            try { await session.SignalEvents.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
        }
        if (session.Peer is not null) await session.Peer.DisposeAsync().ConfigureAwait(false);
        var tasks = new[] { session.PeerEvents, session.SignalEvents }.Where(x => x is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
        await DisposeSignalReaderAsync(session).ConfigureAwait(false);
        session.Dispose();
        }
        finally { session.CloseCompletion.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _hub.IncomingSession -= OnIncomingSignal; _lifetime.Cancel();
        foreach (var session in _sessions.Values) session.Cancel();
        await Task.WhenAll(_sessions.Values.Select(CloseSessionAsync)).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed record SdpPayload(string Sdp, string Type);
    private sealed record CandidatePayload(string Candidate, string? Mid);
    private sealed record ErrorPayload(string? Message);

    private sealed class Session : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly CancellationTokenSource _signalCts;
        private int _closing;
        public Session(string id, DiscoveredDevice device, bool sender, CancellationToken lifetime, CancellationToken caller)
        {
            Id = id; Device = device; Sender = sender;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
            _signalCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        }
        public string Id { get; }
        public DiscoveredDevice Device { get; }
        public bool Sender { get; }
        public CancellationToken Token => _cts.Token;
        public CancellationToken SignalToken => _signalCts.Token;
        public WebRtcPeer Peer { get; set; } = null!;
        public WebRtcSignalingHub.Route Route { get; set; } = null!;
        public IAsyncEnumerator<WebRtcSignal>? SignalReader;
        public TransferOffer? Offer { get; set; }
        public Task? PeerEvents { get; set; }
        public Task? SignalEvents { get; set; }
        public WebRtcTransferSessionState State = WebRtcTransferSessionState.PendingApproval;
        public long Bytes;
        public string? ConnectionPath;
        public string PeerConnectionState = "new";
        public string IceState = "new";
        public string DataChannelState = "waiting";
        public int LocalIceCandidates;
        public int RemoteIceCandidates;
        public int IceServerCount;
        public bool TurnConfigured;
        public double BytesPerSecond;
        public long LastRateBytes;
        public DateTimeOffset? LastRateAt;
        public bool IntegrityVerified;
        public TaskCompletionSource<bool> Acceptance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChannelOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> PeerFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemoteClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<ReadOnlyMemory<byte>> Frames { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        public Channel<long> Acks { get; } = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        public void Cancel() { if (!_cts.IsCancellationRequested) _cts.Cancel(); }
        public void StopSignalLoop() { if (!_signalCts.IsCancellationRequested) _signalCts.Cancel(); }
        public void Fail(Exception exception) { Failure.TrySetResult(exception); Cancel(); }
        public bool TryBeginClose() => Interlocked.Exchange(ref _closing, 1) == 0;
        public void Dispose() { _signalCts.Dispose(); _cts.Dispose(); }
    }
}
