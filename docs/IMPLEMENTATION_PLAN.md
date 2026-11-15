# NOVA TaskBar implementation plan

**Updated:** 2026-11-14  
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

#### Current Critical Path

1. Run `./scripts/build-native.ps1`; confirm the manifest completes without any repository path in vcpkg’s tool/download/build logs.
2. Verify `build/native-x64/Release/nova_webrtc.dll` architecture and exports, then build the Windows client and execute the full harness from a clean output folder.
3. Treat native coordinator and lifecycle results as valid only after the rebuilt DLL is copied beside the harness executable; then continue deployed WSS/direct-ICE/TURN acceptance tests.
