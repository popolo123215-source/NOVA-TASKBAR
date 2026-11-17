using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Collections.Concurrent;
using Nova.Core.Contracts;
using Nova.RendezvousProtocol;

namespace Nova.TransferEngine;

public sealed class LocalTransferService : IAsyncDisposable
{
    private static readonly TimeSpan DirectVpnConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DirectVpnNetworkIdleTimeout = TimeSpan.FromMinutes(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionLimit = new(4, 4);
    private readonly List<Task> _handlers = [];
    private readonly object _handlerGate = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _directVpnTransfers = new(StringComparer.Ordinal);
    private readonly TcpListener _listener;
    private readonly WebRtcSignalingHub _webRtcSignaling = new();
    private readonly ConcurrentDictionary<string, long> _seenSignalNonces = new(StringComparer.Ordinal);
    private readonly InternetRendezvousClient _internet;
    private readonly TcpTlsFileRelayClient _fileRelay;
    private readonly WebRtcTransferCoordinator _webRtcCoordinator;
    private readonly bool _ownsIdentity;
    private LanDiscoveryService? _discovery;
    private Task? _acceptLoop;
    private bool _started;
    private bool _disposed;

    public LocalTransferService(string stateDirectory, string? displayName = null, string? destinationDirectory = null, DeviceIdentity? identity = null)
    {
        Directory.CreateDirectory(stateDirectory);
        Identity = identity ?? DeviceIdentity.LoadOrCreate(stateDirectory, displayName);
        _ownsIdentity = identity is null;
        TrustedPeers = new PeerTrustStore(stateDirectory);
        History = new TransferHistoryStore(stateDirectory);
        Settings = new TransferSettingsStore(stateDirectory);
        InternetSettings = new InternetModeSettingsStore(stateDirectory);
        _internet = new InternetRendezvousClient(Identity, TrustedPeers);
        _fileRelay = new TcpTlsFileRelayClient(Identity, _internet);
        _internet.SignalReceived += OnInternetSignalReceived;
        _internet.StatusChanged += status => InternetStatusChanged?.Invoke(status);
        _internet.DevicesChanged += devices => InternetDevicesChanged?.Invoke(devices);
        if (!string.IsNullOrWhiteSpace(destinationDirectory)) Settings.SetDestinationDirectory(destinationDirectory);
        _webRtcCoordinator = new WebRtcTransferCoordinator(Identity, TrustedPeers, Settings, _webRtcSignaling,
            SendWebRtcSignalAsync, deviceId => GetAvailableDevices().FirstOrDefault(x => x.DeviceId == deviceId),
            GetIceServerUrisAsync, _fileRelay);
        _webRtcCoordinator.IncomingOffer += request => WebRtcIncomingOffer?.Invoke(request);
        _webRtcCoordinator.ProgressChanged += progress => WebRtcProgressChanged?.Invoke(progress);
        _listener = new TcpListener(IPAddress.Any, 0);
    }

    public DeviceIdentity Identity { get; }
    public PeerTrustStore TrustedPeers { get; }
    public TransferHistoryStore History { get; }
    public TransferSettingsStore Settings { get; }
    public InternetModeSettingsStore InternetSettings { get; }
    public InternetRendezvousClient Internet => _internet;
    public WebRtcSignalingHub WebRtcSignaling => _webRtcSignaling;
    public WebRtcTransferCoordinator WebRtcCoordinator => _webRtcCoordinator;
    public int TcpPort { get; private set; }
    public bool IsStarted => _started;

    public event Action<IncomingOfferRequest>? IncomingOffer;
    public event Action<WebRtcIncomingOfferRequest>? WebRtcIncomingOffer;
    public event Action<LocalTransferProgress>? ProgressChanged;
    public event Action<WebRtcSessionProgress>? WebRtcProgressChanged;
    public event Action<InternetConnectionStatus>? InternetStatusChanged;
    public event Action<IReadOnlyList<RendezvousProtocol.RendezvousDevice>>? InternetDevicesChanged;
    public event Action<TransferHistoryEntry>? HistoryChanged;

    public async Task SendWebRtcSignalAsync(DiscoveredDevice recipient, WebRtcSignal signal, CancellationToken cancellationToken = default)
    {
        var trusted = TrustedPeers.Find(recipient.Fingerprint);
        if (trusted is null || trusted.Device.DeviceId != recipient.DeviceId) throw new AuthenticationException("Signaling target is not a trusted device.");
        if (!recipient.IsAvailableOnLan || recipient.Port <= 0)
        {
            if (!recipient.IsAvailableOnInternet) throw new IOException("The recipient has no available signaling route.");
            await _internet.SendSignalAsync(recipient, signal, cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await SendLanWebRtcSignalAsync(recipient, signal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (recipient.IsAvailableOnInternet && ex is (SocketException or IOException))
        {
            await _internet.SendSignalAsync(recipient, signal, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendLanWebRtcSignalAsync(DiscoveredDevice recipient, WebRtcSignal signal, CancellationToken cancellationToken)
    {
        var trusted = TrustedPeers.Find(recipient.Fingerprint) ?? throw new AuthenticationException("Signaling target is not a trusted device.");
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(recipient.Address, recipient.Port, cancellationToken).ConfigureAwait(false);
        using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
            Fingerprint(certificate) == trusted.Fingerprint && DeviceIdentity.DeviceIdFromCertificate(certificate) == trusted.Device.DeviceId);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "nova-lan-peer", ClientCertificates = new X509CertificateCollection { Identity.Certificate },
            EnabledSslProtocols = SslProtocols.None, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, cancellationToken).ConfigureAwait(false);
        var signedSignal = WebRtcSignalSecurity.Sign(Identity, signal);
        await FramedJson.WriteAsync(ssl, WireEnvelope.Create("webrtc-signal", signedSignal), cancellationToken).ConfigureAwait(false);
        var response = await FramedJson.ReadAsync<WireEnvelope>(ssl, cancellationToken).ConfigureAwait(false);
        response.EnsureVersion();
        if (response.Type != "signal-ack" || !response.ReadPayload<WireReply>().Accepted)
            throw new IOException("The remote signaling endpoint rejected the message.");
    }

    public void Start(bool enableDiscovery = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _listener.Start(32);
        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        try
        {
            if (enableDiscovery)
            {
                var discovery = new LanDiscoveryService(Identity, TcpPort, TrustedPeers.IsTrusted);
                try
                {
                    discovery.Start();
                    _discovery = discovery;
                }
                catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
                {
                    discovery.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    Console.Error.WriteLine($"[NOVA Transfer Engine] LAN discovery is unavailable; the direct TCP listener remains active: {ex}");
                }
            }
            _started = true;
            _acceptLoop = AcceptLoopAsync(_lifetime.Token);
            if (InternetSettings.Endpoint is not null && Uri.TryCreate(InternetSettings.Endpoint, UriKind.Absolute, out var internetEndpoint))
                _ = _internet.ConnectAsync(internetEndpoint);
        }
        catch
        {
            _listener.Stop();
            _discovery?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _discovery = null;
            throw;
        }
    }

    public IReadOnlyList<DiscoveredDevice> GetAvailableDevices()
    {
        var byId = (_discovery?.Snapshot() ?? []).ToDictionary(x => x.DeviceId, StringComparer.Ordinal);
        foreach (var remote in _internet.Devices)
        {
            if (byId.TryGetValue(remote.DeviceId, out var lan))
            {
                byId[remote.DeviceId] = lan with { Source = lan.Source | DeviceDiscoverySource.Internet, IsInternetOnline = remote.Online };
                continue;
            }
            byId[remote.DeviceId] = new DiscoveredDevice(remote.DeviceId, remote.DisplayName, IPAddress.None, 0,
                remote.Fingerprint, DateTimeOffset.UtcNow, true, DeviceDiscoverySource.Internet, remote.Online, remote.Online);
        }
        foreach (var trusted in TrustedPeers.GetAll())
        {
            if (!byId.ContainsKey(trusted.Device.DeviceId))
                byId[trusted.Device.DeviceId] = new DiscoveredDevice(trusted.Device.DeviceId, trusted.Device.DisplayName,
                    IPAddress.None, 0, trusted.Fingerprint, trusted.TrustedAt, true, DeviceDiscoverySource.None, false);
        }
        return byId.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public void TrustDevice(DiscoveredDevice device) => TrustedPeers.Trust(device);
    public bool RemoveTrustedDevice(string fingerprint) => TrustedPeers.Remove(fingerprint);

    public void SetDestinationDirectory(string path) => Settings.SetDestinationDirectory(path);

    public Task ConnectInternetAsync(string endpoint)
    {
        InternetSettings.SetEndpoint(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw new ArgumentException("Invalid rendezvous URL.", nameof(endpoint));
        return _internet.ConnectAsync(uri);
    }

    private async Task<IReadOnlyList<string>> GetIceServerUrisAsync(CancellationToken token)
    {
        if (_internet.Status.State != InternetConnectionState.Connected) return [];
        return await _internet.GetIceServerUrisAsync(token).ConfigureAwait(false);
    }

    private void OnInternetSignalReceived(string senderId, WebRtcSignal signal)
    {
        _ = _webRtcSignaling.PublishAsync(senderId, signal, _lifetime.Token);
    }

    public Task SendWebRtcFilesAsync(DiscoveredDevice recipient, IEnumerable<string> filePaths, CancellationToken cancellationToken = default) =>
        _webRtcCoordinator.SendFilesAsync(recipient, filePaths, cancellationToken);

    public bool CancelWebRtcTransfer(string transferId) => _webRtcCoordinator.Cancel(transferId);

    public async Task SendFilesAsync(DiscoveredDevice recipient, IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (!TrustedPeers.IsTrusted(recipient.Fingerprint))
            throw new AuthenticationException("Сначала подтвердите отпечаток устройства-получателя.");
        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendFileAsync(recipient, path, cancellationToken);
        }
    }

    public async Task SendDirectVpnFilesAsync(DiscoveredDevice recipient, IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (recipient.Address.AddressFamily != AddressFamily.InterNetwork || recipient.Port is < 1 or > 65535)
            throw new ArgumentException("Direct VPN requires a numeric IPv4 address and a valid TCP port.", nameof(recipient));
        if (!TrustedPeers.IsTrusted(recipient.Fingerprint))
            throw new AuthenticationException("Сначала подтвердите отпечаток устройства-получателя.");
        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendFileCoreAsync(recipient, path, TransferTransportPreference.DirectVpn, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool CancelDirectVpnTransfer(string transferId)
    {
        if (!_directVpnTransfers.TryGetValue(transferId, out var cancellation)) return false;
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    public Task SendFileAsync(DiscoveredDevice recipient, string filePath, CancellationToken cancellationToken = default) =>
        SendFileCoreAsync(recipient, filePath, TransferTransportPreference.Automatic, cancellationToken);

    private async Task SendFileCoreAsync(DiscoveredDevice recipient, string filePath,
        TransferTransportPreference transportPreference, CancellationToken cancellationToken)
    {
        if (!_started) throw new InvalidOperationException("Служба передачи не запущена.");
        var trusted = TrustedPeers.Find(recipient.Fingerprint);
        if (trusted is null || trusted.Device.DeviceId != recipient.DeviceId)
            throw new AuthenticationException("Устройство не подтверждено.");
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException("Не удалось найти выбранный файл.", filePath);

        string transferId = Guid.NewGuid().ToString("N");
        string fileName = Path.GetFileName(info.FullName);
        using var directVpnCancellation = transportPreference == TransferTransportPreference.DirectVpn
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token)
            : null;
        var transferToken = directVpnCancellation?.Token ?? cancellationToken;
        if (directVpnCancellation is not null && !_directVpnTransfers.TryAdd(transferId, directVpnCancellation))
            throw new InvalidOperationException("Direct VPN transfer id collision.");
        try
        {
            byte[] digest;
            await using (var source = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                digest = await SHA256.HashDataAsync(source, transferToken);

            var offer = new TransferOffer(transferId, Identity.Device, trusted.Device, fileName, info.Length, null,
                Convert.ToHexString(digest), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3), false)
                { TransportPreference = transportPreference };

            using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            if (transportPreference == TransferTransportPreference.DirectVpn)
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(transferToken);
                connectTimeout.CancelAfter(DirectVpnConnectTimeout);
                try { await client.ConnectAsync(recipient.Address, recipient.Port, connectTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!transferToken.IsCancellationRequested)
                { throw new TimeoutException("Direct VPN TCP connection timed out.", ex); }
            }
            else
            {
                await client.ConnectAsync(recipient.Address, recipient.Port, transferToken).ConfigureAwait(false);
            }
            using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null &&
                Fingerprint(certificate) == trusted.Fingerprint &&
                DeviceIdentity.DeviceIdFromCertificate(certificate) == trusted.Device.DeviceId);
            var clientTlsOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "nova-lan-peer",
                ClientCertificates = new X509CertificateCollection { Identity.Certificate },
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };
            if (transportPreference == TransferTransportPreference.DirectVpn)
            {
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(transferToken);
                handshakeTimeout.CancelAfter(DirectVpnConnectTimeout);
                try { await ssl.AuthenticateAsClientAsync(clientTlsOptions, handshakeTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!transferToken.IsCancellationRequested)
                { throw new TimeoutException("Direct VPN TLS authentication timed out.", ex); }
            }
            else
            {
                await ssl.AuthenticateAsClientAsync(clientTlsOptions, transferToken).ConfigureAwait(false);
            }

            TransferProgressChanged(offer, TransferState.Offered, 0);
            await FramedJson.WriteAsync(ssl, WireEnvelope.Create("offer", offer), transferToken);
            var decisionTask = FramedJson.ReadAsync<WireEnvelope>(ssl, transferToken);
            var decision = transportPreference == TransferTransportPreference.DirectVpn
                ? await decisionTask.WaitAsync(TimeSpan.FromMinutes(3), transferToken).ConfigureAwait(false)
                : await decisionTask.ConfigureAwait(false);
            decision.EnsureVersion();
            if (decision.Type != "decision") throw new InvalidDataException("Получен неожиданный ответ устройства.");
            var reply = decision.ReadPayload<WireReply>();
            if (!reply.Accepted)
            {
                ProgressChanged?.Invoke(new LocalTransferProgress(transferId, fileName, TransferState.Rejected, 0, offer.SizeBytes));
                AddHistory(new TransferHistoryEntry(offer.TransferId, TransferDirection.Outgoing, trusted.Device.DisplayName,
                    fileName, info.Length, DateTimeOffset.UtcNow, TransferState.Rejected, "Получатель отклонил файл."));
                return;
            }

            await using (var source = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long sent = 0;
                try
                {
                    while (sent < offer.SizeBytes)
                    {
                        transferToken.ThrowIfCancellationRequested();
                        var wanted = (int)Math.Min(buffer.Length, offer.SizeBytes - sent);
                        var read = await source.ReadAsync(buffer.AsMemory(0, wanted), transferToken);
                        if (read == 0) throw new EndOfStreamException("Исходный файл изменился во время передачи.");
                        if (transportPreference == TransferTransportPreference.DirectVpn)
                            await WriteWithTimeoutAsync(ssl, buffer.AsMemory(0, read), transferToken, DirectVpnNetworkIdleTimeout).ConfigureAwait(false);
                        else
                            await ssl.WriteAsync(buffer.AsMemory(0, read), transferToken).ConfigureAwait(false);
                        sent += read;
                        ProgressChanged?.Invoke(new LocalTransferProgress(transferId, fileName, TransferState.Transferring, sent, offer.SizeBytes));
                    }
                    await ssl.FlushAsync(transferToken);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            var completeTask = FramedJson.ReadAsync<WireEnvelope>(ssl, transferToken);
            var complete = transportPreference == TransferTransportPreference.DirectVpn
                ? await completeTask.WaitAsync(DirectVpnNetworkIdleTimeout, transferToken).ConfigureAwait(false)
                : await completeTask.ConfigureAwait(false);
            complete.EnsureVersion();
            if (complete.Type != "complete") throw new InvalidDataException("Получен неожиданный ответ о завершении.");
            var result = complete.ReadPayload<WireReply>();
            if (!result.Accepted) throw new InvalidDataException(result.Message ?? "Получатель не подтвердил целостность файла.");
            ProgressChanged?.Invoke(new LocalTransferProgress(transferId, fileName, TransferState.Completed, offer.SizeBytes, offer.SizeBytes));
            AddHistory(new TransferHistoryEntry(transferId, TransferDirection.Outgoing, trusted.Device.DisplayName,
                fileName, offer.SizeBytes, DateTimeOffset.UtcNow, TransferState.Completed));
        }
        catch (OperationCanceledException) when (directVpnCancellation?.IsCancellationRequested == true)
        {
            ProgressChanged?.Invoke(new LocalTransferProgress(transferId, fileName, TransferState.Cancelled, 0, info.Exists ? info.Length : 0));
            AddHistory(new TransferHistoryEntry(transferId, TransferDirection.Outgoing, trusted.Device.DisplayName,
                fileName, info.Exists ? info.Length : 0, DateTimeOffset.UtcNow, TransferState.Cancelled, "Передача отменена."));
            throw;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProgressChanged?.Invoke(new LocalTransferProgress(transferId, fileName, TransferState.Failed, 0, info.Exists ? info.Length : 0));
            AddHistory(new TransferHistoryEntry(transferId, TransferDirection.Outgoing, trusted.Device.DisplayName,
                fileName, info.Exists ? info.Length : 0, DateTimeOffset.UtcNow, TransferState.Failed, ex.Message));
            throw;
        }
        finally
        {
            if (directVpnCancellation is not null)
                _directVpnTransfers.TryRemove(transferId, out _);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await _connectionLimit.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (cancellationToken.IsCancellationRequested) break; else continue; }

            client.NoDelay = true;
            var handler = HandleClientGuardedAsync(client, cancellationToken);
            lock (_handlerGate) _handlers.Add(handler);
        }
    }

    private async Task HandleClientGuardedAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await HandleClientAsync(client, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NOVA Transfer Engine] Ошибка входящего подключения: {ex.ToString()}");
        }
        finally
        {
            client.Dispose();
            _connectionLimit.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => IsTrustedClientCertificate(certificate));
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(DirectVpnConnectTimeout);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = Identity.Certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.None
            }, handshakeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Incoming TCP/TLS authentication timed out.", ex);
        }

        var peerFingerprint = Fingerprint(ssl.RemoteCertificate ?? throw new AuthenticationException("Не предоставлен сертификат устройства."));
        var peer = TrustedPeers.Find(peerFingerprint) ?? throw new AuthenticationException("Устройство не подтверждено.");
        WireEnvelope envelope;
        try
        {
            envelope = await FramedJson.ReadAsync<WireEnvelope>(ssl, handshakeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Incoming TCP/TLS request timed out.", ex);
        }
        envelope.EnsureVersion();
        if (envelope.Type == "webrtc-signal")
        {
            using var peerCertificate = X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate!.GetRawCertData());
            var signed = envelope.ReadPayload<DeviceSignalEnvelope>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var stale in _seenSignalNonces.Where(x => x.Value < now - 180_000).Select(x => x.Key).ToArray()) _seenSignalNonces.TryRemove(stale, out _);
            if (!_seenSignalNonces.TryAdd(signed.Nonce, now)) throw new AuthenticationException("Repeated WebRTC signaling nonce.");
            var signal = WebRtcSignalSecurity.VerifyAndConvert(signed, peerCertificate, peer.Device.DeviceId, peer.Fingerprint);
            await _webRtcSignaling.PublishAsync(peer.Device.DeviceId, signal, cancellationToken).ConfigureAwait(false);
            await FramedJson.WriteAsync(ssl, WireEnvelope.Create("signal-ack", new WireReply(true)), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (envelope.Type != "offer") throw new InvalidDataException("Ожидалось предложение передачи.");
        var untrustedOffer = envelope.ReadPayload<TransferOffer>();
        ValidateOffer(untrustedOffer, peer.Device.DeviceId);
        var offer = untrustedOffer with { Sender = peer.Device, Recipient = Identity.Device };

        var request = new IncomingOfferRequest(offer);
        IncomingOffer?.Invoke(request);
        var accepted = await request.WaitForResponseAsync(cancellationToken);
        if (!accepted)
        {
            await FramedJson.WriteAsync(ssl, WireEnvelope.Create("decision", new WireReply(false, "Получатель отклонил передачу.")), cancellationToken);
            AddHistory(new TransferHistoryEntry(offer.TransferId, TransferDirection.Incoming, peer.Device.DisplayName,
                offer.FileName, offer.SizeBytes, DateTimeOffset.UtcNow, TransferState.Rejected, "Передача отклонена."));
            return;
        }

        await FramedJson.WriteAsync(ssl, WireEnvelope.Create("decision", new WireReply(true)), cancellationToken);
        ProgressChanged?.Invoke(new LocalTransferProgress(offer.TransferId, offer.FileName, TransferState.Accepted, 0, offer.SizeBytes));
        var temporary = Path.Combine(Settings.DestinationDirectory, $".nova-{Guid.NewGuid():N}.part");
        string? finalPath = null;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long received = 0;
                try
                {
                    while (received < offer.SizeBytes)
                    {
                        var wanted = (int)Math.Min(buffer.Length, offer.SizeBytes - received);
                        var read = await ReadSomeAsync(ssl, buffer.AsMemory(0, wanted), cancellationToken,
                            offer.TransportPreference == TransferTransportPreference.DirectVpn ? DirectVpnNetworkIdleTimeout : null).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException("Передача оборвалась до получения файла целиком.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        ProgressChanged?.Invoke(new LocalTransferProgress(offer.TransferId, offer.FileName, TransferState.Transferring, received, offer.SizeBytes));
                    }
                    await destination.FlushAsync(cancellationToken);
                    destination.Flush(true);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма файла не совпала.");
            finalPath = MoveWithoutOverwrite(temporary, Settings.DestinationDirectory, offer.FileName);
            await FramedJson.WriteAsync(ssl, WireEnvelope.Create("complete", new WireReply(true)), cancellationToken);
            ProgressChanged?.Invoke(new LocalTransferProgress(offer.TransferId, offer.FileName, TransferState.Completed,
                offer.SizeBytes, offer.SizeBytes, finalPath));
            AddHistory(new TransferHistoryEntry(offer.TransferId, TransferDirection.Incoming, peer.Device.DisplayName,
                Path.GetFileName(finalPath), offer.SizeBytes, DateTimeOffset.UtcNow, TransferState.Completed));
        }
        catch (Exception ex)
        {
            TryDelete(temporary);
            ProgressChanged?.Invoke(new LocalTransferProgress(offer.TransferId, offer.FileName, TransferState.Failed, 0, offer.SizeBytes));
            try
            {
                await FramedJson.WriteAsync(ssl, WireEnvelope.Create("complete", new WireReply(false, "Не удалось сохранить и проверить файл.")), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception responseException) when (responseException is IOException or ObjectDisposedException or OperationCanceledException)
            {
                Console.Error.WriteLine($"[NOVA Transfer Engine] Не удалось отправить отказ о завершении передачи: {responseException}");
            }
            AddHistory(new TransferHistoryEntry(offer.TransferId, TransferDirection.Incoming, peer.Device.DisplayName,
                offer.FileName, offer.SizeBytes, DateTimeOffset.UtcNow, TransferState.Failed, ex.Message));
            throw;
        }
    }

    private static async Task<int> ReadSomeAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken,
        TimeSpan? idleTimeout = null)
    {
        if (idleTimeout is null) return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout.Value);
        try { return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Direct VPN file receive timed out while waiting for data.", ex); }
    }

    private static async Task WriteWithTimeoutAsync(Stream stream, ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken, TimeSpan idleTimeout)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try { await stream.WriteAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Direct VPN file send timed out while waiting for the peer.", ex); }
    }

    private void ValidateOffer(TransferOffer offer, string authenticatedSenderId)
    {
        if (offer.TransferId.Length is 0 or > 80 || offer.Sender.DeviceId != authenticatedSenderId ||
            offer.Recipient.DeviceId != Identity.Device.DeviceId || offer.SizeBytes < 0 ||
            !PeerTrustStore.IsValidFingerprint(Identity.Fingerprint) ||
            offer.Sha256.Length != 64 || !offer.Sha256.All(Uri.IsHexDigit) ||
            !Enum.IsDefined(typeof(TransferTransportPreference), offer.TransportPreference) ||
            offer.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(2) || offer.ExpiresAt < DateTimeOffset.UtcNow ||
            offer.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10))
            throw new InvalidDataException("Предложение передачи не прошло проверку.");
        _ = TransferFileSecurity.ValidateAndResolve(Settings.DestinationDirectory, offer.FileName);
    }

    private static string MoveWithoutOverwrite(string temporaryPath, string destinationDirectory, string fileName)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = TransferFileSecurity.MakeUniquePath(destinationDirectory, fileName);
            try { File.Move(temporaryPath, candidate, false); return candidate; }
            catch (IOException) when (File.Exists(candidate)) { }
        }
        throw new IOException("Не удалось подобрать свободное имя файла.");
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private bool IsTrustedClientCertificate(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        var trusted = TrustedPeers.Find(Fingerprint(certificate));
        return trusted is not null && DeviceIdentity.DeviceIdFromCertificate(certificate) == trusted.Device.DeviceId;
    }

    private static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    private void TransferProgressChanged(TransferOffer offer, TransferState state, long bytes) =>
        ProgressChanged?.Invoke(new LocalTransferProgress(offer.TransferId, offer.FileName, state, bytes, offer.SizeBytes));

    private void AddHistory(TransferHistoryEntry entry)
    {
        History.Add(entry);
        HistoryChanged?.Invoke(entry);
    }

    public void SetDestinationDirectoryPath(string path) => Settings.SetDestinationDirectory(path);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await _webRtcCoordinator.DisposeAsync();
        await _internet.DisposeAsync();
        _webRtcSignaling.Dispose();
        try { _listener.Stop(); } catch (SocketException) { }
        if (_discovery is not null) await _discovery.DisposeAsync();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
        Task[] handlers;
        lock (_handlerGate) handlers = _handlers.ToArray();
        try { await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        if (_ownsIdentity) Identity.Dispose();
        _connectionLimit.Dispose();
        _lifetime.Dispose();
    }
}

public sealed class IncomingOfferRequest
{
    private readonly TaskCompletionSource<bool> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IncomingOfferRequest(TransferOffer offer) => Offer = offer;
    public TransferOffer Offer { get; }
    public bool Respond(bool accept) => _response.TrySetResult(accept);

    internal async Task<bool> WaitForResponseAsync(CancellationToken cancellationToken)
    {
        try { return await _response.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken); }
        catch (TimeoutException) { return false; }
    }
}
