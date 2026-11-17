# NOVA Internet infrastructure

This deployment contains the production WSS rendezvous/signaling service, coturn, and a separate TCP/TLS file relay at `wss://<host>/v1/relay`. Signaling remains device-signed end to end. Relay tickets are short-lived, single-use, bound to an accepted transfer and both mutually trusted devices, and require a fresh device-key proof. File bytes are streamed in bounded NOVA protocol blocks; the receiver durably acknowledges blocks and verifies the offered SHA-256 before sending completion. The relay uses the existing TCP 443 listener, not TURN, and does not add a public port. Rendezvous mints ten-minute TURN REST credentials using the shared secret configured in coturn.

TLS terminates at the relay server: the server forwards the file bytes between its two TLS connections and can inspect their plaintext. Use this relay only on a server whose operator is trusted; this transport does not provide end-to-end file encryption.

## Native Windows server

Rendezvous is a regular .NET 10 ASP.NET Core process and can run directly on Windows. The manager publishes it in Release mode, starts it on TCP 443, and tracks its PID and logs; it does not install a Windows Service or change automatic-start settings.

The [upstream coturn project](https://github.com/coturn/coturn/blob/master/README.md) lists native Windows with an MSVC toolchain as a supported target. This repository includes the Windows x64 coturn 4.19.0 runtime in `coturn\bin`, including its runtime DLLs. It came from the successful upstream [MSVC v143 x64 Release Actions run](https://github.com/coturn/coturn/actions/runs/43870339227), built at commit `160dad2ea98fcacd3c925a6bfbc02ff81055a1d6`; `PROVENANCE.txt` records the artifact and file hashes. The executable is not Authenticode-signed. coturn's included `LICENSE` is retained alongside the runtime.

The package reports OpenSSL 3.5.1 and supports UDP, TCP, TLS 1.2/1.3, TURN REST shared-secret authentication, and the required Windows runtime DLLs. Local loopback tests verified authenticated relayed traffic over UDP and TCP and rejection of an invalid secret. A TLS handshake still requires a real certificate and is not claimed as verified here. The coturn command-line administration interface is not enabled. The manager uses the bundled runtime and does not download, build, or install coturn.

Required operator-provided files:

- A real public DNS/DDNS hostname in `NOVA_PUBLIC_HOST`, resolving to exactly one current public IPv4 A record. The manager refreshes the coturn public/private address pair when started; it does not configure a DDNS updater.
- A matching, publicly trusted certificate: `certs/rendezvous.pfx` for Rendezvous and `certs/fullchain.pem` plus `certs/privkey.pem` for coturn.
- A unique `NOVA_TURN_SHARED_SECRET` of at least 32 characters and the PFX password in the ignored `.env`.
- The bundled Windows coturn executable path in `NOVA_COTURN_EXECUTABLE` (already set in `.env.example`). If overriding it, use a Windows x64 build with TLS and TURN REST shared-secret support.
- The .NET 10 SDK and ASP.NET Core 10 runtime.

Configure and start from the repository root:

```powershell
Copy-Item .\infra\internet\.env.example .\infra\internet\.env
# Edit .env with the real hostname, secrets, and PFX password; the bundled coturn path is already set.
New-Item -ItemType Directory -Force .\infra\internet\certs | Out-Null
# Place the matching rendezvous.pfx, fullchain.pem, and privkey.pem in certs/.
.\infra\internet\manage-windows.ps1 -Action start
```

The manager generates a temporary ignored coturn config from `.env`, starts both native processes, checks the local TURN UDP/TCP listeners and TLS handshake, then checks the Rendezvous HTTPS endpoint. The TCP/TLS file relay is hosted by Rendezvous on the same TCP 443 listener; it is not a third process. `NOVA_MAX_RELAY_FILE_BYTES` sets its maximum offered file size (default 100 GiB, maximum 1 TiB). The manager removes the generated coturn config on stop. These startup checks are local only and do not test router forwarding or Internet reachability.

Operations:

```powershell
.\infra\internet\manage-windows.ps1 -Action status
.\infra\internet\manage-windows.ps1 -Action logs
.\infra\internet\manage-windows.ps1 -Action logs -Follow
.\infra\internet\manage-windows.ps1 -Action stop
```

Logs and process state are stored in ignored paths under `infra/internet/`. The manager only stops PIDs whose executable path and command line match the server it started.

The native state file is `infra/internet/state/rendezvous/device-pins.json`. If migrating a previously used Compose deployment, stop it and copy its `device-pins.json` from the `rendezvous-state` volume into that directory before the first native start. No automatic Docker-volume migration is performed.

## Public network requirements

Forward and allow only these service ports to the Windows host's reserved LAN address:

- TCP 443 — WSS Rendezvous and TCP/TLS file relay (`/v1/connect` and `/v1/relay`).
- UDP and TCP 3478 — STUN/TURN.
- TCP 5349 — TURN over TLS.
- UDP 49160–49259 — TURN relay allocations.

Do not forward container TCP 8443; it is only used by the Compose deployment. DNS-01 certificate renewal avoids needing inbound TCP 80. The optional `configure-windows-firewall.ps1` prepares only the corresponding Windows Defender Firewall rules; it is not run automatically and does not alter router settings.

Dynamic public IPv4 works only if the DDNS A record is updated before starting/restarting coturn. Router port forwarding cannot overcome CGNAT or an ISP that blocks inbound traffic. Reserve the Windows host's LAN IP in the router. No public domain, DDNS updater, certificate, firewall rule, router mapping, or cloud account is created by this repository.

For first-time remote pairing, exchange each app's `NOVA1` device code over a trusted out-of-band channel and add it on the other device. This pins the existing Device ID and certificate fingerprint in NOVA's current trust store; both sides must perform this step. The service does not create trust relationships.

The application routes are `/v1/connect` and `/v1/relay`; neither is a health or administrative endpoint. `/v1/relay` rejects connections without an authorized, unused transfer ticket. A live cross-network acceptance test must use operator-owned DNS, certificate, router, and network resources. Local startup is not evidence that the Internet ICE/TURN path is reachable.

## Linux Docker Compose deployment

The existing Compose alternative remains for a Linux Docker Engine with Docker Compose v2. coturn uses `network_mode: host` so the UDP relay range is not hidden behind Docker NAT. Rendezvous listens on container TCP 8443, published as TCP 443. Windows Docker Desktop/WSL networking is not required for the native manager above.

```powershell
Push-Location .\infra\internet
docker compose up --build -d
docker compose ps
docker compose logs -f rendezvous turn
docker compose down
Pop-Location
```

The configured `turn:...?transport=tcp` and `turns:...?transport=tcp` URLs select TCP/TLS transport from a client to coturn. They are not the separate TCP/TLS file relay. NOVA first uses WebRTC (which can select direct or TURN candidates); in Automatic mode it falls back to the file relay if the WebRTC route fails. The transfer panel also provides forced Direct, TURN UDP, TURN TCP, TURN TLS, and TCP/TLS File Relay modes for controlled testing. The sender's selected mode is included in the consent offer. Transfers are not resumable: after a network failure, retry the transfer from the beginning.

For a local two-client relay check, run the existing integration harness:

```powershell
dotnet run --project tests\Nova.TransferEngine.Tests\Nova.TransferEngine.Tests.csproj
```

For an Internet test, configure both clients with the same WSS rendezvous endpoint, mutually trust their `NOVA1` device codes, and use Automatic mode. To force a route, choose it in the sender's transfer panel; the recipient confirms the transfer as usual. The progress row reports the observed route and SHA-256 result. Automatic WebRTC diagnostics distinguish direct from TURN relay, but the current native WebRTC API does not expose enough information to reliably distinguish the TURN server-side UDP/TCP/TLS transport; use the corresponding forced mode when that distinction is required.

Each device can publish at most 256 mutually trusted contacts per rendezvous registration; this keeps roster frames within the bounded 256 KiB control-frame limit. The default server admission limit is 5,000 simultaneously online devices and 30 new connections per source IP per minute. Operators can tune `NOVA_MAX_ONLINE_DEVICES` and `NOVA_CONNECTS_PER_IP_PER_MINUTE` in `.env`.
