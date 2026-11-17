namespace Nova.RendezvousProtocol;

public sealed record FileRelayTicketRequest(string TransferId, string PeerDeviceId);
public sealed record FileRelayTicket(string TransferId, string DeviceId, string PeerDeviceId, string Role,
    string Token, DateTimeOffset ExpiresAt);
public sealed record FileRelayHello(string Token, string DeviceId);
public sealed record FileRelayChallenge(string Nonce, string TransferId, string PeerDeviceId, string Role);
public sealed record FileRelayProof(string Signature);
public sealed record FileRelayReady(string TransferId);
