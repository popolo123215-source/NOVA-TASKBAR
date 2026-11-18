# NOVA TaskBar implementation plan

**Updated:** 2026-11-15

**Milestone:** Internet Mode implementation (public deployment and cross-network acceptance pending)

> This document retains dated milestone snapshots for project history. Historical toolchain and blocker notes apply to those dated entries; the current verified state and active Critical Path are in the latest section at the end.

## Preflight

- Existing solution contains the WPF shell, Russian interface, and dependency-free Nova.Core.Contracts project. Existing transfer DTOs describe offers/progress/decisions, but there was no engine or UI wiring.
- .NET SDK 10.0.403 and Windows Desktop runtimes are present. Existing stage-0 Debug output is present. The NuGet cache has no xUnit or Microsoft.NET.Test.Sdk packages; the new checks therefore use a dependency-free console test harness.
- Current execution policy blocks process creation through both the command runner and Node child-process API. Build/test execution may be unavailable in this session.
- No second NOVA Windows client is connected to the available desktop inventory. A loopback integration harness can exercise two independent engine instances, but that does not prove two-machine operation.

## Short plan and estimate

1. Add a separate Transfer Engine for local discovery, certificate trust, authenticated streaming, safe destination handling, and persistent history.
2. Connect the existing WPF panel to device selection, fingerprint confirmation, file selection, incoming consent, progress, folder choice, and history.
3. Add package-free protocol/security/loopback transfer checks; build Debug and run them if process execution is allowed.
4. Validate on two real Windows clients when available.

Estimate: **optimistic 1–2 engineering days; realistic 3–5 days; problem case 1–2 weeks** if firewall/multicast behavior, certificate handling, or multi-machine debugging requires iteration.

## Architecture and trust boundary

- Nova.Core.Contracts remains the source for device, transfer, direction, state, progress, and history data.
- Nova.TransferEngine owns UDP multicast discovery, mutual TLS, peer trust, framed JSON offers, streamed file bytes, integrity checks, folder safety, and durable history. WPF only calls service APIs and renders events.
- Discovery uses multicast and chooses the source address plus announced listener port automatically. A discovery packet is not authentication.
- Users compare the full SHA-256 certificate fingerprint on both clients and trust the peer on each device. The listener accepts file-protocol messages only from trusted client certificates. Each file still requires recipient approval.
- Payloads use a bounded 64 KiB memory buffer and a 64 KiB maximum JSON frame. Receiver validates SHA-256 before a collision-safe move. Resume is explicitly unavailable in this first local implementation.

## Milestone status

- [x] Reuse and extend Nova.Core.Contracts with transfer history/direction.
- [x] Add separate Transfer Engine with multicast discovery and mutual TLS trust checks.
- [x] Add one-or-many file selection and peer selection without manual addresses.
- [x] Add explicit incoming accept/reject, tray notification, and progress display.
- [x] Add selectable receive folder, path traversal checks, no-overwrite naming, and no auto-open.
- [x] Persist incoming/outgoing history across restarts.
- [x] Add dependency-free protocol and loopback integration scenarios.
- [ ] Debug build: **NOT VERIFIED** — process creation is blocked by this environment.
- [ ] Run local protocol/transfer harness: **NOT VERIFIED** — process creation is blocked.
- [ ] Verify multicast discovery, Windows Firewall, and an actual transfer between two computers: **NOT VERIFIED** — no second client is available in the current desktop inventory.

## Critical path

1. Run Debug build and the local test harness; correct compiler/runtime failures.
2. Exercise two separate Windows clients on a LAN, pair fingerprints in both directions, and transfer multi-megabyte plus zero-byte files.
3. Validate Windows Firewall and multicast behavior across common Wi-Fi/router configurations.
4. Add transfer resume only after the on-disk partial-file protocol is designed and tested.
5. Later: Android client against the documented protocol; cloud relay is outside this milestone.

## Blockers and limitations

- Command execution is blocked before dotnet starts, so this implementation has not been compiled or run in this session.
- No separate Windows peer is available here; two-engine loopback tests cannot replace cross-machine validation.
- Multicast discovery may be blocked by AP/client isolation or firewall policy. When that happens, devices will not appear; this stage does not add manual IP entry.
- Device trust is manual and reciprocal: users must compare fingerprints and approve the peer on both computers. Files always require individual recipient acceptance.
- Resume is not enabled; the offer advertises CanResume=false.
- No cloud relay, Android implementation, clipboard history, or remote control is included.

## Verification commands

Build the Windows client with: dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj -c Debug

Run protocol and loopback checks with: dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug

See [local protocol v1](protocol/LOCAL_TRANSFER.md), [transfer wire design](protocol/TRANSFER_PROTOCOL.md), and [API schema](protocol/openapi.yaml).

## References

- [Microsoft SslStream overview](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-10.0)
- [SslStream server authentication](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream.authenticateasserverasync)


## Milestone 2 — Internet Transfer Engine foundation

**Updated:** 2026-11-13

### Preflight

- Existing LAN Transfer Engine is a separate mutual-TLS TCP implementation with its own local discovery, trust store, consent callback, streamed bytes, SHA-256, and history. No LAN source was changed for this stage.
- Shared transfer DTOs already provide TransferOffer, TransferDecision, TransferProgress, history, and a resume method. CanResume is currently false in the LAN sender.
- Added a platform-neutral Nova.TransferProtocol project targeting net10.0; this is the first reusable wire component for Windows and Android, not a native transport or complete Internet transfer service.
- dotnet.exe exists at the standard Program Files path. CMake, Visual Studio 2022 Community, and Android NDK were not found at the checked standard paths. This does not prove no alternate installation exists.
- Process launch is blocked in this execution environment, so compile/test status cannot be established here.

### Scope completed in this stage

- Specify a bounded version-1 JSON control envelope and binary file-block header suitable for individual reliable/ordered DataChannel messages.
- Enforce a 16 KiB maximum message size and canonical transfer IDs in the codec.
- Add regression scenarios for control round trips, version/type rejection, network-order UUIDs, block bounds, malformed headers, and offset overflow.
- Keep local LAN framing and behavior independent.

### Status

- [x] Add platform-neutral transfer protocol project and bounded codec.
- [x] Document wire layout, control message registry, limits, and pending semantics.
- [x] Add four protocol cases to the existing dependency-free harness.
- [x] Compile Core.Contracts, TransferProtocol, and TransferEngine: verified in the 2026-11-14 follow-up; native task rebuild also passed.
- [ ] Run all existing LAN and new protocol scenarios: NOT VERIFIED in this WebRTC dependency task; the earlier implementation session ran the 10-case harness successfully.
- [x] Build Windows x64 DLL against pinned libdatachannel v0.24.5: verified below. Android .so remains NOT VERIFIED.
- [ ] Verify Windows↔Android DataChannel, ICE direct, TURN fallback, authenticated identity, consent, durable resume and SHA-256 on real peers.

### Estimate for the complete Internet Transfer Engine

For one developer: **optimistic 3–4 weeks; realistic 6–10 weeks; problem case 3–4 months**. The longer cases include native toolchain/ABI integration, Android identity binding, NAT/TURN deployment, durable resume behavior, and real cross-network debugging.

### Critical path

1. Compile and run the platform-neutral protocol component and preserve all existing LAN scenarios.
2. Pin libdatachannel and build the C ABI native core for Windows x64 and Android arm64; add JNI/P/Invoke lifetime-safe adapters.
3. Implement signaling and authenticated device proof bound to the WebRTC DTLS session.
4. Implement consent and bounded sending/receiving with durable block acknowledgements and resume.
5. Add route coordinator: LAN → direct ICE → TURN → actionable error.
6. Validate on Windows and Android across different networks, including direct ICE, relay-only, interruption/resume, and integrity failure.

### Current blockers and limits

- Native x64 build toolchain and OpenSSL are now provisioned reproducibly as recorded below. Android native build remains unverified.
- The protocol codec bounds individual messages but does not yet implement transfer-session state, transport backpressure, checkpoint persistence, resume, identity proof, signalling, or route selection.
- No cross-platform or Internet transfer is claimed as working.

### WebRTC native transport progress (2026-11-14)

- Toolchain verified through Visual Studio 18 `VsDevCmd.bat`: MSVC 19.52.37029 x64 and CMake 4.3.1.
- OpenSSL no longer blocks the native build. `vcpkg.json` pins registry baseline `aa2d37682e3318d93aef87efa7b0e88e81cd3d59` and OpenSSL `3.5.4`; manifest mode builds static x64 OpenSSL under `build/native-x64/vcpkg_installed` without relying on a system OpenSSL. `scripts/build-native.ps1` bootstraps the pinned vcpkg checkout and builds the complete target.
- `libdatachannel` v0.24.5 and `nova_webrtc.dll` built successfully for x64 Release. Artifact: `build/native-x64/Release/nova_webrtc.dll`. `dumpbin /headers` reports machine `8664 (x64)` and PE32+; `dumpbin /exports` lists all nine ABI v1 exports.
- A .NET P/Invoke probe loaded the DLL by absolute path and called `nova_webrtc_abi_version`, receiving `1`. This verifies DLL loading and that export call only; peer negotiation and DataChannel runtime behavior are not yet verified.
- `Nova.TransferEngine` rebuilt after native delivery: 0 warnings, 0 errors. The 10-case harness was not rerun in the dependency task; the subsequent native-backed validation is recorded below.
- LAN source remains unchanged. Durable transfer sessions, identity authentication, receiver file writes, ACK/resume, and route selection remain pending.

#### Reproducible Windows x64 build

From a PowerShell prompt in the repository root:

```powershell
./scripts/build-native.ps1
```

The script checks out pinned vcpkg, bootstraps its executable inside `build/tools/vcpkg`, installs manifest-pinned OpenSSL 3.5.4 for `x64-windows-static`, deletes/recreates only `build/native-x64`, configures with Visual Studio 18 x64, and builds `nova_webrtc` Release. CMake FetchContent checks out the fixed `libdatachannel` tag `v0.24.5`.

#### Critical path

1. Add native-backed two-peer tests for offer/answer, ICE, ordered reliable DataChannel messages, callback lifetime and close/dispose races.
2. Make the managed adapter available in the Windows client output and run a real peer-to-peer runtime scenario.
3. Complete authenticated transfer-session orchestration and file block durability/ACK/resume before claiming Internet transfer support.

### Native-backed WebRTC integration validation (2026-11-14)

- Added an integration case to the existing `Nova.TransferEngine.Tests` harness; it constructs two independent `WebRtcPeer` instances and communicates only through their native libdatachannel PeerConnections.
- The test project copies `build/native-x64/Release/nova_webrtc.dll` beside its executable. OpenSSL and libdatachannel are statically linked into the bridge for this build.
- The real local signaling relay forwards native SDP and trickle ICE callbacks. It queues candidates until the matching remote description is installed and drives answer creation. The test requires both local ICE callbacks, both PeerConnection connected-state callbacks, and both DataChannel open callbacks before sending.
- The test exposed a missing remote DataChannel callback registration in the bridge. Added callback registration/event kind 7 and installs open/close/message callbacks for the remotely-created channel. Also configured explicit negotiation and raised the managed/native message cap to 64 KiB so the test can send payloads larger than 16 KiB.
- `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug` passed **11/11** after these changes, including the native-backed offer/answer, ICE, channel-open, and bidirectional binary payload scenario. The scenario transfers 16,641 bytes A→B and 16,897 bytes B→A and compares every byte. It was repeated successfully before adding the final connected-state/candidate assertions; the strengthened scenario also passed once.
- This verifies loopback ICE and a real native DataChannel on one Windows host. Cross-network/STUN/TURN behavior, Windows client packaging, and authenticated NOVA device identity remain **NOT VERIFIED**.

