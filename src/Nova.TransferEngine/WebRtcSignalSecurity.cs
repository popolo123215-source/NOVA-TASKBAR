using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nova.RendezvousProtocol;

namespace Nova.TransferEngine;

internal static class WebRtcSignalSecurity
{
    public static DeviceSignalEnvelope Sign(DeviceIdentity identity, WebRtcSignal signal)
    {
        using var rsa = identity.Certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Device identity has no RSA signing key.");
        var unsigned = new DeviceSignalEnvelope(identity.Device.DeviceId, signal.SessionId, signal.Type, signal.Payload,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Guid.NewGuid().ToString("N"),
            Convert.ToBase64String(identity.Certificate.RawData), "");
        var signature = rsa.SignData(DeviceSignalCryptography.Transcript(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return unsigned with { Signature = Convert.ToBase64String(signature) };
    }

    public static WebRtcSignal VerifyAndConvert(DeviceSignalEnvelope signal, X509Certificate2 certificate, string expectedDeviceId, string expectedFingerprint)
    {
        if (!Guid.TryParseExact(signal.SessionId, "N", out _) || !Guid.TryParseExact(signal.Nonce, "N", out _) ||
            signal.SenderDeviceId != expectedDeviceId || DeviceSignalCryptography.Fingerprint(certificate) != expectedFingerprint ||
            !DeviceSignalCryptography.Verify(signal, certificate, DateTimeOffset.UtcNow))
            throw new CryptographicException("WebRTC signaling signature, identity, timestamp, or session is invalid.");
        return new WebRtcSignal(signal.SessionId, signal.Type, signal.Payload);
    }
}
