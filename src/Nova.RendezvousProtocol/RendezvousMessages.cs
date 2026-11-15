using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nova.RendezvousProtocol;

public sealed record RendezvousFrame(string Type, string? RequestId, JsonElement Payload)
{
    public static RendezvousFrame Create<T>(string type, T payload, string? requestId = null) =>
        new(type, requestId, JsonSerializer.SerializeToElement(payload, JsonOptions));

    public T ReadPayload<T>() => Payload.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException($"Empty payload for {Type}.");

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
}

public sealed record RendezvousHello(string DeviceId, string DisplayName, string CertificateDer);
public sealed record RendezvousChallenge(string Nonce);
public sealed record RendezvousContact(string DeviceId, string DisplayName, string Fingerprint);
public sealed record RendezvousRegister(string Signature, IReadOnlyList<RendezvousContact> Contacts);
public sealed record RendezvousDevice(string DeviceId, string DisplayName, string Fingerprint, bool Online);
public sealed record RendezvousDeviceList(IReadOnlyList<RendezvousDevice> Devices, DateTimeOffset GeneratedAt);
public sealed record RendezvousHeartbeat(IReadOnlyList<RendezvousContact> Contacts);
public sealed record RendezvousSignalRequest(string TargetDeviceId, DeviceSignalEnvelope Signal);
public sealed record RendezvousSignalDelivery(string MessageId, string SenderDeviceId, DeviceSignalEnvelope Signal);
public sealed record RendezvousSignalReceipt(string MessageId);
public sealed record RendezvousIceRequest;
public sealed record RendezvousIceCredentials(IReadOnlyList<string> StunUris, IReadOnlyList<string> TurnUris,
    string? TurnUsername, string? TurnPassword, DateTimeOffset? ExpiresAt);
public sealed record RendezvousError(string Code, string Message);

/// <summary>End-to-end signed signaling payload. The rendezvous server forwards it without modifying the signed fields.</summary>
public sealed record DeviceSignalEnvelope(string SenderDeviceId, string SessionId, string Type, JsonElement Payload,
    long IssuedAtUnixMs, string Nonce, string CertificateDer, string Signature);

public static class DeviceSignalCryptography
{
    public static byte[] Transcript(DeviceSignalEnvelope signal)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("senderDeviceId", signal.SenderDeviceId);
            writer.WriteString("sessionId", signal.SessionId);
            writer.WriteString("type", signal.Type);
            writer.WriteNumber("issuedAtUnixMs", signal.IssuedAtUnixMs);
            writer.WriteString("nonce", signal.Nonce);
            writer.WriteString("certificateDer", signal.CertificateDer);
            writer.WritePropertyName("payload");
            signal.Payload.WriteTo(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static bool Verify(DeviceSignalEnvelope signal, X509Certificate2 expectedCertificate, DateTimeOffset now)
    {
        if (signal.IssuedAtUnixMs < now.AddMinutes(-3).ToUnixTimeMilliseconds() ||
            signal.IssuedAtUnixMs > now.AddMinutes(1).ToUnixTimeMilliseconds() ||
            !string.Equals(signal.SenderDeviceId, DeviceIdFromCertificate(expectedCertificate), StringComparison.Ordinal) ||
            !string.Equals(Fingerprint(expectedCertificate), FingerprintFromDer(signal.CertificateDer), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var rsa = expectedCertificate.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(Transcript(signal), Convert.FromBase64String(signal.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    public static string? DeviceIdFromCertificate(X509Certificate2 certificate)
    {
        const string prefix = "NOVA TaskBar ";
        var commonName = certificate.GetNameInfo(X509NameType.SimpleName, false);
        var id = commonName.StartsWith(prefix, StringComparison.Ordinal) ? commonName[prefix.Length..] : null;
        return Guid.TryParseExact(id, "N", out _) ? id : null;
    }

    private static string FingerprintFromDer(string encoded)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded));
            return Fingerprint(certificate);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return string.Empty; }
    }
}
