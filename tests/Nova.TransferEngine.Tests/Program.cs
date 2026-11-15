using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nova.Core.Contracts;
using Nova.TransferEngine;
using Nova.TransferProtocol;

var passed = 0;
var failed = 0;

await Run("framed protocol round trip with partial reads", TestFramedJsonAsync);
RunSync("oversized protocol frame is rejected", TestOversizedFrame);
RunSync("path traversal and rooted paths are rejected", TestPathSafety);
RunSync("discovery payload is validated", TestDiscoveryPayload);
RunSync("one Device ID cannot be trusted with two certificate identities", TestDuplicateDeviceIdentityTrust);
await Run("authenticated streaming transfer, consent and persisted history", TestEndToEndAsync);
await Run("unknown client certificate is rejected", TestUnknownPeerAsync);
RunSync("internet protocol control message round trip and bounds", TestTransferControlProtocol);
RunSync("unsupported internet protocol version is rejected", TestUnsupportedTransferVersion);
RunSync("internet data block round trip and canonical UUID bytes", TestTransferDataBlock);
RunSync("malformed and oversized internet data blocks are rejected", TestInvalidTransferDataBlock);
RunSync("internet data block uses signed 64-bit offsets beyond 20 GiB", TestLargeTransferOffset);
RunSync("internet transfer cancellation control round trip", TestTransferCancellation);
await Run("bounded streaming file blocks, durable acknowledgements and atomic receive", TestStreamingFileBlocksAsync);
await Run("mutually authenticated signaling delivery between independent transfer services", TestSignalingRouteAsync);
await Run("real WSS rendezvous, trusted presence, signed signaling and temporary TURN credentials", TestInternetRendezvousAsync);
await Run("native-backed WebRTC file transfer through production signaling and coordinator", TestWebRtcCoordinatorAsync);
await Run("native WebRTC offer/answer ICE and bidirectional DataChannel payload", WebRtcIntegration.RunAsync);

static void TestTransferControlProtocol()
{
    var transferId = Guid.NewGuid().ToString("N");
    var decision = new TransferDecision(transferId, true, "destination-token");
    var bytes = TransferProtocolCodec.EncodeControl(TransferControlType.Decision, transferId, decision);
    var decoded = TransferProtocolCodec.DecodeControl<TransferDecision>(bytes);
    Assert(decoded.ProtocolVersion == TransferProtocolCodec.CurrentVersion && decoded.Type == TransferControlType.Decision &&
        decoded.TransferId == transferId && decoded.Payload == decision, "Управляющее сообщение не прошло обратное преобразование.");
    Assert(bytes.Length <= TransferProtocolCodec.MaximumMessageLength, "Управляющее сообщение превысило лимит.");

    Expect<InvalidDataException>(() =>
        TransferProtocolCodec.EncodeControl(TransferControlType.Failure, transferId, new string('x', TransferProtocolCodec.MaximumMessageLength)));
    Expect<InvalidDataException>(() =>
        TransferProtocolCodec.EncodeControl(TransferControlType.Offer, transferId.ToUpperInvariant(), new { Name = "bad-id" }));
}

static void TestTransferCancellation()
{
    var id = Guid.NewGuid().ToString("N");
    var wire = TransferProtocolCodec.EncodeControl(TransferControlType.Cancel, id, new TransferCancelPayload("user-requested"));
    var decoded = TransferProtocolCodec.DecodeControl<TransferCancelPayload>(wire);
    Assert(decoded.Type == TransferControlType.Cancel && decoded.TransferId == id && decoded.Payload.Reason == "user-requested",
        "Cancellation control message did not round-trip.");
}

static async Task TestStreamingFileBlocksAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-block-stream-" + Guid.NewGuid().ToString("N"));
    var sourcePath = Path.Combine(root, "source.bin");
    var destination = Path.Combine(root, "received");
    Directory.CreateDirectory(root);
    try
    {
        var contents = new byte[5 * 1024 * 1024 + 173];
        RandomNumberGenerator.Fill(contents);
        await File.WriteAllBytesAsync(sourcePath, contents);
        var sha = Convert.ToHexString(SHA256.HashData(contents));
        var id = Guid.NewGuid();
        var frames = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
        var acks = System.Threading.Channels.Channel.CreateBounded<long>(1);
        var sender = Task.Run(async () =>
        {
            try
            {
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await WebRtcFileBlockTransfer.SendAsync(input, id, contents.Length, sha,
                    (frame, token) => frames.Writer.WriteAsync(frame, token),
                    async (offset, token) => Assert(await acks.Reader.ReadAsync(token) == offset, "Durable block ACK offset mismatch."));
            }
            finally { frames.Writer.TryComplete(); }
        });
        using var transferTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var receivedPath = await WebRtcFileBlockTransfer.ReceiveAsync(frames.Reader.ReadAllAsync(), id, destination,
            "received.bin", contents.Length, sha,
            (offset, token) => acks.Writer.WriteAsync(offset, token), cancellationToken: transferTimeout.Token);
        await sender.WaitAsync(TimeSpan.FromSeconds(20));
        Assert((await File.ReadAllBytesAsync(receivedPath)).AsSpan().SequenceEqual(contents), "Streamed destination bytes differ from source.");
        Assert(Directory.GetFiles(destination, "*.nova-partial").Length == 0, "A temporary partial file remained after successful transfer.");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

static async Task TestSignalingRouteAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-signal-route-" + Guid.NewGuid().ToString("N"));
    using var identityA = DeviceIdentity.CreateEphemeral("Signaling A");
    using var identityB = DeviceIdentity.CreateEphemeral("Signaling B");
    await using var a = new LocalTransferService(Path.Combine(root, "a"), identity: identityA);
    await using var b = new LocalTransferService(Path.Combine(root, "b"), identity: identityB);
    a.Start(enableDiscovery: false);
    b.Start(enableDiscovery: false);
    var deviceA = new DiscoveredDevice(identityA.Device.DeviceId, identityA.Device.DisplayName, IPAddress.Loopback, a.TcpPort, identityA.Fingerprint, DateTimeOffset.UtcNow, true);
    var deviceB = new DiscoveredDevice(identityB.Device.DeviceId, identityB.Device.DisplayName, IPAddress.Loopback, b.TcpPort, identityB.Fingerprint, DateTimeOffset.UtcNow, true);
    a.TrustDevice(deviceB);
    b.TrustDevice(deviceA);
    var sessionId = Guid.NewGuid().ToString("N");
    var aRoute = a.WebRtcSignaling.Register(identityB.Device.DeviceId, sessionId);
    var bRoute = b.WebRtcSignaling.Register(identityA.Device.DeviceId, sessionId);
    var signal = WebRtcSignal.Create(sessionId, "candidate", new { candidate = "session-bound-candidate", mid = "data" });
    await a.SendWebRtcSignalAsync(deviceB, signal).WaitAsync(TimeSpan.FromSeconds(10));
    var received = await b.WebRtcSignaling.PollAsync(identityA.Device.DeviceId, sessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
    Assert(received is { Type: "candidate" } && received.SessionId == sessionId && received.Payload.GetProperty("candidate").GetString() == "session-bound-candidate", "Signaling payload was not routed to the authenticated peer/session.");
    await b.SendWebRtcSignalAsync(deviceA, WebRtcSignal.Create(sessionId, "accept", new { accepted = true })).WaitAsync(TimeSpan.FromSeconds(10));
    var response = await a.WebRtcSignaling.PollAsync(identityB.Device.DeviceId, sessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
    Assert(response is { Type: "accept" } && response.SessionId == sessionId && response.Payload.GetProperty("accepted").GetBoolean(), "Reverse signaling response was not delivered.");
    a.WebRtcSignaling.Remove(identityB.Device.DeviceId, sessionId);
    b.WebRtcSignaling.Remove(identityA.Device.DeviceId, sessionId);
    Directory.Delete(root, recursive: true);
}

static async Task TestInternetRendezvousAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-rendezvous-it-" + Guid.NewGuid().ToString("N"));
    var certDirectory = Path.Combine(root, "certs");
    Directory.CreateDirectory(certDirectory);
    var portListener = new TcpListener(IPAddress.Loopback, 0);
    portListener.Start();
    var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
    portListener.Stop();

    using var serverKey = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName("localhost");
    request.CertificateExtensions.Add(names.Build());
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    using var serverCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    var pfxPath = Path.Combine(certDirectory, "rendezvous.pfx");
    const string pfxPassword = "test-only-rendezvous-pfx-password";
    await File.WriteAllBytesAsync(pfxPath, serverCertificate.Export(X509ContentType.Pfx, pfxPassword));
    var serverFingerprint = Convert.ToHexString(SHA256.HashData(serverCertificate.RawData));

    var executableDirectory = new DirectoryInfo(AppContext.BaseDirectory);
    var configuration = executableDirectory.Parent?.Name ?? "Debug";
    var repositoryRoot = executableDirectory.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
    var serviceDll = Path.Combine(repositoryRoot, "src", "Nova.RendezvousService", "bin", configuration, "net10.0", "Nova.RendezvousService.dll");
    Assert(File.Exists(serviceDll), $"Rendezvous Service build output is missing: {serviceDll}");

    using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = repositoryRoot
    }};
    process.StartInfo.ArgumentList.Add(serviceDll);
    process.StartInfo.Environment["NOVA_TLS_PFX_PATH"] = pfxPath;
    process.StartInfo.Environment["NOVA_TLS_PFX_PASSWORD"] = pfxPassword;
    process.StartInfo.Environment["NOVA_HTTPS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    process.StartInfo.Environment["NOVA_STATE_PATH"] = Path.Combine(root, "server-state");
    process.StartInfo.Environment["NOVA_STUN_URIS"] = "stun:stun.example.invalid:3478";
    process.StartInfo.Environment["NOVA_TURN_URIS"] = "turn:turn.example.invalid:3478?transport=udp;turns:turn.example.invalid:5349?transport=tcp";
    var turnSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    process.StartInfo.Environment["NOVA_TURN_SHARED_SECRET"] = turnSecret;
    var serverOutput = new StringBuilder();
    var logGate = new object();
    process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (logGate) serverOutput.AppendLine(args.Data); };
    process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (logGate) serverOutput.AppendLine(args.Data); };

    try
    {
        Assert(process.Start(), "Rendezvous Service process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var identityA = DeviceIdentity.CreateEphemeral("Internet A");
        using var identityB = DeviceIdentity.CreateEphemeral("Internet B");
        var trustA = new PeerTrustStore(Path.Combine(root, "device-a"));
        var trustB = new PeerTrustStore(Path.Combine(root, "device-b"));
        var deviceA = new DiscoveredDevice(identityA.Device.DeviceId, identityA.Device.DisplayName, IPAddress.None, 0,
            identityA.Fingerprint, DateTimeOffset.UtcNow, true, DeviceDiscoverySource.Internet, true, true);
        var deviceB = new DiscoveredDevice(identityB.Device.DeviceId, identityB.Device.DisplayName, IPAddress.None, 0,
            identityB.Fingerprint, DateTimeOffset.UtcNow, true, DeviceDiscoverySource.Internet, true, true);
        trustA.Trust(deviceB);
        trustB.Trust(deviceA);

        await using var clientA = new InternetRendezvousClient(identityA, trustA);
        await using var clientB = new InternetRendezvousClient(identityB, trustB);
        var endpoint = new Uri($"wss://localhost:{port}/v1/connect");
        await clientA.ConnectAsync(endpoint, serverFingerprint);
        await WaitUntilAsync(() => clientA.Status.State == InternetConnectionState.Connected, process, serverOutput, TimeSpan.FromSeconds(15));
        await clientB.ConnectAsync(endpoint, serverFingerprint);
        await WaitUntilAsync(() => clientB.Status.State == InternetConnectionState.Connected, process, serverOutput, TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => clientA.Devices.Any(x => x.DeviceId == identityB.Device.DeviceId && x.Online) &&
            clientB.Devices.Any(x => x.DeviceId == identityA.Device.DeviceId && x.Online), process, serverOutput, TimeSpan.FromSeconds(10));

        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var iceUris = await clientA.GetIceServerUrisAsync(requestTimeout.Token);
        Assert(iceUris.Count == 3 && iceUris[0] == "stun:stun.example.invalid:3478" &&
            iceUris[1].StartsWith("turn:", StringComparison.Ordinal) && iceUris[1].Contains("@turn.example.invalid:3478?transport=udp", StringComparison.Ordinal) &&
            iceUris[2].StartsWith("turns:", StringComparison.Ordinal) && iceUris[2].Contains("@turn.example.invalid:5349?transport=tcp", StringComparison.Ordinal),
            "Rendezvous did not issue STUN/TURN URLs with temporary TURN credentials.");
        var userInfo = iceUris[1]["turn:".Length..].Split('@', 2)[0].Split(':', 2);
        Assert(userInfo.Length == 2, "TURN URL did not contain username and password.");
        var turnUsername = Uri.UnescapeDataString(userInfo[0]);
        var turnPassword = Uri.UnescapeDataString(userInfo[1]);
        var expirySeconds = long.Parse(turnUsername.Split(':', 2)[0], System.Globalization.CultureInfo.InvariantCulture);
        using (var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(turnSecret)))
        {
            var expectedPassword = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(turnUsername)));
            Assert(turnPassword == expectedPassword && expirySeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                expirySeconds <= DateTimeOffset.UtcNow.AddMinutes(11).ToUnixTimeSeconds(),
                "TURN credential HMAC or short expiration did not match coturn REST credentials.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var receivedAtB = new TaskCompletionSource<WebRtcSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.SignalReceived += (sender, signal) =>
        {
            if (sender == identityA.Device.DeviceId && signal.SessionId == sessionId) receivedAtB.TrySetResult(signal);
        };
        var payload = new { offer = "signed-real-sdp-frame", sequence = 17 };
        await clientA.SendSignalAsync(deviceB, WebRtcSignal.Create(sessionId, "offer", payload), requestTimeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), requestTimeout.Token);
        var delivered = await receivedAtB.Task.WaitAsync(TimeSpan.FromSeconds(10), requestTimeout.Token);
        Assert(delivered.Type == "offer" && delivered.SessionId == sessionId &&
            delivered.Payload.GetProperty("offer").GetString() == payload.offer && delivered.Payload.GetProperty("sequence").GetInt32() == payload.sequence,
            "WSS signaling did not preserve the authenticated device and session payload.");

        var reverseId = Guid.NewGuid().ToString("N");
        var receivedAtA = new TaskCompletionSource<WebRtcSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.SignalReceived += (sender, signal) =>
        {
            if (sender == identityB.Device.DeviceId && signal.SessionId == reverseId) receivedAtA.TrySetResult(signal);
        };
        await clientB.SendSignalAsync(deviceA, WebRtcSignal.Create(reverseId, "sdp", new { type = "answer", answer = "signed-reverse-frame" }), requestTimeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), requestTimeout.Token);
        var reverse = await receivedAtA.Task.WaitAsync(TimeSpan.FromSeconds(10), requestTimeout.Token);
        Assert(reverse.Type == "sdp" && reverse.SessionId == reverseId && reverse.Payload.GetProperty("type").GetString() == "answer" &&
            reverse.Payload.GetProperty("answer").GetString() == "signed-reverse-frame",
            "Reverse WSS signaling was not delivered.");

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var offlineDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < offlineDeadline && (clientA.Status.State != InternetConnectionState.Reconnecting ||
            clientB.Status.State != InternetConnectionState.Reconnecting || clientA.Devices.Any(x => x.Online) || clientB.Devices.Any(x => x.Online)))
            await Task.Delay(100);
        Assert(clientA.Status.State == InternetConnectionState.Reconnecting && clientB.Status.State == InternetConnectionState.Reconnecting &&
            clientA.Devices.All(x => !x.Online) && clientB.Devices.All(x => !x.Online),
            "Rendezvous loss did not mark Internet devices offline and reconnecting.");
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task WaitUntilAsync(Func<bool> predicate, Process server, StringBuilder output, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (predicate()) return;
        if (server.HasExited)
        {
            string detail;
            lock (output) detail = output.ToString();
            throw new InvalidOperationException($"Rendezvous Service exited with code {server.ExitCode}: {detail}");
        }
        await Task.Delay(100);
    }
    string logs;
    lock (output) logs = output.ToString();
    throw new TimeoutException($"Timed out waiting for Rendezvous Service state. Server log: {logs}");
}

static async Task TestWebRtcCoordinatorAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-webrtc-coordinator-" + Guid.NewGuid().ToString("N"));
    var aRoot = Path.Combine(root, "a");
    var bRoot = Path.Combine(root, "b");
    var destination = Path.Combine(bRoot, "received");
    Directory.CreateDirectory(aRoot);
    Directory.CreateDirectory(destination);
    using var identityA = DeviceIdentity.CreateEphemeral("Coordinator A");
    using var identityB = DeviceIdentity.CreateEphemeral("Coordinator B");
    await using var a = new LocalTransferService(aRoot, identity: identityA);
    await using var b = new LocalTransferService(bRoot, destinationDirectory: destination, identity: identityB);
    a.Start();
    b.Start();
    var discoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(12);
    DiscoveredDevice? deviceA = null;
    DiscoveredDevice? deviceB = null;
    while (DateTimeOffset.UtcNow < discoveryDeadline)
    {
        deviceA = a.GetAvailableDevices().FirstOrDefault(x => x.DeviceId == identityB.Device.DeviceId);
        deviceB = b.GetAvailableDevices().FirstOrDefault(x => x.DeviceId == identityA.Device.DeviceId);
        if (deviceA is not null && deviceB is not null) break;
        await Task.Delay(200);
    }
    Assert(deviceA is not null && deviceB is not null, "Independent LocalTransferService instances did not discover one another over multicast.");
    a.TrustDevice(deviceA!);
    b.TrustDevice(deviceB!);

    var contents = new byte[256 * 1024 + 91];
    RandomNumberGenerator.Fill(contents);
    var source = Path.Combine(aRoot, "coordinator-payload.bin");
    await File.WriteAllBytesAsync(source, contents);
    var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelRequest = new TaskCompletionSource<WebRtcIncomingOfferRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receiverCancelRequest = new TaskCompletionSource<WebRtcIncomingOfferRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelledAtSender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelledAtReceiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var receiverCancellationAtSender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var receiverCancellationAtReceiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    b.WebRtcIncomingOffer += request =>
    {
        if (request.Offer.FileName == "reject.bin") request.Respond(false);
        else if (request.Offer.FileName == "cancel.bin") cancelRequest.TrySetResult(request);
        else if (request.Offer.FileName == "cancel-from-receiver.bin") receiverCancelRequest.TrySetResult(request);
        else { request.Respond(true); accepted.TrySetResult(); }
    };
    a.WebRtcProgressChanged += progress =>
    {
        if (progress.State == WebRtcTransferSessionState.Completed) received.TrySetResult();
        if (progress.State == WebRtcTransferSessionState.Rejected && progress.FileName == "reject.bin") rejected.TrySetResult();
        if (progress.State == WebRtcTransferSessionState.Cancelled && progress.FileName == "cancel.bin") cancelledAtSender.TrySetResult();
        if (progress.State == WebRtcTransferSessionState.Cancelled && progress.FileName == "cancel-from-receiver.bin") receiverCancellationAtSender.TrySetResult();
    };
    b.WebRtcProgressChanged += progress =>
    {
        if (progress.State == WebRtcTransferSessionState.Cancelled && progress.FileName == "cancel.bin") cancelledAtReceiver.TrySetResult();
        if (progress.State == WebRtcTransferSessionState.Cancelled && progress.FileName == "cancel-from-receiver.bin") receiverCancellationAtReceiver.TrySetResult();
    };
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var send = a.SendWebRtcFilesAsync(deviceA!, [source], timeout.Token);
    await accepted.Task.WaitAsync(TimeSpan.FromSeconds(20), timeout.Token);
    await send.WaitAsync(TimeSpan.FromSeconds(75), timeout.Token);
    await received.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    var finalPath = Path.Combine(destination, "coordinator-payload.bin");
    Assert(File.Exists(finalPath), "Accepted WebRTC transfer did not produce a final file.");
    var actual = await File.ReadAllBytesAsync(finalPath, timeout.Token);
    Assert(actual.AsSpan().SequenceEqual(contents), "Coordinator transfer output differs from the source payload.");
    Assert(Convert.ToHexString(SHA256.HashData(actual)) == Convert.ToHexString(SHA256.HashData(contents)), "Coordinator transfer SHA-256 differs.");

    var rejectPath = Path.Combine(aRoot, "reject.bin");
    await File.WriteAllBytesAsync(rejectPath, [1, 2, 3, 4]);
    try
    {
        await a.SendWebRtcFilesAsync(deviceA!, [rejectPath], timeout.Token).WaitAsync(TimeSpan.FromSeconds(20), timeout.Token);
        throw new InvalidOperationException("Rejected transfer returned success to the sender.");
    }
    catch (WebRtcTransferRejectedException) { }
    await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    Assert(!File.Exists(Path.Combine(destination, "reject.bin")), "Rejected transfer unexpectedly created a destination file.");

    var cancelPath = Path.Combine(aRoot, "cancel.bin");
    await File.WriteAllBytesAsync(cancelPath, [5, 6, 7, 8]);
    var cancelSend = a.SendWebRtcFilesAsync(deviceA!, [cancelPath], timeout.Token);
    var pending = await cancelRequest.Task.WaitAsync(TimeSpan.FromSeconds(20), timeout.Token);
    Assert(a.CancelWebRtcTransfer(pending.Offer.TransferId), "An active pre-approval session could not be cancelled.");
    try { await cancelSend.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token); throw new InvalidOperationException("Cancelled sender operation returned success."); }
    catch (OperationCanceledException) { }
    await cancelledAtSender.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    await cancelledAtReceiver.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    Assert(!File.Exists(Path.Combine(destination, "cancel.bin")), "Cancelled pre-approval transfer unexpectedly created a destination file.");

    var receiverCancelPath = Path.Combine(aRoot, "cancel-from-receiver.bin");
    await File.WriteAllBytesAsync(receiverCancelPath, [9, 10, 11, 12]);
    var receiverCancelSend = a.SendWebRtcFilesAsync(deviceA!, [receiverCancelPath], timeout.Token);
    var receiverPending = await receiverCancelRequest.Task.WaitAsync(TimeSpan.FromSeconds(20), timeout.Token);
    Assert(b.CancelWebRtcTransfer(receiverPending.Offer.TransferId), "The receiver could not cancel its pending approval session.");
    try { await receiverCancelSend.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token); throw new InvalidOperationException("Remote cancellation returned success to the sender."); }
    catch (OperationCanceledException) { }
    await receiverCancellationAtSender.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    await receiverCancellationAtReceiver.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    Assert(!File.Exists(Path.Combine(destination, "cancel-from-receiver.bin")), "Receiver-cancelled transfer unexpectedly created a destination file.");
    Directory.Delete(root, recursive: true);
}

