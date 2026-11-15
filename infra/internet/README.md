# NOVA Internet infrastructure

This deployment contains the production WSS rendezvous/signaling service and coturn. Signaling frames are device-signed end to end; the server authenticates the device certificate and forwards the signed frame. File bytes continue to use the peer DataChannel. TURN credentials are minted for ten minutes using coturn's shared-secret REST mechanism.

## Required public resources

- A Linux host with a stable public IPv4 address (or a correctly configured public IPv6 deployment) and Docker Compose v2.
- Public DNS `NOVA_PUBLIC_HOST` pointing to that host.
- A publicly trusted certificate for that DNS name. Put the PFX at `certs/rendezvous.pfx`, and its PEM full chain and matching private key at `certs/fullchain.pem` and `certs/privkey.pem` for coturn. Restrict key permissions to the service account.
- Open inbound TCP 443, UDP and TCP 3478, TCP 5349, and UDP 49160–49259 in the host firewall and cloud security group. The relay range supports a bounded number of concurrent TURN allocations; raise both the range and published firewall rules for expected load.
- Internet egress from the host for image pulls/builds and certificate renewal.

The Compose file uses the pinned `coturn/coturn:4.18.0-alpine` image. coturn shares the host network so relay UDP ports are not hidden behind Docker NAT. The Rendezvous Service listens on container port 8443 and is published as TCP 443.

Each device can publish at most 256 mutually trusted contacts per rendezvous registration; this keeps roster frames within the bounded 256 KiB control-frame limit. The default server admission limit is 5,000 simultaneously online devices and 30 new connections per source IP per minute. Operators can tune `NOVA_MAX_ONLINE_DEVICES` and `NOVA_CONNECTS_PER_IP_PER_MINUTE` in `.env` for the host's expected size and NAT sharing.

## Configure and start

```powershell
Copy-Item .env.example .env
# Edit .env with the public hostname and unique random secrets.
New-Item -ItemType Directory -Force certs | Out-Null
# Place rendezvous.pfx, fullchain.pem, and privkey.pem in certs/.
docker compose up --build -d
docker compose ps
curl.exe https://$env:NOVA_PUBLIC_HOST/healthz
```

Generate the shared TURN secret with a cryptographic random generator. Never commit `.env` or anything under `certs/`. The client endpoint is `wss://<public-host>/v1/connect`; enter it in NOVA on both devices. For first-time remote pairing, exchange each app's `NOVA1` device code over a trusted out-of-band channel and add it on the other device. This pins the existing Device ID and certificate fingerprint in NOVA's current trust store; both sides must perform this step. The service does not create trust relationships.

For a production rollout, terminate/renew the public certificate through the operator's existing certificate automation, then restart the Rendezvous container after renewal. Back up the `rendezvous-state` volume: it stores identity-certificate pins and must not be casually reset or edited.

## Operations

```powershell
docker compose logs -f rendezvous turn
docker compose restart rendezvous turn
docker compose down
```

No public domain, certificate, server, firewall, or cloud account is provisioned by this repository. A live cross-network acceptance test must use those operator-owned resources; a local build is not evidence that the public ICE/TURN path is deployed or reachable.