#### Updated Critical Path

1. Package the native DLL in the Windows client publish output using the same build artifact and validate app startup from a clean publish directory.
2. Exercise callback queue saturation and cross-thread close/send races under a dedicated bounded stress scenario.
3. Add authenticated transfer session semantics and durable file transfer only after the native transport lifecycle remains stable under those tests.
4. Validate direct ICE and TURN between separate machines/networks; loopback success does not prove NAT traversal.

### WebRTC lifecycle and stability validation (2026-11-14)

- The existing native integration scenario now performs **25 sequential complete lifecycles**. Every cycle creates two native peers, negotiates SDP, exchanges trickle ICE, waits for both ICE-connected states and both channel-open events, sends/verifies 16,641-byte and 16,897-byte binary payloads, closes the DataChannel on both ends, and disposes both peers before starting the next cycle.
- Added `nova_peer_close_channel` to close the channel independently and verify the remote close callback before PeerConnection teardown. Repeated channel close and repeated `DisposeAsync` are exercised in each cycle. A second send after channel close must fail before entering native code.
- A 20-second per-cycle timeout bounds connection/send/close waits; event pumps are cancelled and required to stop within 3 seconds. The complete 25-cycle run finished in **7.06 seconds**, and the harness process exited normally.
- Lifecycle review found that callback delegates were owned only by `WebRtcPeer`, while callbacks are registered in native state. `SafePeerHandle` now roots the delegate for the native handle lifetime so finalization cannot collect the thunk before native close. The event byte budget is decremented as events are consumed; malformed callback data/managed callback exceptions and queue overflow complete the managed event stream with an explicit error instead of unwinding into native code or silently discarding lifecycle events. Native callback handling remains non-blocking (`TryWrite`).
- Full `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug` after rebuilding the native bridge passed **11/11**, including all 25 native lifecycles. No hang or callback exception was observed.
- This is repeated functional lifecycle evidence, not a heap/native-allocation leak proof. Native live-object counts are not exposed by the current ABI, and process memory/working-set measurements are not a reliable per-object leak detector. Queue saturation behavior is explicitly fail-fast but was not driven to capacity in a synthetic test.

#### Lifecycle Critical Path

1. Add a focused bounded-queue saturation test and verify callers always observe the terminal queue error.
2. Add cross-thread send/dispose race and cancellation-during-negotiation scenarios if those concurrent operations are exposed to production callers.
3. Validate DLL packaging from a clean Windows client publish and repeat lifecycle tests across supported Windows versions.
4. Proceed to STUN/TURN and authenticated file-transfer sessions only after packaging/runtime validation.

### File-transfer implementation checkpoint (2026-11-14)

- Added the `cancel` control type and `TransferCancelPayload` to the existing bounded `Nova.TransferProtocol` codec, with a round-trip regression test.
- Added `WebRtcFileBlockTransfer`: sequential source reads use 16,348-byte payload blocks (16 KiB total protocol message including the 36-byte block header), wait for a durable-offset ACK after every block, and verify the source digest. Receive validates transfer ID/offset/size, checks available volume space, writes a hidden unique partial file, flushes it to disk before each ACK, verifies final length/hash, and moves it to a collision-safe final name. Temporary data is removed on failure/cancellation. There is no application-level total file-size cap; total sizes, offsets and progress use signed 64-bit values, bounded buffers are independent of file size, and the filesystem/API limits remain authoritative.
- The native-backed test transfers a 37-byte file and a 5,243,053-byte file over the actual two-peer DataChannel. It sends a `TransferOffer`, receives a test-peer `TransferDecision`, and returns `BlockAcknowledgement` control envelopes over the reverse DataChannel. The test peer auto-accepts; this is not a user consent UI. Both destination files are checked for exact size and SHA-256; partial files are not left behind. The 5 MiB scenario uses one data block in flight, two slots in the managed frame handoff, and waits for each durable ACK before reading/sending the next block.
- Confirmed the application has no WebRTC signaling channel: current discovery advertises the mutually authenticated LAN file-service TCP port, and no signaling listener/route exists. Reusing that port requires changing the LAN service's framing/handler; introducing a separate listener requires advertising a new port. Neither can be presented as an existing signaling mechanism. No production WebRTC file-transfer path or UI route is enabled yet.
- The test harness passed **13/13**, including the native-backed offer/decision, small/large file block transfer, returned ACKs, and 25 sequential lifecycle cycles (8.36s on this run). This validates streaming block/finalization primitives through native WebRTC, but acceptance is automatic in the test and there is no production session coordinator or UI binding.
- Immediate Critical Path: add a real signaling route while preserving existing LAN transfer semantics; implement per-session offer/consent/cancel/error coordination and route UI selections to it; verify that full user flow over native WebRTC. Until signaling and UI integration exist, users cannot initiate WebRTC files across devices from the application.

### File-size and long-streaming clarification (2026-11-14)

- Removed the provisional 20 GiB receive cap. The WebRTC block stream has no application-level total-size ceiling: .NET stream lengths, offers, offsets, acknowledgements and progress use `long`; each DataChannel block remains independently bounded and does not cap total file size.
- Added a streaming SHA-256 helper and source re-hash during send, receiver capacity check using destination-volume free space, block bounds before offset advancement, disk flush before durable ACK, and cleanup on failure. Actual limits are the source/destination filesystem, available space, .NET stream APIs (`long` range), and transport lifetime.
- Added a regression test round-tripping a block at a 5 TiB offset to prove the protocol does not truncate offsets above 20 GiB. The test does not allocate or transmit a 5 TiB file. Existing native file fixtures remain 37 bytes and 5,243,053 bytes and are test sizes only, not product caps.
- `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug`: **14/14** passed, including native-backed file fixtures and 25 lifecycle cycles (8.36s). `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj -c Debug --no-restore`: passed with 0 warnings/errors.
- NOT VERIFIED: a very large physical file transfer, disk-full mid-transfer, and maximum `long` boundary. These depend on filesystem capacity and are not proven by the 5 TiB offset-only codec test.

### Authenticated LAN signaling transport checkpoint (2026-11-14)

- Added `webrtc-signal` RPC messages to the existing mutually authenticated TLS listener used by LAN transfer. It reuses the discovered TCP port, server identity certificate and per-peer trusted certificate validation; it adds no new listener or discovery port and leaves the existing `offer` LAN file-transfer branch intact.
- Incoming signaling is keyed by the authenticated certificate's device ID and canonical session GUID, not a sender-provided device identity. Routes support bounded queues and independent sessions; supported message kinds include offer, answer, candidate, accept/reject, cancel, closed and error. Candidate ordering is handled by the session route consumer.
- Added outbound mTLS signal publishing and a `WebRtcSignalingHub.IncomingSession` notification. Integration test uses two independent `LocalTransferService` listeners, reciprocal trusted device certificates, and actual TCP/TLS exchanges in both directions; it verifies session/payload routing. This is a real LAN signaling transport test, not a mock relay.
- `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug`: **15/15** passed, including signaling delivery, 37-byte and 5,243,053-byte native-backed file blocks, and 25 native lifecycle cycles. `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj -c Debug --no-restore`: passed with 0 warnings/errors.
- **Production file-session coordinator and UI are still NOT IMPLEMENTED.** The signaling endpoint now exists, but the user flow does not yet create peers from signaling events or bind incoming consent/progress controls. No full application-level WebRTC file transfer is claimed.
- Signaling currently reaches devices advertised by LAN multicast. It does not provide public Internet rendezvous, STUN/TURN configuration or NAT traversal. Those require external network infrastructure/configuration and are not simulated here.

#### Immediate Critical Path

1. Implement the production session coordinator over `WebRtcSignalingHub`, handling offer/decision, SDP, ICE-before-description buffering, channel lifecycle, per-session file transfer, cancel and terminal state.
2. Bind coordinator request/progress/cancel events to the existing transfer list and consent UI while keeping the LAN send action available.
3. Run a two-process UI scenario over the mutual-TLS signaling path; test reject, cancel, disconnect, concurrent sessions and small/large file integrity.
4. Separately add real Internet rendezvous/STUN/TURN deployment before claiming cross-network availability.

### Production WebRTC coordinator and NOVA UI integration (2026-11-14)

- Implemented `WebRtcTransferCoordinator` and made `LocalTransferService` own it. Outgoing offers use the selected trusted discovery record and the existing mutual-TLS signaling RPC. Incoming offers are matched against the authenticated certificate's device ID, the session GUID, the local recipient ID, offer expiry, trusted discovery fingerprint, and safe destination name before the consent event is raised.
- User acceptance is required before the sender starts SDP negotiation or sends file bytes. The coordinator creates the native peer/channel, exchanges SDP and trickled ICE through the existing TLS signaling route, queues candidates received before remote SDP, and connects the established DataChannel to `WebRtcFileBlockTransfer`. The existing bounded frame/ACK path still flushes receiver data before acknowledging; file data remains streamed with 64-bit size/offset values and no application total-size cap.
- Added bounded per-session signal/failure handling, timeouts, sender/receiver rejection, cancellation and terminal states. Native peer/event loops are supervised; session cleanup is guarded to make close idempotent. Offer/accept/reject/SDP/ICE/cancel/closed/error messages are scoped to authenticated peer ID and session ID.
- Integrated WebRTC into the existing transfer screen: a separate WebRTC send action preserves the existing LAN send action; incoming WebRTC offers appear in the same accept/reject list and generate a tray notification; progress, result, error, reject and cancel states appear in the existing activity list. The cancel button acts on the selected active WebRTC activity. The native DLL is copied to Windows client build and publish outputs beside the executable.
- Added a native-backed coordinator integration test to the existing harness. It starts two independent `LocalTransferService` instances with real multicast discovery, reciprocal trusted certificates and production mTLS signaling. With actual `nova_webrtc.dll` PeerConnections and DataChannel, it verifies user-consent flow, SDP/ICE, 262,235-byte file reception, byte-for-byte identity and SHA-256. It also verifies rejection creates no destination file, and both sender-initiated and receiver-initiated cancellation during approval terminate both peers without creating a file.
- Final checks: `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj -c Debug --no-restore` succeeded with **0 warnings and 0 errors**; the x64 DLL (6,201,344 bytes) was present in Debug output. `dotnet publish src/Nova.WindowsClient/Nova.WindowsClient.csproj -c Release --no-restore` succeeded and included the DLL in `bin/Release/net10.0-windows/publish/`. The full `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug` completed **16/16**. It includes native-backed small/5 MiB block tests and 25 peer lifecycle cycles (8.19 s); the production-route coordinator case passed for accept, reject and cancellation from both sides while approval was pending.
- LAN transfer implementation was not changed. The existing LAN integration case remains in the passing harness.
- **Still NOT VERIFIED:** an interactive two-device WPF session with a human clicking consent, cancel during active file-block transfer, concurrent simultaneous user sessions, disk-full/disconnect during an active transfer, multi-gigabyte physical files, and a clean publish launch on another Windows machine. The current coordinator sends multiple selected files sequentially as separate transfer IDs and consent prompts. The service-level end-to-end test proves real discovery/signaling/native file transfer but does not claim visible two-window UI automation.
- **Network scope:** this route works for peers reachable through existing LAN multicast discovery and their advertised listener port. STUN/TURN, public rendezvous and NAT traversal remain unavailable; no Internet connectivity claim is made.

