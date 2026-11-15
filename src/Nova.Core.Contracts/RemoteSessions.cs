namespace Nova.Core.Contracts;

public sealed record RemoteSessionRequest(string DeviceId, string RequestedScopes, string ClientNonce);
public sealed record RemoteSessionConsent(string SessionId, string OwnerDeviceId, string GrantedScopes, DateTimeOffset ExpiresAt);
public sealed record RemoteSessionEnded(string SessionId, string EndedByDeviceId, DateTimeOffset EndedAt, string Reason);

/// <summary>Metadata contract only; remote input requires explicit owner-controlled consent.</summary>
public interface IRemoteSessionClient
{
    Task<RemoteSessionConsent> RequestConsentAsync(RemoteSessionRequest request, CancellationToken cancellationToken);
    Task EndSessionAsync(string sessionId, CancellationToken cancellationToken);
}
