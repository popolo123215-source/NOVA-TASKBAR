using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Nova.Core.Contracts;
using Nova.TransferEngine;
using Nova.TransferProtocol;

internal static class WebRtcIntegration
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const int LifecycleCycles = 25;

    public static async Task RunAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        for (var cycle = 1; cycle <= LifecycleCycles; cycle++)
        {
            await RunCycleAsync(cycle);
            if (cycle % 5 == 0) Console.WriteLine($"      lifecycle cycles: {cycle}/{LifecycleCycles}");
        }
        Console.WriteLine($"      {LifecycleCycles} sequential native peer lifecycles completed in {stopwatch.Elapsed.TotalSeconds:F2}s");
    }

    private static async Task RunCycleAsync(int cycle)
    {
        await using var a = WebRtcPeer.Create([]);
        await using var b = WebRtcPeer.Create([]);
        using var stop = new CancellationTokenSource(Timeout);
        var aOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aIceConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bIceConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aCandidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCandidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aChannelClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bChannelClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var durableAcks = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var offerDecisions = new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
        var failures = new ConcurrentQueue<Exception>();
        a.CreateReliableChannel($"nova-transfer-{cycle}");
        var pumpA = PumpAsync(a, b, aOpened, aConnected, aIceConnected, aCandidate, aChannelClosed, aReceived, nativeFrames, durableAcks, offerDecisions, failures, stop.Token);
        var pumpB = PumpAsync(b, a, bOpened, bConnected, bIceConnected, bCandidate, bChannelClosed, bReceived, nativeFrames, durableAcks, offerDecisions, failures, stop.Token);

        try
        {
            a.CreateOffer();
            await Task.WhenAll(aOpened.Task, bOpened.Task, aConnected.Task, bConnected.Task, aIceConnected.Task, bIceConnected.Task, aCandidate.Task, bCandidate.Task).WaitAsync(Timeout);
            if (failures.TryDequeue(out var failure)) throw new InvalidOperationException("Signaling callback failed.", failure);
            Assert(a.GetSelectedCandidatePair() is { IsRelay: false } && b.GetSelectedCandidatePair() is { IsRelay: false },
                "Local WebRTC integration did not select a direct candidate pair.");

            var payloadA = CreatePayload(16 * 1024 + 257, 0x51);
            await a.SendAsync(payloadA, stop.Token);
            var receivedB = await bReceived.Task.WaitAsync(Timeout);
            AssertPayload(payloadA, receivedB, "A -> B");
            Assert(payloadA.Length >= 16 * 1024, "Test payload must be at least 16 KiB.");

            var payloadB = CreatePayload(16 * 1024 + 513, 0xA7);
            await b.SendAsync(payloadB, stop.Token);
            var receivedA = await aReceived.Task.WaitAsync(Timeout);
            AssertPayload(payloadB, receivedA, "B -> A");
            Assert(payloadB.Length >= 16 * 1024, "Test payload must be at least 16 KiB.");
            await Task.Delay(250, stop.Token);
            if (failures.TryDequeue(out var duplicate)) throw new InvalidOperationException("DataChannel callback reported duplicate or invalid event.", duplicate);
            if (cycle == 1)
            {
                await TransferFileOverNativeAsync(a, b, nativeFrames.Reader, durableAcks.Reader, stop.Token,
                    "small.bin", 37, cycle, offerDecisions);
                await TransferFileOverNativeAsync(a, b, nativeFrames.Reader, durableAcks.Reader, stop.Token,
                    "large.bin", 5 * 1024 * 1024 + 173, cycle, offerDecisions);
            }
            a.CloseDataChannel();
            a.CloseDataChannel();
            await Task.WhenAll(aChannelClosed.Task, bChannelClosed.Task).WaitAsync(Timeout);
            try { await a.SendAsync(new byte[] { 1 }, stop.Token); throw new InvalidOperationException("Send succeeded after DataChannel closure."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("DataChannel has not been created", StringComparison.Ordinal)) { }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(pumpA, pumpB).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { throw new InvalidOperationException("WebRTC event pumps did not stop within the timeout."); }
        }
        // SafeHandle disposal must be idempotent; the await-using scope disposes each peer once more.
        await a.DisposeAsync();
        await b.DisposeAsync();
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    private static async Task PumpAsync(WebRtcPeer local, WebRtcPeer remote,
        TaskCompletionSource opened, TaskCompletionSource connected, TaskCompletionSource iceConnected,
        TaskCompletionSource candidateSeen,
        TaskCompletionSource channelClosed,
        TaskCompletionSource<byte[]> received,
        Channel<byte[]> nativeFrames, Channel<long> durableAcks,
        ConcurrentDictionary<string, TaskCompletionSource<bool>> offerDecisions,
        ConcurrentQueue<Exception> failures, CancellationToken cancellationToken)
    {
        var remoteDescriptionSet = false;
        var pendingCandidates = new Queue<(string Candidate, string? Mid)>();
        try
        {
            await foreach (var ev in local.Events(cancellationToken))
            {
                switch (ev.Kind)
                {
                    case 1: // PeerConnection state
                        if (ev.Data.Length == sizeof(int))
                        {
                            var state = BitConverter.ToInt32(ev.Data);
                            if (state == 4) throw new IOException("PeerConnection entered failed state.");
                            if (state == 2) connected.TrySetResult();
                        }
                        break;
                    case 2: // gathered local description
                        var sdp = System.Text.Encoding.UTF8.GetString(ev.Data);
                        if (string.IsNullOrWhiteSpace(sdp) || string.IsNullOrWhiteSpace(ev.Detail))
                            throw new InvalidDataException("Native local description callback was empty.");
                        remote.SetRemoteDescription(sdp, ev.Detail);
                        remoteDescriptionSet = true;
                        while (pendingCandidates.TryDequeue(out var candidate))
                            remote.AddIceCandidate(candidate.Candidate, candidate.Mid);
                        if (ev.Detail == "offer") remote.CreateAnswer();
                        break;
                    case 3: // trickle ICE candidate
                        var candidateText = System.Text.Encoding.UTF8.GetString(ev.Data);
                        if (!string.IsNullOrWhiteSpace(candidateText)) candidateSeen.TrySetResult();
                        if (remoteDescriptionSet) remote.AddIceCandidate(candidateText, ev.Detail);
                        else pendingCandidates.Enqueue((candidateText, ev.Detail));
                        break;
                    case 8: // ICE state
                        if (ev.Data.Length == sizeof(int) && BitConverter.ToInt32(ev.Data) is 2 or 3)
                            iceConnected.TrySetResult();
                        break;
                    case 4: // DataChannel open
                        opened.TrySetResult();
                        break;
                    case 5: // binary DataChannel message
                        if (ev.Data.AsSpan().StartsWith("NVAF"u8))
                            await nativeFrames.Writer.WriteAsync(ev.Data, cancellationToken);
                        else if (ev.Data.Length > 0 && ev.Data[0] == (byte)'{')
                        {
                            var control = TransferProtocolCodec.DecodeControl<System.Text.Json.JsonElement>(ev.Data);
                            if (control.Type == TransferControlType.BlockAcknowledgement)
                            {
                                var ack = control.Payload.Deserialize<TransferBlockAcknowledgementPayload>(WireJson) ?? throw new InvalidDataException("Empty ACK.");
                                await durableAcks.Writer.WriteAsync(ack.DurableOffsetBytes, cancellationToken);
                            }
                            else if (control.Type == TransferControlType.Offer)
                            {
                                var offer = control.Payload.Deserialize<TransferOffer>(WireJson) ?? throw new InvalidDataException("Empty file offer.");
                                if (offer.TransferId != control.TransferId) throw new InvalidDataException("Offer transfer id mismatch.");
                                await local.SendControlAsync(TransferControlType.Decision, offer.TransferId,
                                    new TransferDecision(offer.TransferId, true), cancellationToken);
                            }
                            else if (control.Type == TransferControlType.Decision)
                            {
                                var decision = control.Payload.Deserialize<TransferDecision>(WireJson) ?? throw new InvalidDataException("Empty offer decision.");
                                if (offerDecisions.TryGetValue(control.TransferId, out var completion)) completion.TrySetResult(decision.Accept);
                            }
                            else throw new InvalidDataException($"Unexpected transfer control: {control.Type}.");
                        }
                        else if (!received.TrySetResult(ev.Data)) throw new InvalidDataException("Unexpected duplicate raw DataChannel message.");
                        break;
                    case 6: // DataChannel closed
                        channelClosed.TrySetResult();
                        break;
                    case 7: // incoming DataChannel registered
                        if (ev.Data.Length != sizeof(int)) throw new InvalidDataException("Incoming DataChannel callback omitted its channel id.");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failures.Enqueue(ex);
            opened.TrySetException(ex);
            connected.TrySetException(ex);
            candidateSeen.TrySetException(ex);
            channelClosed.TrySetException(ex);
            received.TrySetException(ex);
            foreach (var pending in offerDecisions.Values) pending.TrySetException(ex);
        }
    }

    private static async Task TransferFileOverNativeAsync(WebRtcPeer sender, WebRtcPeer receiver,
        ChannelReader<byte[]> frameReader, ChannelReader<long> ackReader, CancellationToken cancellationToken,
        string name, int length, int cycle,
        ConcurrentDictionary<string, TaskCompletionSource<bool>> offerDecisions)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nova-native-file-{cycle}-{Guid.NewGuid():N}");
        var destination = Path.Combine(root, "received");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, name);
        var contents = CreatePayload(length, length ^ cycle);
        try
        {
            await File.WriteAllBytesAsync(source, contents, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(contents));
            var id = Guid.NewGuid();
            var transferId = id.ToString("N");
            var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!offerDecisions.TryAdd(transferId, accepted)) throw new InvalidOperationException("Duplicate test transfer id.");
            var offer = new TransferOffer(transferId, new DeviceRef("peer-a", "Peer A", "Windows"),
                new DeviceRef("peer-b", "Peer B", "Windows"), name, length, "application/octet-stream", hash,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false);
            await sender.SendControlAsync(TransferControlType.Offer, transferId, offer, cancellationToken);
            try { Assert(await accepted.Task.WaitAsync(Timeout, cancellationToken), "Receiver rejected file offer."); }
            catch (Exception ex) { if (offerDecisions.ContainsKey(transferId)) throw new InvalidOperationException($"Receiver did not accept offer {transferId}.", ex); throw; }
            offerDecisions.TryRemove(transferId, out _);
            var receive = WebRtcFileBlockTransfer.ReceiveAsync(ReadFrames(frameReader, cancellationToken), id,
                destination, name, length, hash,
                (offset, token) => receiver.SendControlAsync(TransferControlType.BlockAcknowledgement,
                    id.ToString("N"), new TransferBlockAcknowledgementPayload(offset), token),
                cancellationToken: cancellationToken);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await WebRtcFileBlockTransfer.SendAsync(input, id, length, hash,
                (frame, token) => sender.SendAsync(frame, token),
                async (offset, token) => Assert(await ackReader.ReadAsync(token) == offset, "Native durable ACK offset mismatch."),
                cancellationToken: cancellationToken).WaitAsync(Timeout);
            var receivedPath = await receive.WaitAsync(Timeout);
            await using var receivedStream = File.OpenRead(receivedPath);
            var receivedHash = Convert.ToHexString(await SHA256.HashDataAsync(receivedStream, cancellationToken));
            Assert(new FileInfo(receivedPath).Length == length && receivedHash == hash, $"Native file verification failed for {name}.");
            Console.WriteLine($"      native file {name}: {length} bytes, SHA-256 verified");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFrames(ChannelReader<byte[]> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
            while (reader.TryRead(out var frame)) yield return frame;
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        var bytes = new byte[length];
        var state = unchecked((uint)seed);
        for (var i = 0; i < bytes.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }
        return bytes;
    }

    private static void AssertPayload(byte[] expected, byte[] actual, string direction)
    {
        if (actual.Length != expected.Length || !actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException($"Payload mismatch {direction}: expected {expected.Length} bytes, received {actual.Length}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