#### Current Critical Path

1. Run the built/published client on two separate Windows devices and verify the visible consent, progress, completion and tray notification flow.
2. Extend end-to-end coverage for cancellation after block transfer starts, connection loss during transfer, simultaneous sessions, and a large physical file constrained only by filesystem/transport limits.
3. Validate the release publish artifact on a clean Windows machine, including runtime loading of the packaged x64 native DLL.
4. Add and deploy STUN/TURN or Internet rendezvous only if cross-network support is required; it is outside the verified LAN path.

### Internet Mode implementation checkpoint (2026-11-14)

#### Implemented

- Added `Nova.RendezvousProtocol`, a shared WSS frame, presence, ICE credential and device-signed signaling contract.
- Added a deployable ASP.NET Core `Nova.RendezvousService`. It requires TLS PFX configuration, proves device-key possession with a fresh challenge, derives device IDs from the existing NOVA certificate, pins device certificates persistently, exposes only reciprocal trusted contacts, routes signed per-session control messages, limits per-IP connection creation and per-connection requests, and bounds WebSocket frames, queues and offline control offers. SDP/ICE are not queued for offline devices. The service never accepts file data.
- Added `InternetRendezvousClient`: WSS connection/reconnect, heartbeat, trusted presence, signature/freshness/replay validation and temporary ICE credentials. Production TLS uses normal platform CA/name validation; a fingerprint-pinned certificate option is available for private/test endpoints.
- Internet presence is marked offline immediately when the WSS transport drops; the client reconnects with bounded exponential delay and refreshes its roster after registration.
- Connected the client and presence to `LocalTransferService`. A single device row merges LAN and Internet sources; independent online flags preserve LAN status if the rendezvous connection is down. Existing LAN signaling and file-transfer path remains available and is used first when reachable; failed LAN signaling can fall back to an online Internet route.
- Added remote trust onboarding in the existing transfer UI: each device can copy a `NOVA1` pairing code containing its existing Device ID and certificate fingerprint, and the other user can add it to the existing trust store after exchanging it over a trusted channel. A Device ID cannot be rebound to a different certificate without first removing the previous trust. The rendezvous contact list is still reciprocal and limited to trusted peers.
- Connected Internet-provided STUN/TURN URIs to the existing `WebRtcTransferCoordinator` and native `nova_peer_create_ex` ICE policy. Default WebRTC policy prefers direct candidate pairs and allows ICE relay fallback; relay-only can be selected at the native peer API. The coordinator reports whether the selected pair is direct or TURN.
- Enabled libdatachannel's libnice backend (`USE_NICE=ON`) and pinned libnice 0.1.22 in the native vcpkg manifest to provide TURN TCP/TLS support in addition to UDP. OpenSSL remains pinned at 3.5.4.
- Added a Compose deployment for the Rendezvous Service and pinned `coturn/coturn:4.18.0-alpine`, with coturn REST temporary credentials, UDP/TCP/TLS listeners and a configurable bounded relay-port range. See [Internet deployment instructions](../infra/internet/README.md). No credentials or certificate files are committed.
- Added an integration case to the existing test harness that launches the real Rendezvous Service with ephemeral TLS, connects two independent `InternetRendezvousClient` instances, checks mutual trusted online presence, obtains temporary TURN URLs, and verifies signed WSS signaling in both directions.

#### Actual verification

- `dotnet build src/Nova.TransferEngine/Nova.TransferEngine.csproj -c Debug`: passed, 0 warnings/errors.
- `dotnet build src/Nova.RendezvousService/Nova.RendezvousService.csproj -c Debug`: passed after the registration-order, heartbeat-timeout, rate-limit and connection-lifetime fixes, 0 warnings/errors.
- `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj -c Debug`: passed after the managed Internet client, presence merge and UI changes, 0 warnings/errors.
- `dotnet build tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug`: passed, 0 warnings/errors.
- Harness run reached **15 passing cases**, including the actual WSS rendezvous integration. The two remaining native-backed cases failed because the clean native rebuild had removed `build/native-x64/Release/nova_webrtc.dll` and the replacement was not yet complete. This run is therefore **not a passing full suite**; the native coordinator case timed out awaiting events and the native API test reported `DllNotFoundException`.
- Native x64 rebuild status: the first attempt reached the libiconv install step and exposed the vcpkg package path containing spaces. A second clean attempt uses a pinned vcpkg checkout and tool under `%TEMP%`, with buildtrees and installed artifacts outside paths containing spaces. It is currently compiling libiconv; `nova_webrtc.dll` has not yet been produced by this retry. Do not treat prior DLL output as rebuilt with libnice.
- Docker/Compose validation was not run: the Docker CLI is not installed in the current environment.

#### Not verified / external requirements

- Public cross-network discovery/signaling and user-to-user transfer are **NOT VERIFIED**. No public DNS name, server host, trusted TLS certificate or firewall/security-group access was provided. The repository now contains a runnable backend and deployment configuration, but they have not been deployed.
- STUN server reachability, direct ICE across independent NATs, coturn UDP allocation, TURN TCP/TLS fallback, relay-only candidate selection, and actual Internet file transfer are **NOT VERIFIED**. Temporary credential issuance is covered by the local WSS test; a configured TURN service is not.
- Interactive two-device WPF behavior over Internet, online/offline recovery under an actual WAN outage, relay capacity, persistent-pin backup/restore and malicious-client load behavior remain **NOT VERIFIED**.
- Required deployment resources and firewall ports are listed in `infra/internet/README.md`: public DNS/TLS, TCP 443, UDP/TCP 3478, TCP 5349, and the selected UDP relay range. Public Internet/NAT support must not be claimed until the native rebuild and cross-network direct plus TURN tests pass.

#### Current Critical Path

1. Finish the clean Windows x64 vcpkg/libnice + libdatachannel + `nova_webrtc.dll` build. Verify PE x64 architecture and ABI exports, then rerun all native-backed tests against that exact DLL.
2. Validate the Compose configuration/build on a Docker host, deploy with operator-provided DNS/TLS/firewall resources, and confirm `/healthz` and WSS registration.
3. Run two independent NOVA instances on different networks. Verify authenticated presence, incoming consent, SDP/ICE, direct candidate selection, DataChannel file transfer, checksum and safe save.
4. Exercise an enforced relay-only WebRTC run through coturn and a real NAT-restricted path; verify TURN TCP/TLS where the client network requires it.
5. Validate reconnect, offline route, rejected request, cancellation, connection loss and concurrent sessions in the deployed Internet path. Preserve the existing LAN cases throughout.

### Native toolchain remediation checkpoint (2026-11-15)

- **Root cause of the failed manifest install identified:** the prior retry isolated vcpkg’s tool, buildtrees and installed tree under `C:\Temp\nova-webrtc-vcpkg`, but left `VCPKG_DOWNLOADS` under the repository path `C:\Users\NIIOIY\Desktop\Nova Taskbar\...`. Autotools/MSYS2 then executed tools from a path containing a space. `libiconv` completed in the isolated retry; the next Autotools dependency, `libffi`, failed at `make` with `/usr/bin/bash: line 1: C:/Users/NIIOIY/Desktop/Nova: No such file or directory`. The original `libiconv` failure was the same class of defect, not a source/compiler failure.
- Updated `scripts/build-native.ps1` so **all** vcpkg mutable/runtime paths — tool, buildtrees, installed tree and downloads (including CMake/MSYS2) — use the no-space temporary dependency root. The invocation now also passes the selected vcpkg root explicitly, preventing a developer-shell `VCPKG_ROOT` from changing tool resolution.
- The native DLL currently exists only in prior Windows-client Release/publish output; `build/native-x64/Release/nova_webrtc.dll` is absent, so tests built from the current tree cannot load the native transport. The managed test harness’s prior complete exception was not persisted anywhere in the repository. The retained result identifies the concrete load failure as `DllNotFoundException` for `nova_webrtc`; the coordinator’s timeout was a secondary symptom of that missing DLL, not evidence of a WebRTC negotiation defect.
- **NOT VERIFIED:** rerunning vcpkg, native CMake, managed builds and harness is blocked in this session before process launch by the managed Windows sandbox. The script fix was statically checked, but has not been represented as a successful native build.

#### Critical Path at this checkpoint

1. Run `./scripts/build-native.ps1`; confirm the manifest completes without any repository path in vcpkg’s tool/download/build logs.
2. Verify `build/native-x64/Release/nova_webrtc.dll` architecture and exports, then build the Windows client and execute the full harness from a clean output folder.
3. Treat native coordinator and lifecycle results as valid only after the rebuilt DLL is copied beside the harness executable; then continue deployed WSS/direct-ICE/TURN acceptance tests.

### Post-baseline stabilization — current verified state

This section supersedes earlier NOT VERIFIED notes where the same build, test, or runtime checks are now recorded as passed.

#### Native build

- `scripts/build-native.ps1` completed successfully for Windows x64 after correcting the Release `libffi` selection in `CMakeLists.txt`. Release now links the configuration-specific `lib/ffi.lib`, rather than the Debug `debug/lib/ffi.lib`.
- The prior `LNK4098` cause was the Debug archive's `/DEFAULTLIB:LIBCMTD` directive entering the Release link alongside Release dependencies using `LIBCMT`. The post-fix native build completed without `LNK4098`; no CRT warning suppression or forced runtime override was added.
- `build/native-x64/Release/nova_webrtc.dll` was verified as x64 PE32+. `dumpbin /exports` reports 12 exports: `nova_peer_add_candidate`, `nova_peer_close`, `nova_peer_close_channel`, `nova_peer_create`, `nova_peer_create_answer`, `nova_peer_create_channel`, `nova_peer_create_ex`, `nova_peer_create_offer`, `nova_peer_get_selected_candidate_pair`, `nova_peer_send`, `nova_peer_set_remote_description`, and `nova_webrtc_abi_version`.
- Third-party usrsctp warnings remain: C4319 ×3 and C4703 ×2. They were not disabled or patched as part of this stabilization.

#### Managed builds, tests, and publish

- Windows client and test harness Debug and Release builds completed with 0 errors and 0 warnings.
- `Nova.TransferEngine.Tests` passed **18/18** in Debug and **18/18** in Release, including the native-backed scenarios.
- `dotnet publish src/Nova.WindowsClient/Nova.WindowsClient.csproj -c Release --no-restore` completed successfully. The publish output contains `nova_webrtc.dll`.

#### WPF runtime

- The Debug build and Release publish both launched and displayed the expected `Панель NOVA` window.
- The window's hide control removed the window while the corresponding process remained running with no main-window handle, consistent with the tray-resident lifecycle and `ShutdownMode="OnExplicitShutdown"`.
- A hidden Debug instance held the named `NOVA.TaskBar.SingleInstance` mutex, so an initial Release launch did not create a second window. After cleaning up that test instance, the Release publish launched normally.
- The initial Release manual acceptance confirmed that the tray icon appeared and double-clicking it restored the hidden window.
- The old Release build did not exit after `Завершить NOVA`; PID 29664 remained alive. Code-path analysis identified a UI-dispatcher deadlock: `OnExit` synchronously blocked on `LocalTransferService.DisposeAsync`, while the TCP accept loop had been started on the WPF dispatcher and awaited listener shutdown through that same dispatcher.
- Shutdown now awaits `LocalTransferService.DisposeAsync` asynchronously from the tray command before calling `Shutdown()`. `OnExit` no longer blocks on async disposal; it releases the tray icon/menu and mutex. `ShellWindow.AllowClose` also stops both dispatcher timers.
- The fixed Release publish at `build/shutdown-validation/publish/Nova.TaskBar.exe` was manually tested: double-click restore succeeded, the tray-menu exit returned, and a process check confirmed that executable was no longer running.
- **Repeated launch/exit cycles are NOT VERIFIED:** the manual report did not include a cycle count beyond the single successful shutdown. No forced process termination was used to claim a successful exit; the stale PID from the old build was stopped separately with explicit approval only to release the mutex for testing.

