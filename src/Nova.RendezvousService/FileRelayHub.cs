using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Nova.Core.Contracts;
using Nova.RendezvousProtocol;
using Nova.TransferProtocol;

sealed class FileRelayHub : IHostedService
{
    private const int MaximumRelayMessageLength = TransferProtocolCodec.MaximumMessageLength;
    private const int MaximumPendingTransfers = 4096;
    private const int MaximumPendingTickets = 16_384;
    private const int MaximumTicketAttemptsPerPeer = 4;
    private const int MaximumActiveTunnels = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly object _gate = new();
    private readonly Dictionary<string, RelayTransfer> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelayTicketRecord> _tickets = new(StringComparer.Ordinal);
    private readonly long _maximumFileBytes = ReadMaximumFileBytes();
    private int _activeTunnels;
    private CancellationTokenSource? _shutdown;
    private Task? _cleanupLoop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdown = new CancellationTokenSource();
        _cleanupLoop = CleanupLoopAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown?.Cancel();
        if (_cleanupLoop is not null)
        {
            try { await _cleanupLoop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown?.IsCancellationRequested == true) { }
        }

        RelayPair[] pairs;
        lock (_gate)
        {
            pairs = _transfers.Values.Select(x => x.Pair).Where(x => x is not null).Cast<RelayPair>().ToArray();
            _transfers.Clear();
            _tickets.Clear();
        }
        foreach (var pair in pairs) pair.Abort();
    }

    public void RegisterOffer(TransferOffer offer, string senderFingerprint, string senderCertificateDer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var now = DateTimeOffset.UtcNow;
        if (offer.Sender is null || offer.Recipient is null ||
            string.IsNullOrWhiteSpace(offer.FileName) || offer.FileName.Length > 255 ||
            !Guid.TryParseExact(offer.TransferId, "N", out _) ||
            offer.SizeBytes < 0 ||
            !IsSha256(offer.Sha256) ||
            offer.CreatedAt > now.AddMinutes(1) ||
            offer.ExpiresAt <= now || offer.ExpiresAt <= offer.CreatedAt ||
            offer.ExpiresAt > offer.CreatedAt.AddMinutes(5) ||
            !PeerFingerprintValid(senderFingerprint) ||
            !Enum.IsDefined(typeof(TransferTransportPreference), offer.TransportPreference))
            throw new InvalidDataException("Transfer offer is not eligible for the TCP/TLS relay.");

        using var certificate = LoadAndValidateCertificate(senderCertificateDer, offer.Sender.DeviceId, senderFingerprint);
        var state = new RelayTransfer(offer.TransferId, offer.Sender.DeviceId, offer.Recipient.DeviceId,
            offer.SizeBytes, offer.Sha256.ToUpperInvariant(), offer.ExpiresAt, senderFingerprint,
            certificate.RawData);
        lock (_gate)
        {
            CleanupExpiredLocked(DateTimeOffset.UtcNow);
            if (_transfers.Count >= MaximumPendingTransfers)
                throw new IOException("TCP/TLS relay transfer capacity has been reached.");
            if (!_transfers.TryAdd(offer.TransferId, state))
                throw new InvalidDataException("Duplicate transfer id cannot reserve a relay session.");
        }
    }

    public void ApproveOffer(string transferId, string senderDeviceId, string recipientDeviceId,
        string recipientFingerprint, string recipientCertificateDer)
    {
        using var certificate = LoadAndValidateCertificate(recipientCertificateDer, recipientDeviceId, recipientFingerprint);
        lock (_gate)
        {
            CleanupExpiredLocked(DateTimeOffset.UtcNow);
            if (!_transfers.TryGetValue(transferId, out var transfer) ||
                transfer.SenderDeviceId != senderDeviceId || transfer.RecipientDeviceId != recipientDeviceId ||
                transfer.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new AuthenticationException("No matching active transfer offer exists for relay approval.");
            if (transfer.Approved)
                throw new InvalidDataException("Transfer offer has already been approved.");
            transfer.RecipientFingerprint = recipientFingerprint;
            transfer.RecipientCertificateDer = certificate.RawData;
            transfer.Approved = true;
        }
    }

    public void CancelOffer(string transferId, string senderDeviceId, string recipientDeviceId)
    {
        RelayPair? pair = null;
        lock (_gate)
        {
            if (_transfers.TryGetValue(transferId, out var transfer) &&
                ((transfer.SenderDeviceId == senderDeviceId && transfer.RecipientDeviceId == recipientDeviceId) ||
                 (transfer.SenderDeviceId == recipientDeviceId && transfer.RecipientDeviceId == senderDeviceId)))
            {
                pair = transfer.Pair;
                RemoveTransferLocked(transfer);
            }
        }
        pair?.Abort();
    }

    public FileRelayTicket IssueTicket(string transferId, string deviceId, string peerDeviceId,
        string fingerprint, string certificateDer)
    {
        lock (_gate)
        {
            CleanupExpiredLocked(DateTimeOffset.UtcNow);
            if (!_transfers.TryGetValue(transferId, out var transfer) || !transfer.Approved ||
                transfer.ExpiresAt <= DateTimeOffset.UtcNow ||
                !transfer.ContainsPair(deviceId, peerDeviceId))
                throw new AuthenticationException("The transfer is not authorized for TCP/TLS relay.");
            if (transfer.SizeBytes > _maximumFileBytes)
                throw new IOException("Offered file exceeds NOVA_MAX_RELAY_FILE_BYTES.");

            var role = transfer.GetRole(deviceId);
            var expectedFingerprint = role == "sender" ? transfer.SenderFingerprint : transfer.RecipientFingerprint;
            var expectedDer = role == "sender" ? transfer.SenderCertificateDer : transfer.RecipientCertificateDer;
            if (expectedFingerprint is null || expectedDer is null ||
                !string.Equals(expectedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(certificateDer), expectedDer))
                throw new AuthenticationException("Relay requester identity does not match the approved device.");
            if (transfer.Pair?.HasJoined(role) == true)
                throw new IOException("This transfer participant is already connected to the relay.");

            var attempts = role == "sender" ? transfer.SenderTicketCount : transfer.RecipientTicketCount;
            if (attempts >= MaximumTicketAttemptsPerPeer)
                throw new IOException("TCP/TLS relay retry limit has been reached for this transfer.");
            if (_tickets.Count >= MaximumPendingTickets)
                throw new IOException("TCP/TLS relay ticket capacity has been reached.");

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var tokenHash = HashToken(token);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
            if (expiresAt > transfer.ExpiresAt) expiresAt = transfer.ExpiresAt;
            _tickets.Add(tokenHash, new RelayTicketRecord(tokenHash, transfer, deviceId, peerDeviceId, role,
                expectedFingerprint, expectedDer, expiresAt));
            if (role == "sender") transfer.SenderTicketCount++; else transfer.RecipientTicketCount++;
            return new FileRelayTicket(transfer.Id, deviceId, peerDeviceId, role, token, expiresAt);
        }
    }

    public async Task HandleWebSocketAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var hello = await ReceiveJsonAsync<FileRelayHello>(socket, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (hello is null || string.IsNullOrWhiteSpace(hello.Token) || hello.Token.Length > 128 ||
            !Guid.TryParseExact(hello.DeviceId, "N", out _))
            throw new AuthenticationException("Invalid TCP/TLS relay hello.");

        var attempt = BeginJoin(hello);
        try
        {
            await SendJsonAsync(socket, new FileRelayChallenge(attempt.Nonce, attempt.Transfer.Id,
                attempt.PeerDeviceId, attempt.Role), cancellationToken).ConfigureAwait(false);
            var proof = await ReceiveJsonAsync<FileRelayProof>(socket, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (proof is null || !VerifyProof(attempt, proof.Signature))
                throw new AuthenticationException("TCP/TLS relay device proof failed.");

            var pair = await AttachAsync(attempt, socket, cancellationToken).ConfigureAwait(false);
            await SendJsonAsync(socket, new FileRelayReady(attempt.Transfer.Id), cancellationToken).ConfigureAwait(false);
            await pair.RunOrWaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            AbortJoin(attempt);
            throw;
        }
        finally
        {
            EndPair(attempt.Transfer, attempt.Transfer.Pair);
        }
    }

    private RelayJoinAttempt BeginJoin(FileRelayHello hello)
    {
        var tokenHash = HashToken(hello.Token);
        lock (_gate)
        {
            CleanupExpiredLocked(DateTimeOffset.UtcNow);
            if (!_tickets.TryGetValue(tokenHash, out var ticket) || ticket.ExpiresAt <= DateTimeOffset.UtcNow ||
                ticket.DeviceId != hello.DeviceId || ticket.ReservationId is not null || ticket.Consumed)
                throw new AuthenticationException("TCP/TLS relay ticket is invalid, expired, or already used.");
            if (!ticket.Transfer.Approved || ticket.Transfer.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new AuthenticationException("TCP/TLS relay offer is no longer active.");
            if (ticket.Transfer.Pair?.HasJoined(ticket.Role) == true)
                throw new AuthenticationException("A relay connection for this participant already exists.");
            ticket.ReservationId = Guid.NewGuid().ToString("N");
            return new RelayJoinAttempt(ticket, ticket.ReservationId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }
    }

    private static bool VerifyProof(RelayJoinAttempt attempt, string signatureText)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(attempt.Ticket.CertificateDer);
            var transcript = Encoding.UTF8.GetBytes(
                $"NOVA-TCP-TLS-RELAY-v1\n{attempt.Nonce}\n{attempt.Transfer.Id}\n{attempt.Ticket.DeviceId}\n{attempt.PeerDeviceId}\n{attempt.Role}");
            using var rsa = certificate.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(transcript, Convert.FromBase64String(signatureText),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<RelayPair> AttachAsync(RelayJoinAttempt attempt, WebSocket socket, CancellationToken cancellationToken)
    {
        RelayPair pair;
        lock (_gate)
        {
            var ticket = attempt.Ticket;
            if (!_tickets.TryGetValue(ticket.TokenHash, out var current) || current != ticket ||
                ticket.ReservationId != attempt.ReservationId || ticket.Consumed ||
                ticket.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new AuthenticationException("TCP/TLS relay ticket could not be consumed.");
            if (ticket.Transfer.Pair is null)
            {
                if (_activeTunnels >= MaximumActiveTunnels)
                    throw new IOException("TCP/TLS relay tunnel capacity has been reached.");
                _activeTunnels++;
                ticket.Transfer.Pair = new RelayPair(ticket.Transfer, OnPairClosed);
            }
            pair = ticket.Transfer.Pair;
            if (!pair.TryAttach(ticket.Role, socket))
                throw new AuthenticationException("A relay socket is already active for this participant.");
            ticket.Consumed = true;
            ticket.ReservationId = null;
            _tickets.Remove(ticket.TokenHash);
            pair.TryMarkReady();
        }

        try
        {
            await pair.WaitUntilReadyAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            return pair;
        }
        catch
        {
            EndPair(attempt.Transfer, pair);
            throw;
        }
    }

    private void AbortJoin(RelayJoinAttempt attempt)
    {
        lock (_gate)
        {
            if (_tickets.TryGetValue(attempt.Ticket.TokenHash, out var ticket) &&
                ticket.ReservationId == attempt.ReservationId)
                ticket.ReservationId = null;
        }
    }

    private void EndPair(RelayTransfer transfer, RelayPair? pair)
    {
        if (pair is null) return;
        var removed = false;
        lock (_gate)
        {
            if (ReferenceEquals(transfer.Pair, pair))
            {
                transfer.Pair = null;
                if (_activeTunnels > 0) _activeTunnels--;
                foreach (var key in _tickets.Where(x => ReferenceEquals(x.Value.Transfer, transfer)).Select(x => x.Key).ToArray())
                    _tickets.Remove(key);
                if (transfer.ExpiresAt <= DateTimeOffset.UtcNow || transfer.Completed)
                    RemoveTransferLocked(transfer);
                removed = true;
            }
        }
        if (removed) pair.Close();
    }

    private void OnPairClosed(RelayTransfer transfer, RelayPair pair, bool completed)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(transfer.Pair, pair)) return;
            transfer.Pair = null;
            transfer.Completed |= completed;
            foreach (var key in _tickets.Where(x => ReferenceEquals(x.Value.Transfer, transfer)).Select(x => x.Key).ToArray())
                _tickets.Remove(key);
            if (_activeTunnels > 0) _activeTunnels--;
            if (transfer.Completed || transfer.ExpiresAt <= DateTimeOffset.UtcNow)
                RemoveTransferLocked(transfer);
        }
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            RelayPair[] expiredPairs;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                expiredPairs = _transfers.Values.Where(x => x.ExpiresAt <= now && x.Pair is not null)
                    .Select(x => x.Pair!).ToArray();
                foreach (var transfer in _transfers.Values.Where(x => x.ExpiresAt <= now && x.Pair is null).ToArray())
                    RemoveTransferLocked(transfer);
                foreach (var ticket in _tickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
                    _tickets.Remove(ticket);
            }
            foreach (var pair in expiredPairs) pair.Abort();
        }
    }

    private void CleanupExpiredLocked(DateTimeOffset now)
    {
        foreach (var transfer in _transfers.Values.Where(x => x.ExpiresAt <= now && x.Pair is null).ToArray())
            RemoveTransferLocked(transfer);
        foreach (var ticket in _tickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            _tickets.Remove(ticket);
    }

    private void RemoveTransferLocked(RelayTransfer transfer)
    {
        _transfers.Remove(transfer.Id);
        foreach (var key in _tickets.Where(x => ReferenceEquals(x.Value.Transfer, transfer)).Select(x => x.Key).ToArray())
            _tickets.Remove(key);
    }

    private static async Task<T?> ReceiveJsonAsync<T>(WebSocket socket, CancellationToken token) where T : class
    {
        var message = await ReceiveMessageAsync(socket, token).ConfigureAwait(false);
        if (message is null) return null;
        if (message.Value.Type != WebSocketMessageType.Text)
            throw new InvalidDataException("TCP/TLS relay handshake must use text frames.");
        EnsureNoDuplicateProperties(message.Value.Data);
        return JsonSerializer.Deserialize<T>(message.Value.Data, JsonOptions) ??
            throw new InvalidDataException("Empty TCP/TLS relay handshake message.");
    }

    private static async Task SendJsonAsync<T>(WebSocket socket, T payload, CancellationToken token) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions).AsMemory(),
            WebSocketMessageType.Text, true, token).ConfigureAwait(false);

    private static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveMessageAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[MaximumRelayMessageLength];
        var length = 0;
        WebSocketMessageType type = WebSocketMessageType.Binary;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (length == 0) type = result.MessageType;
            else if (type != result.MessageType) throw new InvalidDataException("WebSocket message type changed mid-frame.");
            length += result.Count;
            if (length > MaximumRelayMessageLength)
                throw new InvalidDataException("TCP/TLS relay WebSocket frame exceeds 16 KiB.");
            if (result.EndOfMessage) break;
            if (length == buffer.Length)
                throw new InvalidDataException("TCP/TLS relay WebSocket frame exceeds 16 KiB.");
        }
        if (length == 0) throw new InvalidDataException("Empty TCP/TLS relay WebSocket frames are not allowed.");
        return (type, buffer.AsSpan(0, length).ToArray());
    }

    private static void EnsureNoDuplicateProperties(ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        Visit(document.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Relay handshake contains a duplicate JSON property.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
            }
        }
    }

    private static X509Certificate2 LoadAndValidateCertificate(string certificateDer, string deviceId, string fingerprint)
    {
        X509Certificate2 certificate;
        try { certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateDer)); }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        { throw new AuthenticationException("Relay device certificate is invalid.", ex); }
        if (DeviceSignalCryptography.DeviceIdFromCertificate(certificate) != deviceId ||
            !string.Equals(DeviceSignalCryptography.Fingerprint(certificate), fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            certificate.Dispose();
            throw new AuthenticationException("Relay device certificate does not match its registered identity.");
        }
        return certificate;
    }

    private static bool PeerFingerprintValid(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static long ReadMaximumFileBytes()
    {
        var configured = Environment.GetEnvironmentVariable("NOVA_MAX_RELAY_FILE_BYTES");
        if (string.IsNullOrWhiteSpace(configured)) return 107_374_182_400;
        if (!long.TryParse(configured, out var value) || value is < 1 or > 1_099_511_627_776)
            throw new InvalidOperationException("NOVA_MAX_RELAY_FILE_BYTES must be between 1 byte and 1 TiB.");
        return value;
    }

    private sealed class RelayTransfer(
        string id, string senderDeviceId, string recipientDeviceId, long sizeBytes, string sha256,
        DateTimeOffset expiresAt, string senderFingerprint, byte[] senderCertificateDer)
    {
        public string Id { get; } = id;
        public string SenderDeviceId { get; } = senderDeviceId;
        public string RecipientDeviceId { get; } = recipientDeviceId;
        public long SizeBytes { get; } = sizeBytes;
        public string Sha256 { get; } = sha256;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string SenderFingerprint { get; } = senderFingerprint;
        public byte[] SenderCertificateDer { get; } = senderCertificateDer;
        public string? RecipientFingerprint { get; set; }
        public byte[]? RecipientCertificateDer { get; set; }
        public bool Approved { get; set; }
        public bool Completed { get; set; }
        public int SenderTicketCount { get; set; }
        public int RecipientTicketCount { get; set; }
        public RelayPair? Pair { get; set; }
        public bool ContainsPair(string first, string second) =>
            (SenderDeviceId == first && RecipientDeviceId == second) ||
            (SenderDeviceId == second && RecipientDeviceId == first);
        public string GetRole(string deviceId) => deviceId == SenderDeviceId ? "sender" : "recipient";
    }

    private sealed class RelayTicketRecord(string tokenHash, RelayTransfer transfer, string deviceId,
        string peerDeviceId, string role, string fingerprint, byte[] certificateDer, DateTimeOffset expiresAt)
    {
        public string TokenHash { get; } = tokenHash;
        public RelayTransfer Transfer { get; } = transfer;
        public string DeviceId { get; } = deviceId;
        public string PeerDeviceId { get; } = peerDeviceId;
        public string Role { get; } = role;
        public string Fingerprint { get; } = fingerprint;
        public byte[] CertificateDer { get; } = certificateDer;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string? ReservationId { get; set; }
        public bool Consumed { get; set; }
    }

    private sealed record RelayJoinAttempt(RelayTicketRecord Ticket, string ReservationId, string Nonce)
    {
        public RelayTransfer Transfer => Ticket.Transfer;
        public string PeerDeviceId => Ticket.PeerDeviceId;
        public string Role => Ticket.Role;
    }

    private sealed class RelayPair(RelayTransfer transfer, Action<RelayTransfer, RelayPair, bool> closed)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _shutdown = new();
        private WebSocket? _sender;
        private WebSocket? _recipient;
        private long _forwardedBytes;
        private long _lastAcknowledged;
        private int _runner;
        private int _ended;
        private bool _completed;

        public bool HasJoined(string role)
        {
            lock (_gate) return role == "sender" ? _sender is not null : _recipient is not null;
        }

        public bool TryAttach(string role, WebSocket socket)
        {
            lock (_gate)
            {
                if (role == "sender")
                {
                    if (_sender is not null) return false;
                    _sender = socket;
                }
                else
                {
                    if (_recipient is not null) return false;
                    _recipient = socket;
                }
                return true;
            }
        }

        public void TryMarkReady()
        {
            lock (_gate)
            {
                if (_sender is not null && _recipient is not null) _ready.TrySetResult();
            }
        }

        public Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken token) =>
            _ready.Task.WaitAsync(timeout, token);

        public async Task RunOrWaitAsync(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _runner, 1, 0) != 0)
            {
                await _finished.Task.WaitAsync(token).ConfigureAwait(false);
                return;
            }

            try
            {
                var sender = _sender ?? throw new InvalidOperationException("Relay sender socket is missing.");
                var recipient = _recipient ?? throw new InvalidOperationException("Relay recipient socket is missing.");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
                linked.CancelAfter(TimeSpan.FromHours(6));
                var toRecipient = ForwardAsync(sender, recipient, "sender", linked.Token);
                var toSender = ForwardAsync(recipient, sender, "recipient", linked.Token);
                var first = await Task.WhenAny(toRecipient, toSender).ConfigureAwait(false);
                Exception? failure = null;
                try { await first.ConfigureAwait(false); }
                catch (Exception ex) { failure = ex; }
                linked.Cancel();
                try { await Task.WhenAll(toRecipient, toSender).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (failure is null && ex is not OperationCanceledException) failure = ex;
                }
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            finally
            {
                Close();
                _finished.TrySetResult();
            }
        }

        private async Task ForwardAsync(WebSocket source, WebSocket destination, string role, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var received = await ReceiveMessageAsync(source, token)
                    .WaitAsync(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
                if (received is null) return;
                var message = received.Value;
                if (message.Type != WebSocketMessageType.Binary)
                    throw new InvalidDataException("TCP/TLS relay data channel accepts only NOVA binary frames.");
                ValidateFrame(message.Data, role);
                await destination.SendAsync(message.Data.AsMemory(), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            }
        }

        private void ValidateFrame(byte[] frame, string role)
        {
            if (frame.AsSpan().StartsWith("NVAF"u8))
            {
                if (role != "sender") throw new InvalidDataException("Only the authorized sender may relay file blocks.");
                var block = TransferProtocolCodec.DecodeDataBlock(frame);
                lock (_gate)
                {
                    if (block.TransferId.ToString("N") != transfer.Id ||
                        block.OffsetBytes != _forwardedBytes ||
                        block.Payload.Length > transfer.SizeBytes - _forwardedBytes)
                        throw new InvalidDataException("Relay file block has an invalid transfer id, offset, or size.");
                    _forwardedBytes += block.Payload.Length;
                }
                return;
            }

            var control = TransferProtocolCodec.DecodeControl<JsonElement>(frame);
            if (control.TransferId != transfer.Id) throw new InvalidDataException("Relay control frame belongs to another transfer.");
            switch (control.Type)
            {
                case TransferControlType.BlockAcknowledgement when role == "recipient":
                    var ack = control.Payload.Deserialize<TransferBlockAcknowledgementPayload>(JsonOptions) ??
                        throw new InvalidDataException("Empty relay block acknowledgement.");
                    lock (_gate)
                    {
                        if (ack.DurableOffsetBytes < _lastAcknowledged || ack.DurableOffsetBytes > _forwardedBytes)
                            throw new InvalidDataException("Relay durable acknowledgement is outside the forwarded file range.");
                        _lastAcknowledged = ack.DurableOffsetBytes;
                    }
                    break;
                case TransferControlType.Complete when role == "recipient":
                    var complete = control.Payload.Deserialize<TransferCompletePayload>(JsonOptions) ??
                        throw new InvalidDataException("Empty relay completion confirmation.");
                    lock (_gate)
                    {
                        if (_forwardedBytes != transfer.SizeBytes ||
                            !string.Equals(complete.Sha256, transfer.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Relay completion does not match the offered size and SHA-256.");
                        _completed = true;
                    }
                    break;
                case TransferControlType.Cancel:
                case TransferControlType.Failure:
                    throw new IOException("A participant terminated the TCP/TLS relay transfer.");
                default:
                    throw new InvalidDataException("Unexpected control frame direction or type on the TCP/TLS relay.");
            }
        }

        public void Abort()
        {
            _shutdown.Cancel();
            _sender?.Abort();
            _recipient?.Abort();
            Close();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            _shutdown.Cancel();
            _sender?.Abort();
            _recipient?.Abort();
            _finished.TrySetResult();
            closed(transfer, this, _completed);
        }
    }
}
