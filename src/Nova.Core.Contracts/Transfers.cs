namespace Nova.Core.Contracts;

public enum TransferDirection { Incoming, Outgoing }
public enum TransferState { Offered, Accepted, Transferring, Paused, Completed, Rejected, Failed }
public sealed record DeviceRef(string DeviceId, string DisplayName, string Platform);
public sealed record TransferOffer(string TransferId, DeviceRef Sender, DeviceRef Recipient, string FileName, long SizeBytes, string? ContentType, string Sha256, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool CanResume);
public sealed record TransferProgress(string TransferId, TransferState State, long BytesTransferred, long TotalBytes, string? ErrorCode = null);
public sealed record TransferDecision(string TransferId, bool Accept, string? DestinationToken = null);
public sealed record TransferHistoryEntry(string TransferId, TransferDirection Direction, string DeviceName, string FileName, long SizeBytes, DateTimeOffset CompletedAt, TransferState State, string? ErrorMessage = null);

public interface ITransferClient
{
    Task<TransferOffer> CreateOfferAsync(DeviceRef recipient, string fileName, long sizeBytes, string? contentType, string sha256, CancellationToken cancellationToken);
    Task DecideAsync(TransferDecision decision, CancellationToken cancellationToken);
    IAsyncEnumerable<TransferProgress> WatchProgressAsync(string transferId, CancellationToken cancellationToken);
    Task ResumeAsync(string transferId, long offsetBytes, CancellationToken cancellationToken);
}