#### Native WebRTC rebuild follow-up

- `scripts/build-native.ps1` completed successfully and produced `build/native-x64/Release/nova_webrtc.dll` (10,022,400 bytes). The DLL is x64 PE32+ and exposes the 12 ABI functions listed above.
- The rebuilt DLL SHA-256 is `9866ACCA9367ABE80308FD28C886D960609CDFC6B20DEA22411D0E7495FB4EF8`. Its copies in the Windows Client Release output and the test harness Debug and Release outputs matched that hash.
- The Windows Client Release build and the harness Debug and Release builds completed with 0 errors and 0 warnings.
- The full harness was run with `--no-build` after the hash checks: **18/18 passed in Debug and 18/18 in Release**, including native WebRTC offer/answer, ICE, bidirectional DataChannel transfer, verified small and large file payloads, and 25 sequential peer lifecycle cycles per run.
- The negative test for an unknown client certificate logs the expected `AuthenticationException` rejection while the test itself passes. The native build still reports upstream usrsctp C4319 ×3 and C4703 ×2 warnings; no warning suppression was added.

#### Not verified and active Critical Path

1. Perform at least two more sequential launch/restore/exit cycles and confirm no NOVA processes remain after each exit.
2. Validate the Release publish and packaged native DLL on a clean Windows machine.
3. Deploy the Internet service with operator-provided DNS/TLS/firewall resources, then test direct and TURN-relayed transfer between independent networks. No deployment or cross-network acceptance is claimed.

### Dynamic Island implementation checkpoint (2026-11-16)

#### Implemented in the Windows client

- Replaced the former panel surface with a single WPF surface containing Compact, Hover and Expanded views. Expanded modules remain within the same window; the default dock edge for new settings is the top edge.
- Added Windows Media Session access for session metadata, artwork, playback controls and seeking. The compact audio reaction uses the Windows render endpoint's peak meter and fades when playback is inactive; it does not record audio. The media controls and seek bar are anchored in a responsive bottom row. Windows Media Session does not provide lyrics, so unavailable lyrics are shown as such.
- Added Open-Meteo geocoding with selectable locations and saved coordinates, current weather, a process-local countdown timer and stopwatch, and CPU/GPU/RAM sampling.
- Added Inci connect/disconnect actions through the vendor-documented `incy://connect` and `incy://disconnect` URI links. The status action opens INCY; its URI handler does not return VPN state, and no machine-readable status/result API or CLI was identified. NOVA does not display a fabricated connection state.
- Added a transparent, click-through gaming-overlay mode with CPU/GPU/RAM, separate saved size/position, and tray actions for moving or leaving the overlay. Runtime behavior over games, especially exclusive-fullscreen games, remains unverified.
- Removed the pale/blurred surface styling, added black capsule styling, top docking and free positioning, and constrained panel dimensions to the active display's working area.
- Saved the display device name with panel preferences so a docked or freely positioned panel can be restored to the same monitor when that display is available. If it is unavailable, the window falls back to the display containing its current position.
- Preserved the Atoll MIT license and added third-party attribution; no Atoll branding was added to the NOVA UI.

#### Verified

- Windows Client Debug and Release builds passed after the responsive music-layout change: **0 errors, 0 warnings**.
- The console harness (not a `dotnet test` project) passed **18/18 in Debug and 18/18 in Release**, including native WebRTC transfer and 25 sequential peer lifecycle cycles per run.
- Open-Meteo geocoding and current-forecast endpoints returned real results for a smoke-query location. The running UI also displayed current weather from Open-Meteo.
- A self-contained x64 Release publish completed at `build/NOVA-Taskbar-Release-v2/publish/` with 263 files. Its `Nova.TaskBar.exe` and `nova_webrtc.dll` are x64 PE images. The packaged native DLL is 10,022,400 bytes and its SHA-256 matches the current native build; the export table contains the 12 ABI functions documented above.
- The single desktop shortcut `NOVA Taskbar.lnk` was created/updated and its target and working directory were verified to point to `build/NOVA-Taskbar-Release-v2/publish/Nova.TaskBar.exe`.
- An earlier self-contained Release instance launched and displayed the compact and expanded WPF surfaces. The final music-layout change was built and published afterward, but its GUI was not opened.

#### Not verified / remaining Critical Path

1. **Final GUI is NOT VERIFIED.** The current single-instance process was being actively used when the final publish was prepared, so it was left running rather than closed. It was launched from the earlier `build/NOVA-Taskbar-Release/publish/` directory; while it holds the mutex, launching the final shortcut can be forwarded to that older process. After that instance exits normally, launch `NOVA Taskbar.lnk` and verify the final `build/NOVA-Taskbar-Release-v2/publish/Nova.TaskBar.exe`.
2. Verify the final music layout at the saved panel scale: album/metadata, visible previous/play/next controls, and seeking. Test supported media controls against a real playback session.
3. Verify Compact/Hover/Expanded transitions, click-outside behavior, tray actions, timer controls and shutdown/restart cycles in the final build.
4. Verify gaming-overlay transparency, input pass-through, movement, telemetry refresh and behavior over windowed/borderless and exclusive-fullscreen games; verify placement/restoration across physical monitors and mixed-DPI displays.
5. Inci URI commands are implemented, but actual VPN connection/disconnection and authoritative status remain **NOT VERIFIED**. The official status URI only opens INCY and does not report state; do not infer success from launching a URI.

### Direct VPN TCP/TLS file transfer (2026-11-16)

- Added `DirectVpn` to the persisted transport choices. NOVA reuses its existing direct TCP listener and local TLS file protocol; the sender manually supplies the selected trusted contact's numeric IPv4 address and TCP port. Saved trusted contacts remain selectable when LAN multicast and Internet rendezvous are unavailable.
- Direct VPN sends connect straight to that IPv4 endpoint and do not request Rendezvous tickets, use Cloudflare, or use the TCP/TLS File Relay. The receiver's current listener port is assigned at startup and displayed in the transfer panel.
- Authentication remains reciprocal TLS certificate pinning against the local trusted-device stores. IP and port only select the network route; they do not authorize a transfer. The receiver still approves each offer. File bytes stream through bounded 64 KiB buffers to a temporary file, with receiver-side SHA-256 verification before finalization.
- Added sender cancellation, explicit connect/TLS and approval timeouts, a two-minute network-idle timeout during Direct VPN streaming, progress reporting, and partial-file cleanup when a transfer is cancelled or the connection is lost. Transfers remain non-resumable.
- Verification on 2026-11-16: Windows client Debug build succeeded with **0 warnings and 0 errors**. The final `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug` run passed **22/22**, including a loopback Direct VPN transfer with SHA-256 comparison, cancellation and receiver-disconnect cleanup, IPv6 endpoint rejection, unknown-certificate rejection, and the existing Relay/WebRTC scenarios. One intermediate run hit a timeout at cycle 15/25 in the existing native WebRTC lifecycle stress test; the next full run completed all 25 cycles successfully.
- These are same-host loopback checks, not a Radmin VPN acceptance test. Actual Radmin routing, two-computer transfer, Windows Firewall access to the listener port, user consent, cancellation, and disconnect behavior remain **NOT VERIFIED**.

#### Direct VPN Critical Path

1. On two Windows computers, exchange NOVA device pairing codes over a trusted out-of-band channel and mutually trust both device identities.
2. Connect both computers to the same Radmin VPN network. Enter the receiver's Radmin IPv4 address and the current TCP port displayed by NOVA; verify the listener is permitted by Windows Firewall.
3. Transfer a small file and a large file; verify SHA-256, progress, accept/reject, sender cancellation, and interruption cleanup. Confirm the route is direct and no Rendezvous/Relay connection is required.

## NOVA Personal Windows Control Center — assessment and roadmap (2026-11-17)

### Executive assessment

NOVA is an existing Windows 10+ / .NET 10 WPF tray application, not a blank starter. Its recognizable black capsule, monitor-aware placement, tray lifecycle, dynamic island states, transfer engine, and several OS-backed services are useful foundations to preserve. The principal scale-up constraint is presentation architecture: `ShellWindow.xaml`, `ShellWindow.xaml.cs`, and `ShellWindow.Island.cs` form a single large window/code-behind surface, with view sections selected by visibility and event handlers. Feature services exist, but there is no general widget/module host or single composition/settings model.

The nine requested areas are at different maturity levels:

| Area | Current evidence | Gap to requested outcome |
|---|---|---|
| Capsule / audio-reactive shape | Expanded implementation: actual WASAPI loopback RMS, bass, treble and transient energy drive the existing capsule contour with attack/release smoothing; metadata, monitoring, network and local Radmin-adapter status can be selected and reordered. | DSP tests, live loopback smoke test and processing benchmark pass. Visual response and WPF render cost remain NOT VERIFIED; the capsule still belongs to the main window rather than a general widget contract. |
| Control Center panel | Foundation implemented: compact/hover states plus a left widget workspace and right-side navigation backed by an in-process `WidgetRegistry`/`WidgetHost`. | The host is currently wired to existing views in the main window; a general module lifecycle/composition and common settings migration are still future work. GUI acceptance is not yet verified. |
| Music | Partial and real: Windows Media Session metadata/artwork, play/pause, previous/next and seek when supported by the active player. | No volume control or queue/library. Yandex Music synchronized lyrics are not implemented; current UI explicitly explains that Windows Media Session does not supply lyrics. |
| Full-screen workspace | Interactive full-screen workspace mode implemented using the selected monitor's bounds, with the same workspace/navigation and Escape/F11 exit. The compact click-through gaming HUD remains separate. | Actual GUI behavior, display hot-plug, and exclusive-fullscreen game interaction remain unverified. |
| OBS | Not implemented in inspected source or direct package references. | No OBS WebSocket client, authentication, connection state, scene/source operations, or error/reconnect handling. |
| OCR | Not implemented in inspected source or direct package references. | No screen-region capture, OCR engine integration, selection UI, result editing/copying/export, or privacy/permission flow. |
| Browser adaptation | Not implemented in inspected source. | Existing Win32 usage is for NOVA's own window/overlay; no browser discovery, window selection, resize/placement, or restore workflow. |
| Monitoring | Partial and real: CPU/RAM/GPU plus active IPv4 adapter throughput and local Radmin VPN adapter state; the capsule can select these indicators. | Disk metrics, history/trends, device-level GPU normalization, and a fallback provider are absent. The GPU counter can be unavailable and is displayed as unavailable rather than fabricated. |
| Settings | Partial: existing General, Island, Weather, Gaming, Monitoring and Appearance pages; settings persist. | Preferences are distributed across `PanelPreferences`, transfer settings and Internet settings stores. There is no common module schema, validation/migration lifecycle, or central settings experience for future modules. |

### Current architecture and constraints

