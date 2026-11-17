using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nova.Core.Contracts;
using Nova.RendezvousProtocol;
using Nova.TransferProtocol;

namespace Nova.TransferEngine;

public sealed class TcpTlsFileRelayClient(DeviceIdentity identity, InternetRendezvousClient rendezvous)
{
    public async Task<TcpTlsFileRelayConnection> ConnectAsync(TransferOffer offer, string peerDeviceId,
        bool isSender, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var endpoint = rendezvous.FileRelayEndpoint ??
            throw new IOException("NOVA Network is not configured for the TCP/TLS file relay.");
        var ticket = await rendezvous.RequestFileRelayTicketAsync(offer.TransferId, peerDeviceId, token).ConfigureAwait(false);
        return await TcpTlsFileRelayConnection.ConnectAsync(endpoint, ticket, identity, rendezvous,
            offer.TransferId, offer.Sha256, isSender, token).ConfigureAwait(false);
    }
}

public sealed class TcpTlsFileRelayConnection : IAsyncDisposable
{
    private const int MaximumMessageLength = TransferProtocolCodec.MaximumMessageLength;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly ClientWebSocket _socket;
    private readonly string _transferId;
    private readonly string _expectedSha256;
    private readonly bool _isSender;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Channel<ReadOnlyMemory<byte>> _frames = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly Channel<long> _acknowledgements = Channel.CreateBounded<long>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _receiveLoop;
    private bool _disposed;

    private TcpTlsFileRelayConnection(ClientWebSocket socket, string transferId, string expectedSha256, bool isSender)
    {
        _socket = socket;
        _transferId = transferId;
        _expectedSha256 = expectedSha256;
        _isSender = isSender;
        _receiveLoop = ReceiveLoopAsync(_lifetime.Token);
    }

