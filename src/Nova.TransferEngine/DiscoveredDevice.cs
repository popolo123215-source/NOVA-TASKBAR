using System.Net;
using Nova.Core.Contracts;

namespace Nova.TransferEngine;

[Flags]
public enum DeviceDiscoverySource { None = 0, Lan = 1, Internet = 2 }

public sealed record DiscoveredDevice(string DeviceId, string DisplayName, IPAddress Address, int Port, string Fingerprint, DateTimeOffset LastSeen, bool IsTrusted,
    DeviceDiscoverySource Source = DeviceDiscoverySource.Lan, bool IsOnline = true, bool IsInternetOnline = false)
{
    public bool IsAvailableOnLan => (Source & DeviceDiscoverySource.Lan) != 0;
    public bool IsAvailableOnInternet => (Source & DeviceDiscoverySource.Internet) != 0 && IsInternetOnline;
    public string AvailabilityText
    {
        get
        {
            var availability = $"{(IsAvailableOnLan ? $"LAN · {(IsOnline ? "доступно" : "не в сети")}" : "")}{(IsAvailableOnLan && (Source & DeviceDiscoverySource.Internet) != 0 ? " · " : "")}{((Source & DeviceDiscoverySource.Internet) != 0 ? $"Internet · {(IsInternetOnline ? "доступно" : "не в сети")}" : "")}";
            return availability.Length == 0 ? "Сохранённый контакт · адрес вводится вручную" : availability;
        }
    }
    public string ShortFingerprint => Fingerprint.Length >= 12
        ? string.Join("-", Enumerable.Range(0, 3).Select(i => Fingerprint.Substring(i * 4, 4)))
        : Fingerprint;
}

internal sealed record DeviceAnnouncement(int Version, string DeviceId, string DisplayName, int Port, string Fingerprint, DateTimeOffset SentAt);

public sealed record LocalTransferProgress(string TransferId, string FileName, TransferState State, long BytesTransferred, long TotalBytes);
