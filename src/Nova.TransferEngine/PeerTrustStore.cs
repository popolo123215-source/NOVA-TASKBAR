using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Nova.Core.Contracts;

namespace Nova.TransferEngine;

public sealed class PeerTrustStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, TrustedDevice> _devices;

    public PeerTrustStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "trusted-devices.json");
        try
        {
            _devices = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, TrustedDevice>>(File.ReadAllText(_path), JsonOptions) ?? new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { _devices = new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException) { _devices = new(StringComparer.OrdinalIgnoreCase); }
    }

    private static JsonSerializerOptions JsonOptions => new() { WriteIndented = true };

    public IReadOnlyList<TrustedDevice> GetAll()
    {
        lock (_gate) return _devices.Values.OrderBy(x => x.Device.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public bool IsTrusted(string fingerprint)
    {
        lock (_gate) return _devices.ContainsKey(Normalize(fingerprint));
    }

    public bool IsTrusted(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        return IsTrusted(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.GetRawCertData())));
    }

    public TrustedDevice? Find(string fingerprint)
    {
        lock (_gate) return _devices.TryGetValue(Normalize(fingerprint), out var device) ? device : null;
    }

    public void Trust(DiscoveredDevice device)
    {
        if (!IsValidFingerprint(device.Fingerprint)) throw new ArgumentException("Отпечаток сертификата некорректен.", nameof(device));
        lock (_gate)
        {
            var fingerprint = Normalize(device.Fingerprint);
            if (_devices.Values.Any(existing => existing.Device.DeviceId == device.DeviceId && existing.Fingerprint != fingerprint))
                throw new InvalidOperationException("This device ID is already trusted with a different certificate. Remove the previous identity before replacing it.");
            _devices[fingerprint] = new TrustedDevice(new DeviceRef(device.DeviceId, device.DisplayName, "windows"), fingerprint, DateTimeOffset.UtcNow);
            Save();
        }
    }

    public bool Remove(string fingerprint)
    {
        lock (_gate)
        {
            var removed = _devices.Remove(Normalize(fingerprint));
            if (removed) Save();
            return removed;
        }
    }

    public static bool IsValidFingerprint(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Normalize(string value) => value.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_devices, JsonOptions));
        File.Move(temporary, _path, true);
    }
}

public sealed record TrustedDevice(DeviceRef Device, string Fingerprint, DateTimeOffset TrustedAt);