static void TestUnsupportedTransferVersion()
{
    var transferId = Guid.NewGuid().ToString("N");
    var bytes = System.Text.Encoding.UTF8.GetBytes("{\"version\":2,\"type\":\"offer\",\"transferId\":\"" + transferId + "\",\"payload\":{}}");
    Expect<InvalidDataException>(() => TransferProtocolCodec.DecodeControl<System.Text.Json.JsonElement>(bytes));
    var unknownType = System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"unknown\",\"transferId\":\"" + transferId + "\",\"payload\":{}}");
    Expect<InvalidDataException>(() => TransferProtocolCodec.DecodeControl<System.Text.Json.JsonElement>(unknownType));
    var duplicateField = System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"version\":1,\"type\":\"offer\",\"transferId\":\"" + transferId + "\",\"payload\":{}}");
    Expect<InvalidDataException>(() => TransferProtocolCodec.DecodeControl<System.Text.Json.JsonElement>(duplicateField));
}

static void TestTransferDataBlock()
{
    var transferId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    var payload = new byte[] { 0, 1, 2, 128, 255 };
    var frame = TransferProtocolCodec.EncodeDataBlock(transferId, 65536, payload);
    Assert(frame.Length == TransferProtocolCodec.DataBlockHeaderLength + payload.Length, "Размер блока неверен.");
    Assert(Convert.ToHexString(frame.AsSpan(8, 16)) == "00112233445566778899AABBCCDDEEFF", "GUID не закодирован в сетевом порядке байтов.");
    var decoded = TransferProtocolCodec.DecodeDataBlock(frame);
    Assert(decoded.TransferId == transferId && decoded.OffsetBytes == 65536 && decoded.Payload.Span.SequenceEqual(payload),
        "Бинарный блок не прошёл обратное преобразование.");

    var maxPayload = new byte[TransferProtocolCodec.MaximumDataBlockLength];
    var maxFrame = TransferProtocolCodec.EncodeDataBlock(transferId, 0, maxPayload);
    Assert(maxFrame.Length == TransferProtocolCodec.MaximumMessageLength, "Максимальный блок не укладывается в лимит сообщения.");
}

