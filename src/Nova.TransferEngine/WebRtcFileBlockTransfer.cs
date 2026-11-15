using System.Security.Cryptography;
using Nova.TransferProtocol;

namespace Nova.TransferEngine;

/// <summary>Bounded streaming file-block primitives shared by DataChannel session coordinators.</summary>
public static class WebRtcFileBlockTransfer
{
    public const int MaxBlockPayloadSize = TransferProtocolCodec.MaximumDataBlockLength;
    // Target 16 KiB total DataChannel message while respecting its 36-byte NOVA header.
    public const int BlockPayloadSize = 16 * 1024 - TransferProtocolCodec.DataBlockHeaderLength;

    /// <summary>Reads and sends one block at a time. The next source read waits for a durable ACK.</summary>
    public static async Task SendAsync(Stream source, Guid transferId, long expectedSize, string expectedSha256,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendFrameAsync,
        Func<long, CancellationToken, ValueTask> waitForDurableAckAsync,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sendFrameAsync);
        ArgumentNullException.ThrowIfNull(waitForDurableAckAsync);
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("Source must be readable and seekable.", nameof(source));
        if (transferId == Guid.Empty || expectedSize < 0 || source.Position < 0 || source.Position > source.Length || source.Length - source.Position != expectedSize)
            throw new InvalidDataException("Source length does not match the accepted offer.");
        ValidateHash(expectedSha256);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[MaxBlockPayloadSize];
        long offset = 0;
        while (offset < expectedSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = (int)Math.Min(BlockPayloadSize, expectedSize - offset);
            var read = 0;
            while (read < wanted)
            {
                var count = await source.ReadAsync(buffer.AsMemory(read, wanted - read), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Source file changed or ended during transfer.");
                read += count;
            }
            hash.AppendData(buffer, 0, read);
            var frame = TransferProtocolCodec.EncodeDataBlock(transferId, offset, buffer.AsMemory(0, read));
            await sendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            offset += read;
            await waitForDurableAckAsync(offset, cancellationToken).ConfigureAwait(false);
            progress?.Report(offset);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source file contents changed after the offer was created.");
        if (source.Position != source.Length) throw new IOException("Source file length changed during transfer.");
    }

    /// <summary>Validates and flushes each block before acknowledging it, then atomically publishes the file.</summary>
    public static async Task<string> ReceiveAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> frames, Guid transferId,
        string destinationDirectory, string fileName, long expectedSize, string expectedSha256,
        Func<long, CancellationToken, ValueTask> acknowledgeDurableAsync,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(acknowledgeDurableAsync);
        if (transferId == Guid.Empty || expectedSize < 0)
            throw new InvalidDataException("Offered file size or transfer identifier is invalid.");
        ValidateHash(expectedSha256);
        Directory.CreateDirectory(destinationDirectory);
        EnsureAvailableSpace(destinationDirectory, expectedSize);
        var finalPath = TransferFileSecurity.MakeUniquePath(destinationDirectory, fileName);
        var partialPath = Path.Combine(destinationDirectory, $".{Guid.NewGuid():N}.nova-partial");
        var offset = 0L;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (expectedSize > 0) await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    var block = TransferProtocolCodec.DecodeDataBlock(frame);
                    if (block.TransferId != transferId || block.OffsetBytes != offset || block.Payload.Length > expectedSize - offset)
                        throw new InvalidDataException("File block has the wrong transfer id, offset, or length.");
                    await output.WriteAsync(block.Payload, cancellationToken).ConfigureAwait(false);
                    hash.AppendData(block.Payload.Span);
                    offset += block.Payload.Length;
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    await acknowledgeDurableAsync(offset, cancellationToken).ConfigureAwait(false);
                    progress?.Report(offset);
                    if (offset == expectedSize) break;
                }
                if (offset != expectedSize) throw new EndOfStreamException("DataChannel ended before the offered file was complete.");
                var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Received file SHA-256 does not match the offer.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Move within the destination volume and never overwrite an existing file.
            finalPath = TransferFileSecurity.MakeUniquePath(destinationDirectory, fileName);
            File.Move(partialPath, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            try { File.Delete(partialPath); } catch (IOException) { }
            throw;
        }
    }

    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Expected SHA-256 must be 64 hexadecimal characters.");
    }

    public static async Task<string> ComputeSha256Async(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Stream must be readable.", nameof(source));
        return Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
    }

    private static void EnsureAvailableSpace(string destinationDirectory, long expectedSize)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
        if (string.IsNullOrEmpty(root)) throw new IOException("Cannot determine the destination volume.");
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < 0 || expectedSize > available)
            throw new IOException($"Insufficient available space: need {expectedSize} bytes, available {available} bytes.");
    }
}
