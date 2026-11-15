using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Net.WebSockets;
using Nova.Core.Contracts;
using Nova.TransferProtocol;

namespace Nova.TransferEngine;

public enum WebRtcTransferSessionState { PendingApproval, Connecting, Transferring, Verifying, Completed, Rejected, Cancelled, Failed }
public sealed record WebRtcSessionProgress(string TransferId, string FileName, WebRtcTransferSessionState State, long BytesTransferred, long TotalBytes, string? Error = null, string? ConnectionPath = null);
public sealed class WebRtcTransferRejectedException(string fileName) : IOException($"The receiver rejected '{fileName}'.");

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
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public WebRtcTransferCoordinator(DeviceIdentity identity, PeerTrustStore trust, TransferSettingsStore settings,
        WebRtcSignalingHub hub, Func<DiscoveredDevice, WebRtcSignal, CancellationToken, Task> publish,
        Func<string, DiscoveredDevice?> resolve, Func<CancellationToken, Task<IReadOnlyList<string>>>? iceServers = null)
    {
        _identity = identity; _trust = trust; _settings = settings; _hub = hub; _publish = publish; _resolve = resolve;
        _iceServers = iceServers;
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
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3), false);
            session.Offer = offer;
            session.Peer = WebRtcPeer.Create(await GetIceServersAsync(session.Device, session.Token).ConfigureAwait(false));
            session.Peer.CreateReliableChannel("nova-transfer-" + transferId[..8]);
            session.PeerEvents = SuperviseAsync(session, PeerEventLoopAsync(session));
            session.SignalEvents = SuperviseAsync(session, SignalLoopAsync(session));
            Report(session, WebRtcTransferSessionState.PendingApproval, 0);
            await PublishAsync(session, "offer", offer).ConfigureAwait(false);
            var accepted = await WaitForAsync(session.Acceptance.Task, session, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            if (!accepted) { Report(session, WebRtcTransferSessionState.Rejected, 0); return false; }
            Report(session, WebRtcTransferSessionState.Connecting, 0);
            session.Peer.CreateOffer();
            await WaitForAsync(session.ChannelOpen.Task, session, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
            session.ConnectionPath = DescribePath(session.Peer.GetSelectedCandidatePair());
            Report(session, WebRtcTransferSessionState.Transferring, 0);
            await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await WebRtcFileBlockTransfer.SendAsync(source, Guid.ParseExact(transferId, "N"), offer.SizeBytes, offer.Sha256,
                (frame, ct) => session.Peer.SendAsync(frame, ct),
                async (durableOffset, ct) =>
                {
                    var ack = await session.Acks.Reader.ReadAsync(ct).ConfigureAwait(false);
                    if (ack != durableOffset) throw new InvalidDataException("Receiver acknowledged an unexpected file offset.");
                }, new Progress<long>(bytes => Report(session, WebRtcTransferSessionState.Transferring, bytes)), session.Token).ConfigureAwait(false);
            Report(session, WebRtcTransferSessionState.Verifying, offer.SizeBytes);
            await WaitForAsync(session.Completed.Task, session, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            Report(session, WebRtcTransferSessionState.Completed, offer.SizeBytes);
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
            if (offer.TransferId != incoming.SessionId || offer.Sender.DeviceId != incoming.PeerDeviceId ||
                offer.Recipient.DeviceId != _identity.Device.DeviceId || offer.SizeBytes < 0)
                throw new InvalidDataException("Offer identity, session, or size does not match signaling.");
            if (offer.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(1) || offer.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("The incoming transfer offer is not currently valid.");
            _ = TransferFileSecurity.ValidateAndResolve(_settings.DestinationDirectory, offer.FileName);
            var peer = _resolve(incoming.PeerDeviceId) ?? throw new IOException("The trusted sender is no longer available in discovery.");
            if (!_trust.IsTrusted(peer.Fingerprint)) throw new UnauthorizedAccessException("Incoming signaling peer is no longer trusted.");
            var session = new Session(offer.TransferId, peer, false, _lifetime.Token, token) { Route = incoming.Route, Offer = offer, SignalReader = routeReader };
            if (!_sessions.TryAdd(offer.TransferId, session)) throw new InvalidDataException("Duplicate active transfer id.");
            routeReader = null!;
            try
            {
                session.Peer = WebRtcPeer.Create(await GetIceServersAsync(session.Device, session.Token).ConfigureAwait(false));
                session.PeerEvents = SuperviseAsync(session, PeerEventLoopAsync(session));
                session.SignalEvents = SuperviseAsync(session, SignalLoopAsync(session));
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
                Report(session, WebRtcTransferSessionState.Connecting, 0);
                await WaitForAsync(session.ChannelOpen.Task, session, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
                session.ConnectionPath = DescribePath(session.Peer.GetSelectedCandidatePair());
                Report(session, WebRtcTransferSessionState.Transferring, 0);
                var received = WebRtcFileBlockTransfer.ReceiveAsync(session.Frames.Reader.ReadAllAsync(session.Token),
                    Guid.ParseExact(offer.TransferId, "N"), _settings.DestinationDirectory, offer.FileName, offer.SizeBytes, offer.Sha256,
                    async (offset, ct) =>
                    {
                        var ack = new TransferBlockAcknowledgementPayload(offset);
                        await session.Peer.SendControlAsync(TransferControlType.BlockAcknowledgement, offer.TransferId, ack, ct).ConfigureAwait(false);
                    }, new Progress<long>(bytes => Report(session, WebRtcTransferSessionState.Transferring, bytes)), cancellationToken: session.Token);
                var finalPath = await received.ConfigureAwait(false);
                Report(session, WebRtcTransferSessionState.Verifying, offer.SizeBytes);
                await session.Peer.SendControlAsync(TransferControlType.Complete, offer.TransferId,
                    new TransferCompletePayload(offer.Sha256), session.Token).ConfigureAwait(false);
                Report(session, WebRtcTransferSessionState.Completed, offer.SizeBytes);
                _ = finalPath;
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

    private async Task PeerEventLoopAsync(Session session)
    {
        await foreach (var ev in session.Peer.Events(session.Token).ConfigureAwait(false))
        {
            switch (ev.Kind)
            {
                case 1:
                    if (ev.Data.Length == sizeof(int) && BitConverter.ToInt32(ev.Data) is 4 or 5)
                        throw new IOException("WebRTC peer connection failed or closed.");
                    break;
                case 2:
                    await PublishAsync(session, "sdp", new { sdp = System.Text.Encoding.UTF8.GetString(ev.Data), type = ev.Detail }).ConfigureAwait(false);
                    break;
                case 3:
                    await PublishAsync(session, "candidate", new { candidate = System.Text.Encoding.UTF8.GetString(ev.Data), mid = ev.Detail }).ConfigureAwait(false);
                    break;
                case 4: session.ChannelOpen.TrySetResult(); break;
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
                            case TransferControlType.Complete: session.Completed.TrySetResult(); break;
                            case TransferControlType.Cancel: session.Cancel(); break;
                            case TransferControlType.Failure: throw new IOException("The receiver reported a transfer failure.");
                            default: throw new InvalidDataException($"Unexpected transfer control {control.Type}.");
                        }
                    }
                    break;
                case 6: session.ChannelOpen.TrySetException(new IOException("Remote DataChannel closed.")); break;
                case 7: break;
            }
        }
    }

    private async Task SignalLoopAsync(Session session)
    {
        var pendingCandidates = new Queue<(string Candidate, string? Mid)>();
        var remoteDescriptionSet = false;
        await foreach (var signal in session.Route.ReadAllAsync(session.Token).ConfigureAwait(false))
        {
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

    private async Task SuperviseAsync(Session session, Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        catch (Exception ex) { session.Fail(ex); }
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

    private void Report(Session session, WebRtcTransferSessionState state, long bytes, string? error = null)
    {
        session.Bytes = bytes;
        ProgressChanged?.Invoke(new WebRtcSessionProgress(session.Id, session.Offer?.FileName ?? "", state, bytes, session.Offer?.SizeBytes ?? 0, error, session.ConnectionPath));
    }

    private static string? DescribePath(WebRtcPeer.SelectedCandidatePair? pair)
    {
        if (pair is null) return null;
        return pair.IsRelay ? "TURN relay" : "direct";
    }

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
        if (session.Peer is not null) await session.Peer.DisposeAsync().ConfigureAwait(false);
        var tasks = new[] { session.PeerEvents, session.SignalEvents }.Where(x => x is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
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
        private int _closing;
        public Session(string id, DiscoveredDevice device, bool sender, CancellationToken lifetime, CancellationToken caller)
        {
            Id = id; Device = device; Sender = sender; _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
        }
        public string Id { get; }
        public DiscoveredDevice Device { get; }
        public bool Sender { get; }
        public CancellationToken Token => _cts.Token;
        public WebRtcPeer Peer { get; set; } = null!;
        public WebRtcSignalingHub.Route Route { get; set; } = null!;
        public IAsyncEnumerator<WebRtcSignal>? SignalReader { get; set; }
        public TransferOffer? Offer { get; set; }
        public Task? PeerEvents { get; set; }
        public Task? SignalEvents { get; set; }
        public long Bytes;
        public string? ConnectionPath;
        public TaskCompletionSource<bool> Acceptance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChannelOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemoteClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<ReadOnlyMemory<byte>> Frames { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        public Channel<long> Acks { get; } = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        public void Cancel() { if (!_cts.IsCancellationRequested) _cts.Cancel(); }
        public void Fail(Exception exception) { Failure.TrySetResult(exception); Cancel(); }
        public bool TryBeginClose() => Interlocked.Exchange(ref _closing, 1) == 0;
        public void Dispose() => _cts.Dispose();
    }
}