static void TestInvalidTransferDataBlock()
{
    var id = Guid.NewGuid();
    var frame = TransferProtocolCodec.EncodeDataBlock(id, 0, new byte[] { 1, 2, 3 });
    var badMagic = frame.ToArray();
    badMagic[0] ^= 0xFF;
    Expect<InvalidDataException>(() => TransferProtocolCodec.DecodeDataBlock(badMagic));
    var reserved = frame.ToArray();
    reserved[6] = 1;
    Expect<InvalidDataException>(() => TransferProtocolCodec.DecodeDataBlock(reserved));
    var wrongLength = frame.ToArray();
    wrongLength[35] = 2;
    Expect<InvalidDataException>(() => TransferProtocolCodec.DecodeDataBlock(wrongLength));
    var overflow = frame.ToArray();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(overflow.AsSpan(24, 8), ulong.MaxValue);
    Expect<InvalidDataException>(() => TransferProtocolCodec.DecodeDataBlock(overflow));
    Expect<ArgumentOutOfRangeException>(() =>
        TransferProtocolCodec.EncodeDataBlock(id, 0, new byte[TransferProtocolCodec.MaximumDataBlockLength + 1]));
}

static void TestLargeTransferOffset()
{
    const long offset = 5L * 1024 * 1024 * 1024 * 1024;
    var transferId = Guid.NewGuid();
    var frame = TransferProtocolCodec.EncodeDataBlock(transferId, offset, new byte[] { 9, 8, 7 });
    var decoded = TransferProtocolCodec.DecodeDataBlock(frame);
    Assert(decoded.OffsetBytes == offset && decoded.TransferId == transferId && decoded.Payload.Span.SequenceEqual(new byte[] { 9, 8, 7 }),
        "Large 64-bit data block offset was truncated.");
}