- `Nova.WindowsClient` targets `net10.0-windows10.0.19041.0`, WPF and WinForms, declares Windows 10 build 19041 as its supported OS floor, and directly references NAudio 3.3.0. The native WebRTC DLL is copied conditionally from the repository build output.
- `Nova.Core.Contracts` holds a small number of cross-component contracts. `Nova.TransferEngine`, `Nova.TransferProtocol`, `Nova.RendezvousProtocol`, and `Nova.RendezvousService` are separate projects; these transfer/network components should remain intact and not become UI-module dependencies.
- WPF composition is manual: `App` creates the transfer service and `ShellWindow`; the window owns media, audio meter, stats, timer, weather, and UI lifecycle. The current shell now has an in-process `WidgetRegistry`/`WidgetHost` for its existing views, but future modules still need broader service ownership, initialization/disposal, UI-thread dispatch, and error state.
- The UI already handles multiple displays and PerMonitorV2 DPI for NOVA's own panel. Its `SetWindowLongPtr`/`SetWindowPos` behavior applies to the NOVA window only; it is not a general window-management service and does not establish safety for manipulating unrelated browser windows.
- Existing `SystemStatsService` uses PDH for CPU and GPU Engine counters and `GlobalMemoryStatusEx` for RAM; it now samples aggregate active IPv4 adapter throughput and local Radmin adapter state, but not disk activity. Audio reactivity samples the render-endpoint loopback mix while the capsule is visible and the option is enabled; it does not record audio.
- Music control uses `GlobalSystemMediaTransportControlsSessionManager`. This is a system media-control/metadata surface, not a general player API: support for seek/skip depends on each player. Volume needs a distinct Windows audio-session/endpoint design and explicit target selection; it must not be presented as a universally supported media-session command.
- The transfer test project is a dependency-light console harness, not a `dotnet test` / WPF UI test suite; its latest recorded transfer verification is 22/22 on 2026-11-16. A separate Windows client console harness now covers capsule DSP inputs and a bounded analyzer throughput check, but neither harness verifies the rendered WPF UI or live audio device behavior.
- The existing plan contains historical snapshots with earlier NOT VERIFIED statements superseded by later entries. Keep new acceptance results in a dated current section and distinguish recorded historical evidence from checks run for a future change.
- A web-documentation fetch for Microsoft, OBS, and Yandex endpoints was blocked in the analysis environment. Accordingly, the exact supported OBS protocol/version, OCR language/runtime packaging behavior, and an authorized Yandex synchronized-lyrics API/terms are not claimed as verified here. Resolve these before committing to implementations that depend on them.

### Target architecture

1. **Keep the WPF and capsule foundation.** Do not replace the application shell or migrate UI frameworks. Preserve the current black styling, tray lifecycle, monitor/DPI logic, and existing services while moving one feature boundary at a time.
2. **Add a composition root and a small compiled-widget contract.** A widget/module should declare stable ID, display metadata, capabilities, lifecycle (`initialize`/`activate`/`deactivate`/`dispose`), a view-model/service boundary, view, settings section, and failure/permission status. Begin with in-process compiled modules; do not add arbitrary dynamic plugin loading.
3. **Separate shell, workspace, navigation, and modules.** Keep the compact capsule as the entry point; introduce one workspace host that displays the selected module on the left and shared navigation on the right. Reuse/move current module views into the host incrementally rather than duplicate their logic.
4. **Centralize settings without a destructive migration.** Define typed, validated, versioned module settings and one save/migration boundary under the existing LocalAppData application state. Read and migrate current `settings.json`, transfer settings, and Internet settings before switching writes; retain existing values and report persistence errors.
5. **Isolate Windows and vendor integrations.** Put WASAPI, PDH, media-session, capture/OCR, monitor/window, OBS WebSocket, and any Yandex API behind narrow service interfaces. Keep WPF controls on the dispatcher; perform network, OCR and metric work asynchronously with cancellation, bounded resources, and explicit unavailable/permission/error states.
6. **Use measurable module acceptance.** Each module needs core/service tests with controlled inputs, a Windows integration check against the actual OS/vendor endpoint, and UI scenarios for permissions, no-data/offline state, errors and disposal. Test click-through, mixed DPI and display changes on real Windows configurations.

### Ordered implementation plan and estimates

Estimates are one experienced Windows/.NET developer's engineering time, excluding external account/credential wait and cross-machine scheduling. Some post-foundation modules can proceed independently, but the critical path is the shell/settings foundation and user acceptance.

| Stage | Scope and dependency | Estimate | Main risks / exit criteria |
|---|---|---:|---|
| 0. Product/API feasibility | Confirm screens, module scope, privacy, exact volume target, OCR languages/export, OBS actions, browser ownership/restore, and Yandex Music account/API/legal route. Prototype only as a bounded technical spike after this analysis is approved. | 3–5 days | **Lyrics is a feasibility blocker:** no supported/authorized synchronized-lyrics API was confirmed. Exit with documented API/auth/terms and fallbacks; otherwise mark lyrics blocked rather than scrape or evade access controls. |
| 1. Module and settings foundation | Establish composition/lifecycle and module metadata/view-model conventions; add schema-versioned settings/migration and common error/capability handling. Keep existing preferences readable and migrate them without resetting values. | 2–3 weeks | Highest architectural leverage. Risks include breaking saved settings, duplicate timers/event handlers and shutdown leaks. Exit with current capsule/music/weather/system/transfer sections still operating through the new host boundary and migration tests. |
| 2. Workspace and navigation | **Implemented as a shell foundation (2026-11-17):** selected module work area on the left and navigation on the right, retaining compact/hover states and persistence. | Complete for this increment | Responsive sizing, keyboard/focus, hide/restore, and multi-monitor GUI acceptance are still NOT VERIFIED. |
| 3. Capsule feature completion | **Implemented as a capsule increment (2026-11-17):** WASAPI level/bass/treble/transient analysis, smoothed contour/glow, content toggles and layout presets; audio analysis remains off the UI thread. | Implemented; acceptance pending | DSP tests and benchmark pass. Confirm actual audio playback, silence return, mixed display scaling and whole-UI frame/CPU cost on the running NOVA shell. Module-contract migration remains separate. |
| 4. Full-screen workspace | **Implemented as a shell foundation (2026-11-17):** interactive workspace on the selected monitor with Escape/F11 return; the gaming click-through HUD remains separate. | Complete for this increment | Focus capture, taskbar/z-order, exclusive-fullscreen and display hot-plug behavior vary by Windows/app and remain NOT VERIFIED. |
| 5. Music controls and lyrics feasibility | First add volume only after deciding whether it controls master output or a selected app/session/device; make target and unavailable states explicit. Add synchronized lyrics only if Stage 0 proves an authorized supported source. | 1–2 weeks volume; lyrics 2–4+ weeks conditional | Volume API behavior differs by endpoint/session and may affect other applications. Lyrics auth, rate limits, synchronization, language/catalog coverage, service terms and DRM are unresolved; no scraping/private API workaround. Exit with real supported player tests and explicit no-support behavior. |
| 6. Monitoring expansion | Add disk metrics and refine the new aggregate network sampling behind a provider that identifies disks/adapters, handles counter resets, and exposes units/sample timestamps. Keep the existing CPU/RAM/GPU provider until replacement is justified. | 1–2 weeks | PDH counter localization/availability, multi-GPU and adapter selection, virtual disks, sleep/resume, sampling load. Exit with multiple-device samples, absent-device/error states, and no fabricated zero values. |
| 7. OBS integration | Add an optional OBS WebSocket adapter with explicit endpoint/port, authentication handshake, secure secret storage, connection lifecycle and a bounded set of requested real commands/state. Use the installed/officially supported OBS WebSocket version determined in Stage 0. | 1–2 weeks | Version mismatch, password handling, OBS stopped/restarted, local firewall, command permissions and user consent. Do not log secrets. Exit with tests against a real local OBS instance, auth failure and reconnect. |
| 8. OCR | Add a user-invoked region selector and Windows capture/OCR adapter, then result review/copy/export. Make capture and data retention visible; do not capture in background. | 1–2 weeks | Windows.Graphics.Capture/WinRT interop, OCR language packs (often dependent on installed Windows languages), mixed DPI, protected content and privacy. Exit with supported-language checks and tests for cancel/no-text/recognition error. |
| 9. Browser adaptation | Only after windowing design review, add explicit browser/window opt-in, preview, placement rules around the capsule, and restore of original bounds/state on disable/exit. | 2–3 weeks | Third-party windows/tabs, multiple profiles, DPI/monitor changes, fullscreen/maximized windows, user races and potentially elevated processes. Do not enumerate/reposition all windows implicitly; never leave browser windows displaced after NOVA exits. |
| 10. Integration and release acceptance | Consolidate settings UX, accessibility/keyboard/focus, dark visual consistency, diagnostics, update/docs, regression and clean publish. Run module lifecycle/long-session tests and actual Windows UI scenarios. | 1–2 weeks | Cross-module settings/lifecycle defects and OS variations. Exit only on clean build, service tests, actual OBS/player/capture checks, and release GUI verification; record untested hardware/OS cases as NOT VERIFIED. |

**Overall planning envelope:** approximately **12–18 engineering weeks** to deliver and validate the foundation plus the non-blocked modules sequentially. Yandex synchronized lyrics have no committed delivery date until the feasibility stage finds a supported, authorized source; any API/terms or authentication blocker can exclude that feature without blocking the Control Center.

### Critical Path

1. Approve module UX, permissions, browser behavior and observable acceptance criteria; resolve media volume semantics and run the external API/terms feasibility spike.
2. Build the in-process module lifecycle/composition and backward-compatible settings migration while preserving the existing capsule and services.
3. **Implemented:** deliver the left-workspace/right-navigation host; remaining work is GUI acceptance and lifecycle proof for existing modules.
4. **Implemented as a shell foundation:** add full-screen behavior; full OS/UI acceptance and module migration through a broader module contract remain.
5. **Implemented as a capsule increment:** add selectable audio bands, real compact system/network/VPN data, and persisted content/layout settings; perform GUI acceptance before moving to later module stages.
6. Implement OBS and OCR behind isolated, optional adapters with explicit authentication/consent/errors. Proceed with Yandex lyrics only if Stage 0 yields an authorized API.
7. Add browser placement last, with opt-in and restoration guarantees.
8. Complete full UI/integration/release verification and document real-device/driver/API NOT VERIFIED cases.

### Capsule system implementation checkpoint (2026-11-17)

**Implemented**

- Replaced the bass-only, media-session-gated response with WASAPI loopback analysis that runs while the capsule is visible and audio reactivity is enabled. It measures overall RMS level, a low-pass bass band, a high-pass treble band, and fast level/peak rises; silence and capture shutdown feed zero into smoothed envelopes.
- Drives the existing pill/capsule contour from those real band levels: volume creates broad movement, bass creates low-order organic lobes, treble adds smaller high-order ripples, and transient energy creates a short pulse. Attack/release smoothing and phase advancement are deterministic; no random animation or media playback assumption is used.
- Improved the capsule surface with a graphite vertical gradient, restrained gold contour glow and a fuller outline while preserving the original silhouette. The display refresh remains timer-driven at about 30 Hz; audio decoding/analysis remains on the WASAPI callback thread.
- Added persisted visibility controls for clock/date, weather, track title/artist, available cover art, playback status, CPU/GPU/RAM, network throughput and the local Radmin VPN adapter. Added four compact-block layout presets. Full track/artist values remain available via text and hover tooltips; stationary decorative audio bars were removed.
- Added network receive/send rates from active IPv4 adapter counters. The VPN indicator reports only whether a Radmin VPN adapter is present/up with IPv4; it does not claim that a peer or remote network is reachable.

