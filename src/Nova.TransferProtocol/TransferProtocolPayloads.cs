namespace Nova.TransferProtocol;

public sealed record TransferResumePayload(long OffsetBytes);
public sealed record TransferBlockAcknowledgementPayload(long DurableOffsetBytes);
public sealed record TransferCompletePayload(string Sha256);
public sealed record TransferFailurePayload(string Code, string? Message = null);
public sealed record TransferCancelPayload(string Reason = "cancelled");

// Shape only: the signature transcript and verification policy are defined by the
// device-authentication component before this payload is accepted by a session.
public sealed record TransferDeviceProofPayload(
    string DeviceId,
    string ChallengeNonce,
    string PeerNonce,
    string DeviceKeyFingerprint,
    string LocalDtlsFingerprint,
    string RemoteDtlsFingerprint,
    string SignatureAlgorithm,
    string Signature);