static void Expect<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Ожидалось исключение {typeof(TException).Name}.");
}



Console.WriteLine($"Итог: успешно {passed}, ошибок {failed}.");
return failed == 0 ? 0 : 1;

async Task Run(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL  {name}:{Environment.NewLine}{ex.ToString()}"); }
}

void RunSync(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}"); }
}

static async Task TestFramedJsonAsync()
{
    var source = new MemoryStream();
    await FramedJson.WriteAsync(source, new TestPayload("NOVA", 42));
    source.Position = 0;
    using var chunked = new ChunkedReadStream(source, 3);
    var actual = await FramedJson.ReadAsync<TestPayload>(chunked);
    Assert(actual.Name == "NOVA" && actual.Value == 42, "Значение сообщения не совпало.");
}

static void TestOversizedFrame()
{
    var data = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data, FramedJson.MaximumFrameLength + 1);
    using var stream = new MemoryStream(data);
    try { FramedJson.ReadAsync<TestPayload>(stream).GetAwaiter().GetResult(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Большой кадр протокола был принят.");
}

static void TestPathSafety()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-path-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = TransferFileSecurity.ValidateAndResolve(root, "фото 1.jpg");
        Assert(Path.GetDirectoryName(path) == Path.GetFullPath(root), "Файл разрешён за пределами папки.");
        foreach (var invalid in new[] { "..\\secret.txt", "../secret.txt", "C:\\secret.txt", "." })
        {
            try { TransferFileSecurity.ValidateAndResolve(root, invalid); }
            catch (InvalidDataException) { continue; }
            throw new InvalidOperationException($"Недопустимое имя принято: {invalid}");
        }
        File.WriteAllText(Path.Combine(root, "фото 1.jpg"), "exists");
        Assert(Path.GetFileName(TransferFileSecurity.MakeUniquePath(root, "фото 1.jpg")) == "фото 1 (2).jpg", "Конфликт не получил новое имя.");
    }
    finally { Directory.Delete(root, true); }
}