**Verification**

- `dotnet build src\Nova.WindowsClient\Nova.WindowsClient.csproj --no-restore` — passed, 0 warnings and 0 errors.
- `dotnet run --project tests\Nova.WindowsClient.Tests\Nova.WindowsClient.Tests.csproj -c Release` — passed 6/6: silence baseline, bass/treble separation, level response, transient decay, live WASAPI loopback startup, and analyzer throughput.
- Live WASAPI smoke test observed non-silent system audio (normalized level 0.66 in the latest run); the test only computes bands and does not record or save audio.
- Analyzer benchmark: 30 seconds of synthetic 48 kHz samples processed in approximately 31 ms (about 0.1% of the represented audio duration in the latest run). This measures the DSP analyzer only, not WPF rendering.
- **Actual playback-to-visible-contour response, full capsule render CPU/frame time, and GUI settings/layout behavior: NOT VERIFIED.** The already-running NOVA process is an older single-instance published build; it was not closed or replaced. The synthetic signals verify DSP behavior, while the smoke test verifies loopback capture availability only.

**Remaining Critical Path**

1. Verify the built capsule with real system audio and silence, including music metadata/artwork and the new settings/layout controls.
2. Measure whole-window CPU/frame behavior on the updated shell, then resolve any issues before starting later roadmap stages.

### Workspace and full-screen implementation checkpoint (2026-11-17)

**Implemented**

- Added an in-process `WidgetRegistry`/`WidgetHost` and registered the existing Overview, Music, Weather, VPN, Timer, System, Transfer, History, and Settings views. Selection controls the left workspace and right navigation without duplicating those views.
- Reshaped the expanded panel into the selected widget workspace on the left and widget navigation on the right; retained the existing compact capsule and transient hover state.
- Added an interactive full-screen state that fills the selected monitor and uses the workspace, with Escape/F11 return to the expanded workspace. It does not replace the separate gaming click-through HUD.
- Persisted selected widget and expanded workspace size, position, and monitor alongside existing capsule preferences. Added target-monitor DPI-aware bounds restoration and clamps saved size/position to the selected display's usable area.
- Added workspace resizing and responsive navigation sizing. This is a first shell foundation, not completion of the planned general module lifecycle, centralized settings migration, or other roadmap features.

**Verification**

- `dotnet build src\Nova.WindowsClient\Nova.WindowsClient.csproj --no-restore` — passed, 0 warnings and 0 errors.
- `dotnet run --project tests\Nova.TransferEngine.Tests\Nova.TransferEngine.Tests.csproj -c Debug --no-restore` — passed, 22/22 checks. This is the existing transfer regression harness and does not exercise the WPF workspace.
- `git diff --check` — passed; Git reported existing LF-to-CRLF working-copy warnings.
- **GUI, mixed-DPI/multi-monitor behavior, display hot-plug, hide/restore, keyboard focus, and fullscreen game interaction: NOT VERIFIED.** The GUI was not exercised; the running single-instance application was an older published build, so it was not closed or replaced during this change.

**Remaining Critical Path**

1. Manually verify the updated shell on Windows, including module switching, resize and persistence, restore/hide, Escape/F11, multiple monitors and mixed DPI.
2. Address any findings before starting subsequent roadmap stages. No later feature stage is part of this implementation.

## Release v3 publication and desktop shortcut (2026-11-17)

- Superseded by the UX refresh and v4 publication recorded below; retained as release history.
- Published the current Windows client as a self-contained `win-x64` Release to `build/NOVA-Taskbar-Release-v3/publish/`.
- `dotnet publish src/Nova.WindowsClient/Nova.WindowsClient.csproj --configuration Release --runtime win-x64 --self-contained true --output build/NOVA-Taskbar-Release-v3/publish` completed successfully. The publish contains 490 files and targets `.NETCoreApp,Version=v10.0/win-x64`.
- The published `Nova.TaskBar.dll` SHA-256 is `0F9DD024D300A0202086B5073C53BE0EA92D184CE83DB60C68EE5F9AB45AC58A`, identical to the freshly built `win-x64` Release DLL and different from the older v2 publication. `nova_webrtc.dll` is present and matches `build/native-x64/Release/nova_webrtc.dll` (SHA-256 `D20B95A4BD449CE7E444F9A5C491F1775A24CFB90CC481EBC5E6628F6652BB3F`).
- Updated `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk`; its target, working directory, and icon now point to the v3 publish directory. Shortcut properties were read back and verified.
- The previous `NOVA-Taskbar-BeatReactive-Release` process (PID 16756 at verification) was left running and was not replaced. **GUI/runtime behavior of v3 is NOT VERIFIED**; after the old NOVA instance is closed, launch the desktop shortcut to start v3.

## Music layout and capsule response refresh (2026-11-17)

- Replaced the music widget's fixed 230 px artwork/metadata column and narrow adjacent text column with a responsive artwork-and-metadata header, a full-width text/status panel, and a separate playback control row. Artwork scales with the available widget width; long track titles and artist names wrap instead of being clipped. Missing artwork/title/artist now have explicit placeholders.
- Increased the expanded workspace default to 1040×700 and minimum to 820×560 DIPs, bounded by the current monitor work area. The widget navigation is now a two-column tile grid, showing all eight registered widgets without a navigation scrollbar.
- Capsule deformation now varies along the contour rather than only by edge normal, with stronger bass waves, high-frequency ripples, and beat peaks. The orange glow responds more visibly to measured audio energy. The signal still comes from real WASAPI loopback capture; no random motion was introduced.
- The music text panel is now readable at normal sizes, but **synchronized song lyrics are NOT IMPLEMENTED**: Windows Media Session does not provide lyrics and no lyrics source is connected. The widget states this rather than displaying fabricated lyrics.

### Verification

- `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj --configuration Release --no-restore` — passed, 0 warnings and 0 errors.
- `dotnet run --project tests/Nova.WindowsClient.Tests/Nova.WindowsClient.Tests.csproj --configuration Release --no-restore` — passed **6/6**, including live WASAPI loopback (non-silent sample observed) and real-time analyzer throughput.
- `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj --configuration Debug --no-restore` — passed **22/22**. Expected transport disconnect/authentication diagnostics were emitted by the cancellation and rejection scenarios; those scenarios passed.
- `git diff --check` on the changed WPF files — passed.
- Self-contained x64 publish to `build/NOVA-Taskbar-Release-v4/publish/` completed successfully (490 files; runtime `.NETCoreApp,Version=v10.0/win-x64`, bundled .NET 10.0.14 runtimes). Published `Nova.TaskBar.dll` SHA-256: `682968EBA85F887EBBE0F74CFF969CDA917FD2DCD27D144E749E55C10BDA8A00`; `nova_webrtc.dll` matches the native Release artifact.
- Updated and read back `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk`; target, working directory, and icon point to the v4 publish folder.
- **GUI, visual scaling, and mixed-DPI/multi-monitor behavior of v4: NOT VERIFIED.** The existing single-instance process (PID 4124) was left running from v3 and was not interrupted. Close that instance before launching v4 from the desktop shortcut.

## Widget center and music integration (2026-11-17)

### Research findings and implementation

- The existing `MediaSessionService` uses Windows Global System Media Transport Controls (GSMTC). It provides title, artist, album title, thumbnail, playback status, timeline, transport commands and a seek request. It does **not** expose the active source's playback queue or a per-player volume control. NOVA now displays album metadata when provided, retains previous/next/play-pause/seek, and adds a clearly labelled Windows master-volume slider (this affects all apps).
- The selected widget now uses the existing `WidgetRegistry`/`WidgetHost`. Main navigation prioritizes Music, VPN, Timer, Monitoring, OBS and Tools; the prior Overview, Weather, Transfer, History and Settings pages remain registered. Selected-state styling remains bound to the registry; old persisted `System` selection migrates to `Monitoring`. Tools links to existing pages. OBS clearly reports that WebSocket control is unavailable because there is no OBS client or connection configuration in this project; no simulated controls were added.
- Yandex Music's lyrics/search API was investigated using the `MarshalX/yandex-music-api` project. Its own README calls the API unofficial; its client documents track search and `/tracks/{id}/lyrics` with `format=LRC`, an OAuth token, a request signature and a returned download URL. NOVA adds an optional LRC client without adding the Python library or another package. The user enters an OAuth token in a password field; it remains in process memory only and is not written to NOVA settings. Search requires an exact title and artist match before fetching lyrics. The LRC response is size-limited, downloaded only from HTTPS `*.yandex.net`, parsed into timed lines, and the token is not sent to the lyrics download URL.
- Timed lyrics display the current line, scroll with the Windows media position and attempt a GSMTC seek when a line is clicked. If the player rejects seeking, NOVA reports that limitation. Untimed text is shown without synchronization. Tracks whose Windows metadata does not exactly match a Yandex result, missing lyrics, invalid tokens, API errors and timeouts are reported rather than silently substituted.
- The Yandex API also has account-level queue endpoints, but their queue/device identity cannot be reliably associated with the active GSMTC session. The current media API therefore states that the session queue is unavailable rather than showing another device's queue as if it were current.

Research references:

