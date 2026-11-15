using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Nova.TransferEngine;

public sealed class LanDiscoveryService : IAsyncDisposable
{
    public const int DiscoveryPort = 45984;
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.42.99");
    private readonly DeviceIdentity _identity;
    private readonly int _tcpPort;
    private readonly Func<string, bool> _isTrusted;
    private readonly Dictionary<string, DiscoveredDevice> _devices = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private UdpClient? _udp;

    public LanDiscoveryService(DeviceIdentity identity, int tcpPort, Func<string, bool> isTrusted)
    {
        _identity = identity;
        _tcpPort = tcpPort;
        _isTrusted = isTrusted;
    }

    public void Start()
    {
        if (_cancellation is not null) return;
        _cancellation = new CancellationTokenSource();
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udp.JoinMulticastGroup(MulticastAddress);
            _udp.MulticastLoopback = true;
            _ = ReceiveLoopAsync(_cancellation.Token);
            _ = AnnounceLoopAsync(_cancellation.Token);
        }
        catch
        {
            _cancellation.Dispose();
            _cancellation = null;
            _udp?.Dispose();
            _udp = null;
            throw;
        }
    }

    public IReadOnlyList<DiscoveredDevice> Snapshot()
    {
        lock (_gate)
        {
            PruneExpired();
            return _devices.Values.Select(x => x with { IsTrusted = _isTrusted(x.Fingerprint) })
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        var announcement = new DeviceAnnouncement(1, _identity.Device.DeviceId, _identity.Device.DisplayName, _tcpPort, _identity.Fingerprint, DateTimeOffset.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(announcement);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _udp!.SendAsync(bytes, new IPEndPoint(MulticastAddress, DiscoveryPort), token);
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(TimeSpan.FromSeconds(2), token); }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var datagram = await _udp!.ReceiveAsync(token);
                if (!TryParseAnnouncement(datagram.Buffer, datagram.RemoteEndPoint.Address, out var device) || device is null) continue;
                if (device.DeviceId == _identity.Device.DeviceId || device.Fingerprint == _identity.Fingerprint || device.Address.IsIPv6LinkLocal) continue;
                lock (_gate) _devices[device.DeviceId] = device;
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (token.IsCancellationRequested) break; }
            catch (JsonException) { }
        }
    }

    public static bool TryParseAnnouncement(ReadOnlySpan<byte> payload, IPAddress address, out DiscoveredDevice? device)
    {
        device = null;
        if (payload.Length is 0 or > 2048) return false;
        try
        {
            var announcement = JsonSerializer.Deserialize<DeviceAnnouncement>(payload);
            if (announcement is null || announcement.Version != 1 || string.IsNullOrWhiteSpace(announcement.DeviceId) ||
                string.IsNullOrWhiteSpace(announcement.DisplayName) || announcement.DisplayName.Length > 80 ||
                announcement.Port is < 1 or > 65535 || !PeerTrustStore.IsValidFingerprint(announcement.Fingerprint) ||
                announcement.SentAt < DateTimeOffset.UtcNow.AddMinutes(-2) || announcement.SentAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return false;
            device = new DiscoveredDevice(announcement.DeviceId, announcement.DisplayName, address, announcement.Port,
                announcement.Fingerprint.ToUpperInvariant(), DateTimeOffset.UtcNow, false);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private void PruneExpired()
    {
        var expiry = DateTimeOffset.UtcNow.AddSeconds(-8);
        foreach (var id in _devices.Where(x => x.Value.LastSeen < expiry).Select(x => x.Key).ToArray())
            _devices.Remove(id);
    }

    public ValueTask DisposeAsync()
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _udp?.Dispose();
            _cancellation.Dispose();
            _cancellation = null;
            _udp = null;
        }
        return ValueTask.CompletedTask;
    }
}