static void TestDiscoveryPayload()
{
    var fingerprint = new string('A', 64);
    var valid = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
    {
        Version = 1,
        DeviceId = Guid.NewGuid().ToString("N"),
        DisplayName = "Компьютер",
        Port = 44000,
        Fingerprint = fingerprint,
        SentAt = DateTimeOffset.UtcNow
    }));
    Assert(LanDiscoveryService.TryParseAnnouncement(valid, IPAddress.Loopback, out var found) && found?.Port == 44000, "Корректное объявление отклонено.");
    var bad = System.Text.Encoding.UTF8.GetBytes("{\"Version\":1,\"Port\":70000}");
    Assert(!LanDiscoveryService.TryParseAnnouncement(bad, IPAddress.Loopback, out _), "Некорректное объявление принято.");
}

static void TestDuplicateDeviceIdentityTrust()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-trust-identity-" + Guid.NewGuid().ToString("N"));
    using var first = DeviceIdentity.CreateEphemeral("Trust identity A");
    using var second = DeviceIdentity.CreateEphemeral("Trust identity B");
    var store = new PeerTrustStore(root);
    store.Trust(new DiscoveredDevice(first.Device.DeviceId, first.Device.DisplayName, IPAddress.None, 0,
        first.Fingerprint, DateTimeOffset.UtcNow, false));
    Expect<InvalidOperationException>(() => store.Trust(new DiscoveredDevice(first.Device.DeviceId, "Impostor", IPAddress.None, 0,
        second.Fingerprint, DateTimeOffset.UtcNow, false)));
    Assert(store.Find(first.Fingerprint)?.Device.DeviceId == first.Device.DeviceId && !store.IsTrusted(second.Fingerprint),
        "The trust store replaced the certificate associated with an existing device ID.");
    Directory.Delete(root, recursive: true);
}