- [Windows GSMTC session API](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession?view=winrt-26100)
- [Yandex Music API client and LRC implementation](https://github.com/MarshalX/yandex-music-api/blob/9ca132e5c18a72aab779c1fc68c169380a9357d0/yandex_music/_client/tracks.py)
- [Yandex Music timed-lyrics model](https://github.com/MarshalX/yandex-music-api/blob/9ca132e5c18a72aab779c1fc68c169380a9357d0/yandex_music/track/track_lyrics.py)
- [Yandex Music queue/device API](https://github.com/MarshalX/yandex-music-api/blob/9ca132e5c18a72aab779c1fc68c169380a9357d0/yandex_music/_client/queue.py)
- [Yandex Music request-signing reference](https://github.com/MarshalX/yandex-music-api/blob/9ca132e5c18a72aab779c1fc68c169380a9357d0/yandex_music/utils/sign_request.py)

### Verification and remaining limits

- `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj --configuration Release --no-restore` — passed, 0 warnings and 0 errors.
- `dotnet run --project tests/Nova.WindowsClient.Tests/Nova.WindowsClient.Tests.csproj --configuration Release --no-restore` — passed **8/8**: audio DSP, LRC timestamp/untimed-text parsing, read-only Windows master-volume query, live WASAPI loopback, and throughput.
- `dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj --configuration Debug --no-restore` — passed **22/22**; cancellation and trust-rejection cases emitted their expected transport/authentication diagnostics.
- `git diff --check` on the changed implementation/test files — passed.
- **Live Yandex authorization/API, a real matching Yandex track, and seek behavior: NOT VERIFIED** because no user OAuth token/player session was supplied for a live API run. LRC format parsing itself is covered by tests. **GUI and per-app player behavior: NOT VERIFIED**; the running single-instance process is from an earlier v4 build, not this source revision.
- OBS WebSocket control and active-session queue display remain **NOT IMPLEMENTED** for the backend/identity reasons above. Windows master-volume reading is verified; changing the master volume through the UI is **NOT VERIFIED** and affects all applications.

## OBS, Screen Text/OCR, and Browser Adaptation (2026-11-17)

### Implemented

- **OBS:** Added OBS installation discovery from uninstall registry entries and standard/Steam install paths; the installed Steam copy was detected in this environment. The OBS widget can explicitly launch OBS, connect, poll version/current scene/scene list/record status, start/stop recording in OBS, and apply a selected scene. NOVA sends no recording or scene commands until the user explicitly connects/clicks a control. It connects only to `127.0.0.1`, requires OBS WebSocket challenge authentication, keeps the password only for the connection attempt, clears the password box, never stores it in NOVA settings, and creates no firewall rules.
- The stock `obs-websocket` implementation has important security limits: its server listens on wildcard interfaces (the IPv4-only switch still binds all IPv4 interfaces), and its own configuration code stores `server_password` in OBS's JSON configuration without encryption. NOVA cannot make the stock server loopback-only or change OBS's storage behavior. The UI warns users to keep authentication enabled and restrict inbound access in Windows Firewall. OBS is not launched automatically by Connect; the user must explicitly use **Запустить OBS**.
- **Screen Text/OCR:** Added a virtual-desktop region selector using physical pointer coordinates, captures only the selected rectangle in memory, and passes it to the built-in Windows OCR engine. Available Windows OCR language packs are listed; users can select a language or use profile languages. The separate result window supports editing, copying, formatting cleanup, translation, and retaking the selection. Images are not written to disk. Translation sends text to MyMemory only after an explicit confirmation, over HTTPS, and rejects requests longer than its 500-character free-endpoint limit.
- **Browser Adaptation:** Added a persisted General-settings toggle. When enabled, Windows `EnumWindows`/window-placement APIs track top-level windows from Chrome, Edge, Firefox, Brave, Opera, Opera GX, Vivaldi and Arc. Overlapping windows are moved/resized into available monitor work area; original placement is restored when no longer needed or when the toggle is disabled. The feature does not reserve a global AppBar/work area or alter non-browser windows. It pauses when NOVA's capsule is not displayed. Some protected windows may refuse repositioning; F11/fullscreen browser windows can be resized while enabled.

### Verification

- `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj --configuration Release --no-restore` — **BUILD VERIFIED**, 0 warnings and 0 errors.
- `dotnet run --project tests/Nova.WindowsClient.Tests/Nova.WindowsClient.Tests.csproj --configuration Release --no-restore` — **15/15 passed**. Includes a real Windows OCR engine recognition of an in-memory text image, OBS v5 challenge-authenticated loopback protocol/read-state/scene/record command test peer, fixed authentication vector, installed-OBS detection without launching OBS, browser geometry, text cleanup/translation limits, and existing audio checks.
- MyMemory's live HTTPS endpoint was separately queried with the non-sensitive text `Hello`; it returned HTTP/API status 200 and a Russian translation.
- **Actual OBS recording/scene changes: NOT VERIFIED.** OBS was already running, and its WebSocket password/configuration was not available; the tests exercise the production WebSocket client against a loopback protocol peer, not the OBS application.
- **Browser window movement/restoration in a real browser: NOT VERIFIED.** No supported browser process was running, so only the geometry logic was tested. Multi-monitor/mixed-DPI region selection and browser window behavior also remain **NOT VERIFIED** in this single-instance GUI session; an older v4 NOVA process was running and was not interrupted.
- **Security limitation:** NOVA itself stores no OBS password, but OBS's stock plugin stores its configured password in plaintext in OBS's own config file and can listen on all interfaces. Users must account for both when enabling the OBS server. NOVA does not modify that config or the Windows Firewall.

Research references:

- [obs-websocket v5 protocol](https://github.com/obsproject/obs-websocket/blob/e28f84975984340e84fc4879cfc05741cc0afde1/docs/generated/protocol.md)
- [OBS WebSocket configuration storage](https://github.com/obsproject/obs-websocket/blob/e28f84975984340e84fc4879cfc05741cc0afde1/src/Config.cpp)
- [OBS WebSocket listener binding](https://github.com/obsproject/obs-websocket/blob/e28f84975984340e84fc4879cfc05741cc0afde1/src/websocketserver/WebSocketServer.cpp)
- [Windows OCR engine](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)
- [MyMemory API](https://mymemory.translated.net/doc/spec.php)

## Release v5 publication and desktop shortcut (2026-11-17)

- Published the current Windows client as a self-contained `win-x64` Release to `build/NOVA-Taskbar-Release-v5/publish/` with `dotnet publish src/Nova.WindowsClient/Nova.WindowsClient.csproj --configuration Release --runtime win-x64 --self-contained true --output build/NOVA-Taskbar-Release-v5/publish`. The publish contains 490 files and bundles .NET 10.0.14.
- The published `Nova.TaskBar.dll` matches the RID-specific Release build (SHA-256 `D6DEA3D28230C8C34820B72FF99F31D435B9C2DF12A88D6B9091E424EFC5000F`). The packaged `nova_webrtc.dll` matches the native Release artifact (SHA-256 `D20B95A4BD449CE7E444F9A5C491F1775A24CFB90CC481EBC5E6628F6652BB3F`). Both the apphost EXE and native DLL have x64 PE machine type `0x8664`.
- Updated and read back `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk`; target, working directory and icon now point to the v5 publish directory.
- Release build completed and the Windows client tests passed **15/15**. `git diff --check` passed; Git reported only its existing LF/CRLF normalization warnings.
- **GUI/runtime of v5: NOT VERIFIED.** The previous v4 single-instance process (PID 9080) was left running and was not replaced. Close that instance normally before launching the desktop shortcut to start v5.

## Startup fix and downloadable v6 release (2026-11-17)

- Reproduced the reported launch failure from Windows Application logs and by starting the published EXE. Startup threw `System.ArgumentException: Delegate to an instance method cannot have null 'this'` in `ShellWindow.RegisterWidgets`; the OBS widget's `onDeactivated: _obsRefreshTimer.Stop` method-group was created before the constructor initialized `_obsRefreshTimer`.
- Moved the settings, device-refresh, and OBS-refresh timer initialization ahead of widget registration. This ensures callbacks are constructed only after their timer dependencies exist.
- Published a self-contained x64 build to `build/NOVA-Taskbar-Release-v6/publish/` and packaged all 490 files as `build/NOVA-Taskbar-v6-win-x64.zip`. The ZIP SHA-256 is `54197141E3DE69B2275CE61815BAD1A3A0849F73A71D87066A30D7BE507C1605`; the archive was checked for the apphost, managed assembly, native WebRTC DLL, and runtime configuration.
- `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj --configuration Release --no-restore` — **BUILD VERIFIED**, 0 warnings and 0 errors. Client tests — **15/15 passed**.
- Launched the published v6 EXE after the fix. Its process remained responsive, and Windows UI Automation observed the visible `Панель NOVA` capsule window — **RUNTIME VERIFIED** and **GUI VERIFIED** for startup.
- Updated the desktop shortcut to v6. The app is currently running from `build/NOVA-Taskbar-Release-v6/publish/`.
- The README now distinguishes the source-code ZIP from the runnable GitHub Release archive. Users should download the release ZIP, extract all files together, and run `Nova.TaskBar.exe`; downloading only the EXE will not work because adjacent runtime and native files are required.

## NOVA1 trust-code button layout fix (2026-11-17)

- Fixed the trust-code row layout. The long single-line NOVA1 textbox previously preceded a right-docked button in a `DockPanel`; as the pasted code grew, its desired width could push the action button beyond the visible column. The pairing-code input, local-code copy action, and destination-folder row now use a star-width content column plus an auto-width action column. The input also scrolls horizontally instead of expanding.
- Release build succeeded with 0 warnings and 0 errors; Windows client tests passed **15/15**.
- Published a self-contained x64 build in `build/NOVA-Taskbar-Release-v7/publish/` and verified the 490-entry archive `build/NOVA-Taskbar-v6.0.1-win-x64.zip` contains the EXE, managed DLL, native WebRTC DLL, and runtime config. Archive SHA-256: `418EE543CDCC0FD261EBB80360ABE55E573A29FCD355093667DA267F36C49E0F`.
- Updated and read back the desktop shortcut to target the v7 publish directory.
- **GUI verification of the new NOVA1 row: NOT VERIFIED.** The already-running v6 instance was left open to avoid interrupting the user's current session or losing an entered pairing code. Restart NOVA using the updated shortcut to use the fixed layout.

## Received-file location visibility (2026-11-17)

- Investigated a report that transfers completed but the received-files folder looked empty. The running machine's persisted destination is `Documents/NOVA/Полученные файлы`, not a top-level `Documents/Полученные файлы` folder. Its transfer history showed a completed incoming transfer and the corresponding file was present in that configured directory.
- Added an **Открыть** action beside the destination path so the user can open NOVA's actual configured receive folder rather than guessing which similarly named folder is correct. The full destination path is also available as the path label's tooltip.
- Successful incoming LAN/Direct VPN and WebRTC/relay completion progress now carries the final saved path. NOVA displays that path after the file has been flushed, hash-verified and moved into its final unique filename; sender-side completion does not claim a local save path.
- Release build passed with 0 warnings and 0 errors. Transfer integration tests passed **22/22**, including exact saved-path assertions for LAN, Direct VPN, WebRTC and TCP/TLS relay. Windows-client tests passed **15/15**. `git diff --check` passed.
- **Visual verification of the new folder button: NOT VERIFIED** because the user’s already-running instance is an older build; it was not closed during this task.

## Yandex Music lyrics display and diagnostics (2026-11-17)

### Cause and changes

- The LRC request was awaited, but `LyricsLinesList` was never assigned the populated `_lyricsLines` collection. On a successful timed-lyrics response the UI hid the loading/status text and showed an empty list. The existing active v7 window also showed a 2-DIP-high lyrics list, leaving no usable display area.
- Bound the list to its observable collection, kept a persistent result/status line above it, reserved a minimum lyrics viewport, and compacted the artwork/footer spacing so the list receives usable space in the current workspace.
- Added distinct user-facing outcomes for exact-track mismatch, missing lyrics, authorization/access rejection, network timeout/failure, API status failures, and malformed responses. HTTP 401/403 are authorization/access errors; 404 is interpreted by request stage. Empty parsed responses become “Текст не найден”. Diagnostics never include the OAuth value or raw HTTP exception text.
- Cleared stale source/status text at the beginning of each request and cleared old plain text on track changes/retries. Token handling, exact title/artist matching, HTTPS download-domain checks, response-size limits, playback control, seek, WASAPI and audio-reactive code paths remain unchanged.

### Verification

- **BUILD VERIFIED:** Windows client Release build passed with 0 warnings and 0 errors.
- **FUNCTIONAL VERIFIED (API response observed in the pre-existing runtime):** the running v7 UI showed the real Windows Media Session track “Нежно&небрежно” by “мс улыбочка, onda andar” and the success-only status “LRC загружен из Яндекс Музыки”. That status is set only when the service has returned at least one timed line. The lines were invisible because of the missing collection binding and collapsed layout.
- Client checks passed **16/16**, including LRC parser behavior, empty-response handling, distinct HTTP/network failure classification and existing audio, OCR, OBS, browser and translation checks. `git diff --check` passed.
- Published a self-contained `win-x64` build locally to `build/NOVA-Taskbar-Release-v9/publish/` and updated/read back `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk` to target that directory. The publish contains the EXE, managed assembly, native WebRTC library and self-contained .NET runtime files.
- GitHub was not updated.
- **GUI VERIFIED for the revised build: NOT VERIFIED.** The active single-instance v7 process was left running to avoid interrupting the user or losing its in-memory OAuth value. The next check is to restart NOVA with the updated desktop shortcut, re-enter the token if needed, and confirm the LRC lines are visible for the same real track.
- **Live authorization failure, live no-lyrics track, live network failure and post-fix live request: NOT VERIFIED.** Their state mapping is covered by local tests, but no new live API call was made with a token after rebuilding.

## Local train schedule widget — Tsarskoye Selo → Kupchino (2026-11-18)

### Implemented

- Added the **Расписание** widget to the existing `WidgetRegistry`/`WidgetHost` navigation. It shows the Moscow-local date and time, weekday/weekend mode, up to three upcoming departures, arrival time, train number, and the next-day arrival marker when needed.
- Added a local TXT parser with format/route/time-zone and effective-date validation. Empty lines and comments are ignored; malformed and duplicate rows are reported and skipped without discarding other valid rows. The engine sorts, filters departed trains, chooses the weekday/weekend section using `Europe/Moscow`, and recalculates through the existing one-second clock timer while the widget is selected.
- Added `PARTIAL` data status: available rows now render without a fully verified validity range. Optional `[SATURDAY]` and `[SUNDAY]` sections override `[WEEKENDS]` when daily source results differ. An empty section is an explicit no-data error; a missing/unreadable file is an explicit load error, not an “unverified” state.
- The TXT file is copied beside the client output and configured for publish output. The widget makes no runtime network requests and contains no live delay/cancellation data.

### Available source data and limits

- Read the user-provided Yandex schedule pages for the exact route/station IDs on 2026-11-18 (Wednesday), 2026-11-19 (Thursday), 2026-11-21 (Saturday) and 2026-11-22 (Sunday). These are published date-specific results, not a carrier-operated API or a runtime dependency.
- The weekday section contains **53 exact train/time entries common to both weekday snapshots**; changed weekday rows were excluded. Saturday and Sunday each contain the **48 rows shown for that specific date** and are stored separately: the two weekend snapshots had only one exact number/time match, so treating them as one generic weekend list would be misleading.
- The production file is marked `DATA_STATUS: PARTIAL`; `DATA_NOTE` identifies sample dates and warns that other dates may differ. The app displays the available list and provides this compact caveat rather than blocking on complete confirmation. These samples do not prove the timetable for other dates or future schedule periods.
- Train delays/cancellations are not included. Before travelling, users should check the specific date in the live timetable.

### Verification

- **BUILD VERIFIED:** `dotnet build src/Nova.WindowsClient/Nova.WindowsClient.csproj --configuration Release --no-restore` passed with 0 warnings and 0 errors.
- **FUNCTIONAL VERIFIED:** Windows-client checks passed **22/22**, including parsing the production TXT, its exact 53/48/48 row counts, next-three results for Wednesday, Saturday and Sunday, empty/missing-file errors, partial status, and existing client regressions.
- **GUI VERIFIED: NOT VERIFIED.** An older v10 NOVA instance is already running; it was not closed or interrupted. The updated widget has not yet been opened in a fresh v11 process.
- **LIVE OPERATIONS VERIFIED: NOT VERIFIED.** Source data is date-specific Yandex timetable output; it was not independently confirmed against the carrier for every trip or for dates beyond the samples.

## Local v10 Release publish (2026-11-18)

- Published a self-contained `win-x64` Release to `build/NOVA-Taskbar-Release-v10/publish/` using `dotnet publish`. The output contains 491 files, including `Nova.TaskBar.exe`, the native WebRTC DLL, bundled runtime files, and `assets/schedules/tsarskoe_selo_kupchino.txt`.
- Published EXE SHA-256: `45339042D8836DD3D485B6668A63DB0683B3B75921134BA26DD6E93405F5FE6F`. The EXE PE machine type is `0x8664` (x64). Native DLL SHA-256: `D20B95A4BD449CE7E444F9A5C491F1775A24CFB90CC481EBC5E6628F6652BB3F`.
- Updated and read back `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk`; its target, working directory, and icon now point to the v10 publish folder.
- **PUBLISH VERIFIED.** The EXE, schedule TXT, native DLL, runtime configuration, and shortcut target all exist. **RUNTIME / GUI VERIFIED: NOT VERIFIED**; the newly published EXE was not launched.

## Train departure reminders — local publish v12 (2026-11-18)

### Implemented

- Added an all-trains view for the current Moscow date so users can choose any listed departure, not just the three upcoming rows. The editor supports 10/20/30-minute presets and custom whole minutes from 1 through 1440, with explicit rejection of invalid input and cancellation.
- Added a versioned JSON store at `%LOCALAPPDATA%\NOVA\TaskBar\train-reminders.json`. Each record keeps a stable SHA-256 train/date ID, route, departure/arrival, train number, lead time, enabled state, and delivery state. Writes use a temporary file in the same directory followed by replacement; corrupt or unknown-format data is reported rather than silently overwritten.
- Added reminder management for editing, disabling/re-enabling, and deleting multiple records. Re-selecting the same train updates its existing record.
- Added a 15-second application-level scheduler and real Windows Toast delivery through `CommunityToolkit.WinUI.Notifications`. The scheduler runs independently of window visibility; closing the panel hides it to the tray. Explicitly exiting NOVA stops the scheduler; pending reminders remain stored, and expired alerts are not replayed after their departure.
- The scheduler records a notification claim before handing it to Windows to avoid duplicate delivery after restart. A process crash in the small interval between the claim and OS delivery can therefore lose that one notification; Windows toast history and NOVA storage cannot be updated transactionally.
- Moscow wall-clock departures are converted through `Europe/Moscow`; the scheduler handles previous-day notification times and disables reminders whose departure or notification window has passed.

### Verification

- **BUILD VERIFIED:** Windows-client Debug was built through the client test harness; Release build and self-contained `win-x64` publish completed with 0 warnings and 0 errors.
- **FUNCTIONAL VERIFIED:** the client harness passed **29/29** checks, including preset/custom validation, persistence across scheduler reload, update/disable/re-enable/delete, one-time delivery after restart, simultaneous reminders, missed/departed trains, all departures for a date, and Moscow midnight rollover.
- **WINDOWS TOAST API VERIFIED:** the production `WindowsToastNotificationService` sent a real test toast from a windowless harness process; `ToastNotificationManagerCompat` history contained the expected tag and group. This verifies OS acceptance/history, not whether a popup is visually shown under Focus Assist or user notification settings.
- **PUBLISH VERIFIED:** local self-contained publish is in `build/NOVA-Taskbar-Release-v12/publish/`; the 492-entry ZIP is 85,407,954 bytes and contains the EXE, managed assembly, native WebRTC library, Toast dependency, runtime configuration, and local schedule. ZIP paths were verified portable; SHA-256 is `926391793716a236ca50ccc305a20d249549865b3e21fa2b554115bd704537b9`.
- **SHORTCUT VERIFIED:** `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk` now points to the v12 publish EXE, working directory, and icon.
- **RUNTIME / GUI / scheduled hidden-panel end-to-end: NOT VERIFIED.** The already-running single-instance v11 process was left untouched, so the v12 UI and actual timer-to-toast reminder flow were not launched. The direct Toast smoke test ran without a WPF window; scheduler timing and lifecycle were covered with a recording notifier.
- The GitHub `v6.0.3` release replacement is pending the source push and release-asset update; no `v6.0.4` release is being created.

## UI motion stutter — local publish v15 (2026-11-18)

### Diagnosis and changes

- The main capsule transition changed the HWND size on every animation frame, while `SizeChanged` and `LocationChanged` also rebuilt capsule geometry, repositioned the docked window, refreshed browser adaptation, and updated persisted placement. Collapsing could reposition the window immediately before the size animation, creating a visible jump.
- `ApplyDimensions` now computes one target rectangle and animates only the changed `Left`, `Top`, `Width`, and `Height` properties together. A generation-guarded completion handles final geometry, browser refresh, and persistence once; a newer state transition cancels the previous clocks and invalidates their completion callback.
- Docked/free target placement is calculated without mutating the window during the transition. Size/location handlers skip per-frame side effects, and browser-window adaptation uses a debounced Background-priority refresh.
- Capsule geometry is cached when audio is inactive or below the visible-motion threshold and is not rebuilt during a dimension transition. The audio timer runs at Render priority only while the visible capsule is in Compact/Hover with audio reactivity and motion enabled. Housekeeping timers use Background priority.
- `ScreenRegionSelector` now reuses its GDI pen, brush, and font rather than allocating them on each mouse-move repaint. Existing opacity and dimension transition durations were retained; no broad blur/cache effect or storyboard-based animation system was found.

### Verification

- **BUILD VERIFIED:** Windows-client Release build completed with 0 warnings and 0 errors.
- **FUNCTIONAL VERIFIED:** `Nova.WindowsClient.Tests` passed **29/29**, including audio analysis throughput, local train reminders, OCR, browser geometry, and other client regressions.
- **PUBLISH VERIFIED:** self-contained `win-x64` publish completed to `build/NOVA-Taskbar-Release-v15/publish/` with 492 files. The EXE, managed assembly, runtime, native WebRTC DLL, and local train schedule are present; published managed/native DLL hashes match the corresponding Release build artifacts.
- **SHORTCUT VERIFIED:** `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk` points to the v15 publish EXE, working directory, and icon.
- **RUNTIME / GUI: NOT VERIFIED.** The already-running single-instance v14 process was left untouched; its mutex prevents an interactive launch of v15 without interrupting the existing session.
- **FPS / frame-time / CPU profile: NOT VERIFIED.** No reliable frame-time capture was available, so stable 60 FPS is not claimed. Rapid UI transitions, repeated opens, long-session GUI behavior, and mixed-DPI/multi-monitor behavior remain unverified in v15.
- At the time of this v15 publish, the binary was local and only the source changes had been pushed. It was superseded by the v6.0.4 release below.

## v6.0.4 Windows x64 release (2026-11-18)

- Set the Windows client product version to `6.0.4` and updated the README download instructions.
- Published a self-contained `win-x64` package as GitHub Release [v6.0.4](https://github.com/popolo123215-source/NOVA-TASKBAR/releases/tag/v6.0.4). The tag points to source commit `80102fa`.
- The release archive `NOVA-Taskbar-v6.0.4-win-x64.zip` contains 492 files, including `Nova.TaskBar.exe`, the .NET runtime, `nova_webrtc.dll`, and the bundled local train schedule. Users must extract the complete ZIP and run `Nova.TaskBar.exe` from that folder; the EXE is not a standalone file.
- Archive SHA-256: `757a27b1c07e0c1361e7a8e17d08097da2b76a63db97cc3123dd9ba2a20d00ef`. A `.sha256` asset is also attached to the release.

### Verification

- **BUILD VERIFIED:** Windows-client Release build completed with 0 warnings and 0 errors.
- **FUNCTIONAL VERIFIED:** `Nova.WindowsClient.Tests` passed **29/29**.
- **PUBLISH VERIFIED:** GitHub release API reports both assets as uploaded. The archive was downloaded back from GitHub; its size and SHA-256 matched the local ZIP, and extraction produced all 492 files with version metadata `6.0.4`.
- **SHORTCUT VERIFIED:** `C:\Users\NIIOIY\Desktop\NOVA Taskbar.lnk` points to the v6.0.4 publish EXE, working directory, and icon.
- **RUNTIME / GUI: NOT VERIFIED.** The already-running single-instance v14 app was left untouched, so the v6.0.4 EXE was not launched interactively. Stable 60 FPS and long-session UI behavior are not claimed.
