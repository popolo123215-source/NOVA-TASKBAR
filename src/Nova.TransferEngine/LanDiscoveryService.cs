using System.Net;
using System.Net.NetworkInformation;
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
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private UdpClient? _udp;
    private Task? _receiveLoop;
    private Task? _announceLoop;

    public LanDiscoveryService(DeviceIdentity identity, int tcpPort, Func<string, bool> isTrusted)
    {
        _identity = identity;
        _tcpPort = tcpPort;
        _isTrusted = isTrusted;
    }

    public void Start()
    {
        _lifecycleGate.Wait();
        try { StartCore(); }
        finally { _lifecycleGate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            lock (_gate) _devices.Clear();
            StartCore();
        }
        finally { _lifecycleGate.Release(); }
    }

    private void StartCore()
    {
        if (_cancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            udp.JoinMulticastGroup(MulticastAddress);
            udp.MulticastLoopback = true;
            _udp = udp;
            _cancellation = cancellation;
            _receiveLoop = ReceiveLoopAsync(udp, cancellation.Token);
            _announceLoop = AnnounceLoopAsync(udp, cancellation.Token);
        }
        catch
        {
            udp?.Dispose();
            cancellation.Dispose();
            _udp = null;
            _cancellation = null;
            throw;
        }
    }

    private async Task StopCoreAsync()
    {
        var cancellation = _cancellation;
        if (cancellation is null) return;
        _cancellation = null;
        var udp = _udp;
        _udp = null;
        var receiveLoop = _receiveLoop;
        var announceLoop = _announceLoop;
        _receiveLoop = null;
        _announceLoop = null;
        cancellation.Cancel();
        udp?.Dispose();
        try
        {
            var loops = new[] { receiveLoop, announceLoop }.Where(task => task is not null).Cast<Task>().ToArray();
            if (loops.Length > 0) await Task.WhenAll(loops).ConfigureAwait(false);
        }
        finally { cancellation.Dispose(); }
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

    private async Task AnnounceLoopAsync(UdpClient udp, CancellationToken token)
    {
        var announcement = new DeviceAnnouncement(1, _identity.Device.DeviceId, _identity.Device.DisplayName, _tcpPort, _identity.Fingerprint, DateTimeOffset.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(announcement);
        while (!token.IsCancellationRequested)
        {
            try
            {
                try
                {
                    await udp.SendAsync(bytes, new IPEndPoint(MulticastAddress, DiscoveryPort), token);
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine($"[NOVA Transfer Engine] Multicast discovery announce failed: {ex.Message}");
                }
                foreach (var address in GetDirectedBroadcastAddresses())
                {
                    try
                    {
                        await udp.SendAsync(bytes, new IPEndPoint(address, DiscoveryPort), token);
                    }
                    catch (SocketException ex)
                    {
                        Console.Error.WriteLine($"[NOVA Transfer Engine] Broadcast discovery announce failed for {address}: {ex.Message}");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await WaitBeforeRetryAsync(token).ConfigureAwait(false); }
            catch (NetworkInformationException ex)
            {
                Console.Error.WriteLine($"[NOVA Transfer Engine] Could not read network interfaces for discovery: {ex.Message}");
                await WaitBeforeRetryAsync(token).ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitBeforeRetryAsync(CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static IReadOnlyCollection<IPAddress> GetDirectedBroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var broadcast = GetDirectedBroadcastAddress(unicast.Address, unicast.IPv4Mask);
                if (broadcast is not null) addresses.Add(broadcast);
            }
        }
        return addresses;
    }

    public static IPAddress? GetDirectedBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            subnetMask.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var addressBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        var prefixLength = 0;
        var hostBitsStarted = false;
        for (var index = 0; index < maskBytes.Length; index++)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isNetworkBit = (maskBytes[index] & (1 << bit)) != 0;
                if (isNetworkBit && hostBitsStarted) return null;
                if (isNetworkBit) prefixLength++;
                else hostBitsStarted = true;
            }
        }
        if (prefixLength is 0 or >= 31) return null;

        var broadcastBytes = new byte[addressBytes.Length];
        for (var index = 0; index < broadcastBytes.Length; index++)
            broadcastBytes[index] = (byte)(addressBytes[index] | ~maskBytes[index]);
        return new IPAddress(broadcastBytes);
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var datagram = await udp.ReceiveAsync(token);
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

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }
}