    public static async Task<TcpTlsFileRelayConnection> ConnectAsync(Uri endpoint, FileRelayTicket ticket,
        DeviceIdentity identity, InternetRendezvousClient rendezvous, string transferId, string expectedSha256,
        bool isSender, CancellationToken token)
    {
        if (ticket is null || string.IsNullOrWhiteSpace(ticket.Token) ||
            ticket.ExpiresAt <= DateTimeOffset.UtcNow ||
            endpoint.Scheme != Uri.UriSchemeWss || !Guid.TryParseExact(transferId, "N", out _) ||
            ticket.TransferId != transferId || ticket.DeviceId != identity.Device.DeviceId ||
            ticket.Role != (isSender ? "sender" : "recipient") ||
            expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new AuthenticationException("TCP/TLS file relay connection parameters do not match the authorized transfer.");

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            rendezvous.ValidateServerCertificate(certificate, errors);
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            await SendHandshakeAsync(socket, new FileRelayHello(ticket.Token, identity.Device.DeviceId), token).ConfigureAwait(false);
            var challenge = await ReceiveHandshakeAsync<FileRelayChallenge>(socket, token).ConfigureAwait(false);
            if (challenge.TransferId != transferId || challenge.PeerDeviceId != ticket.PeerDeviceId ||
                challenge.Role != ticket.Role || string.IsNullOrWhiteSpace(challenge.Nonce))
                throw new AuthenticationException("TCP/TLS file relay challenge does not match the ticket.");
            var transcript = Encoding.UTF8.GetBytes(
                $"NOVA-TCP-TLS-RELAY-v1\n{challenge.Nonce}\n{transferId}\n{identity.Device.DeviceId}\n{ticket.PeerDeviceId}\n{ticket.Role}");
            using var rsa = identity.Certificate.GetRSAPrivateKey() ??
                throw new CryptographicException("Device identity has no private signing key.");
            var proof = new FileRelayProof(Convert.ToBase64String(
                rsa.SignData(transcript, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
            await SendHandshakeAsync(socket, proof, token).ConfigureAwait(false);
            var ready = await ReceiveHandshakeAsync<FileRelayReady>(socket, token).WaitAsync(TimeSpan.FromSeconds(95), token).ConfigureAwait(false);
            if (ready.TransferId != transferId)
                throw new AuthenticationException("TCP/TLS file relay paired a different transfer.");
            return new TcpTlsFileRelayConnection(socket, transferId, expectedSha256, isSender);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken token) =>
        SendBinaryAsync(frame, token);

    public ValueTask SendControlAsync<T>(TransferControlType type, T payload, CancellationToken token) =>
        SendBinaryAsync(TransferProtocolCodec.EncodeControl(type, _transferId, payload), token);

    public async ValueTask WaitForDurableAcknowledgementAsync(long expectedOffset, CancellationToken token)
    {
        var actual = await _acknowledgements.Reader.ReadAsync(token).ConfigureAwait(false);
        if (actual != expectedOffset)
            throw new InvalidDataException("TCP/TLS relay acknowledged an unexpected file offset.");
    }

    public async Task WaitForCompletionAsync(CancellationToken token)
    {
        await _completed.Task.WaitAsync(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadDataFramesAsync(CancellationToken token) =>
        _frames.Reader.ReadAllAsync(token);

    private async ValueTask SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (bytes.Length is 0 or > MaximumMessageLength)
            throw new InvalidDataException("TCP/TLS relay frame is empty or exceeds 16 KiB.");
        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(_socket, token).ConfigureAwait(false);
                if (message is null) throw new IOException("TCP/TLS file relay closed before the transfer was confirmed.");
                if (message.Value.Type != WebSocketMessageType.Binary)
                    throw new InvalidDataException("TCP/TLS file relay accepts only binary NOVA transfer frames.");
                var frame = message.Value.Data;
                if (frame.AsSpan().StartsWith("NVAF"u8))
                {
                    if (_isSender) throw new InvalidDataException("Sender received a file block from the relay.");
                    await _frames.Writer.WriteAsync(frame, token).ConfigureAwait(false);
                    continue;
                }

                var control = TransferProtocolCodec.DecodeControl<JsonElement>(frame);
                if (control.TransferId != _transferId)
                    throw new InvalidDataException("TCP/TLS relay control frame belongs to another transfer.");
                switch (control.Type)
                {
                    case TransferControlType.BlockAcknowledgement when _isSender:
                        var ack = control.Payload.Deserialize<TransferBlockAcknowledgementPayload>(JsonOptions) ??
                            throw new InvalidDataException("Empty TCP/TLS relay block acknowledgement.");
                        await _acknowledgements.Writer.WriteAsync(ack.DurableOffsetBytes, token).ConfigureAwait(false);
                        break;
                    case TransferControlType.Complete when _isSender:
                        var complete = control.Payload.Deserialize<TransferCompletePayload>(JsonOptions) ??
                            throw new InvalidDataException("Empty TCP/TLS relay completion confirmation.");
                        if (!string.Equals(complete.Sha256, _expectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Receiver confirmed a different SHA-256 hash.");
                        _completed.TrySetResult();
                        break;
                    case TransferControlType.Cancel:
                        throw new OperationCanceledException("Remote participant cancelled the TCP/TLS relay transfer.", token);
                    case TransferControlType.Failure:
                        throw new IOException("Remote participant reported a TCP/TLS relay transfer failure.");
                    default:
                        throw new InvalidDataException($"Unexpected TCP/TLS relay control type {control.Type}.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _frames.Writer.TryComplete(failure);
            _acknowledgements.Writer.TryComplete(failure);
            if (failure is not null && _isSender) _completed.TrySetException(failure);
            else if (failure is not null && !_completed.Task.IsCompleted) _completed.TrySetException(failure);
        }
    }

    private static async Task SendHandshakeAsync<T>(ClientWebSocket socket, T payload, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (bytes.Length > MaximumMessageLength)
            throw new InvalidDataException("TCP/TLS relay handshake frame exceeds its limit.");
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    private static async Task<T> ReceiveHandshakeAsync<T>(ClientWebSocket socket, CancellationToken token)
    {
        var message = await ReceiveMessageAsync(socket, token).ConfigureAwait(false);
        if (message is null || message.Value.Type != WebSocketMessageType.Text)
            throw new AuthenticationException("TCP/TLS relay handshake response is missing.");
        return JsonSerializer.Deserialize<T>(message.Value.Data, JsonOptions) ??
            throw new InvalidDataException("TCP/TLS relay handshake response is empty.");
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[MaximumMessageLength];
        var length = 0;
        WebSocketMessageType type = WebSocketMessageType.Binary;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (length == 0) type = result.MessageType;
            else if (type != result.MessageType) throw new InvalidDataException("WebSocket message type changed mid-frame.");
            length += result.Count;
            if (length > MaximumMessageLength || (length == buffer.Length && !result.EndOfMessage))
                throw new InvalidDataException("TCP/TLS relay WebSocket frame exceeds 16 KiB.");
            if (result.EndOfMessage) break;
        }
        if (length == 0) throw new InvalidDataException("Empty TCP/TLS relay WebSocket frame.");
        return (type, buffer.AsSpan(0, length).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Transfer finished", CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { _socket.Abort(); }
        }
        _socket.Abort();
        try { await _receiveLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