static async Task TestEndToEndAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-transfer-" + Guid.NewGuid().ToString("N"));
    var aRoot = Path.Combine(root, "a");
    var bRoot = Path.Combine(root, "b");
    var sourceDirectory = Path.Combine(root, "source");
    var destination = Path.Combine(root, "received");
    Directory.CreateDirectory(sourceDirectory);
    Directory.CreateDirectory(destination);
    var sourceFile = Path.Combine(sourceDirectory, "large.bin");
    await using (var file = new FileStream(sourceFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
    {
        var block = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(block);
        for (var i = 0; i < 12; i++) await file.WriteAsync(block);
    }

    using var identityA = DeviceIdentity.CreateEphemeral("Компьютер A");
    using var identityB = DeviceIdentity.CreateEphemeral("Компьютер B");
    await using var a = new LocalTransferService(aRoot, identity: identityA);
    await using var b = new LocalTransferService(bRoot, destinationDirectory: destination, identity: identityB);
    a.TrustedPeers.Trust(new DiscoveredDevice(identityB.Device.DeviceId, identityB.Device.DisplayName, IPAddress.Loopback, 1, identityB.Fingerprint, DateTimeOffset.UtcNow, false));
    b.TrustedPeers.Trust(new DiscoveredDevice(identityA.Device.DeviceId, identityA.Device.DisplayName, IPAddress.Loopback, 1, identityA.Fingerprint, DateTimeOffset.UtcNow, false));
    var acceptNext = true;
    b.IncomingOffer += request => request.Respond(acceptNext);
    a.Start(enableDiscovery: false);
    b.Start(enableDiscovery: false);
    var peer = new DiscoveredDevice(identityB.Device.DeviceId, identityB.Device.DisplayName, IPAddress.Loopback, b.TcpPort, identityB.Fingerprint, DateTimeOffset.UtcNow, true);
    await a.SendFileAsync(peer, sourceFile).WaitAsync(TimeSpan.FromSeconds(30));

    var received = Path.Combine(destination, "large.bin");
    Assert(File.Exists(received), "Файл не сохранён.");
    byte[] sourceDigest;
    byte[] receivedDigest;
    await using (var sourceStream = File.OpenRead(sourceFile)) sourceDigest = await SHA256.HashDataAsync(sourceStream);
    await using (var receivedStream = File.OpenRead(received)) receivedDigest = await SHA256.HashDataAsync(receivedStream);
    Assert(sourceDigest.SequenceEqual(receivedDigest), "Хэши исходного и полученного файла различаются.");
    Assert(a.History.GetRecent().Any(x => x.State == TransferState.Completed && x.Direction == TransferDirection.Outgoing), "Исходящая история не сохранена.");
    Assert(b.History.GetRecent().Any(x => x.State == TransferState.Completed && x.Direction == TransferDirection.Incoming), "Входящая история не сохранена.");

    acceptNext = false;
    var rejectedFile = Path.Combine(sourceDirectory, "отклонён.txt");
    await File.WriteAllTextAsync(rejectedFile, "этот файл не должен попасть к получателю");
    await a.SendFileAsync(peer, rejectedFile).WaitAsync(TimeSpan.FromSeconds(10));
    Assert(!File.Exists(Path.Combine(destination, "отклонён.txt")), "Отклонённый файл был сохранён.");
    Assert(a.History.GetRecent().Any(x => x.FileName == "отклонён.txt" && x.State == TransferState.Rejected), "Отклонённая передача не появилась в истории отправителя.");
    Assert(b.History.GetRecent().Any(x => x.FileName == "отклонён.txt" && x.State == TransferState.Rejected), "Отклонённая передача не появилась в истории получателя.");
    await a.DisposeAsync();
    await b.DisposeAsync();

    var restarted = new TransferHistoryStore(bRoot);
    Assert(restarted.GetRecent().Any(x => x.FileName == "large.bin" && x.State == TransferState.Completed), "История не пережила перезапуск хранилища.");
    Directory.Delete(root, true);
}

static async Task TestUnknownPeerAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "nova-unknown-" + Guid.NewGuid().ToString("N"));
    using var serverIdentity = DeviceIdentity.CreateEphemeral("Сервер");
    using var allowedPeer = DeviceIdentity.CreateEphemeral("Доверенное устройство");
    using var unknown = DeviceIdentity.CreateEphemeral("Неизвестное устройство");
    await using var host = new LocalTransferService(Path.Combine(root, "host"), identity: serverIdentity);
    host.TrustedPeers.Trust(new DiscoveredDevice(allowedPeer.Device.DeviceId, allowedPeer.Device.DisplayName, IPAddress.Loopback, 1, allowedPeer.Fingerprint, DateTimeOffset.UtcNow, false));
    Assert(host.TrustedPeers.IsTrusted(allowedPeer.Fingerprint), "Контрольное устройство не добавлено в доверенные.");
    Assert(!host.TrustedPeers.IsTrusted(unknown.Fingerprint), "Неизвестное устройство неожиданно отмечено доверенным.");
    host.Start(enableDiscovery: false);
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, host.TcpPort);
    using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == serverIdentity.Fingerprint);
    var rejected = false;
    try
    {
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "nova-lan-peer",
            ClientCertificates = new X509CertificateCollection { unknown.Certificate },
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }).WaitAsync(TimeSpan.FromSeconds(5));

        // The client may finish sending its TLS Finished message before the server processes its client-certificate rejection.
        var buffer = new byte[1];
        try
        {
            var read = await ssl.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            rejected = read == 0;
        }
        catch (AuthenticationException) { rejected = true; }
        catch (IOException) { rejected = true; }
    }
    catch (AuthenticationException) { rejected = true; }
    catch (IOException) { rejected = true; }

    Assert(rejected, "Сервер не отклонил клиентский сертификат с неизвестным fingerprint.");
    await host.DisposeAsync();
    Directory.Delete(root, true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record TestPayload(string Name, int Value);

sealed class ChunkedReadStream(Stream inner, int maximumChunk) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maximumChunk));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunk)], cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
