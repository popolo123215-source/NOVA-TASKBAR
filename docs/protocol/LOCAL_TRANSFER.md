# NOVA local transfer protocol v1

## Scope
This protocol transfers files between paired Windows NOVA clients on a local network or over a manually addressed IPv4 path such as Radmin VPN. It has no cloud relay. Android can implement the same framed control messages and byte stream later; Windows-specific discovery and identity storage remain in the client adapter.

## Discovery
For LAN use, clients announce device ID, display name, TCP listener port, SHA-256 certificate fingerprint, protocol version, and timestamp over IPv4 multicast group 239.255.42.99 on UDP port 45984. Announcements expire after eight seconds. Direct VPN mode does not depend on multicast: the sender selects a saved trusted contact and enters its Radmin VPN IPv4 address and the receiver's current TCP listener port. The listener port is assigned at startup and displayed in NOVA. An address or discovery announcement supplies a route only; it never grants trust or transfer access.

## Trust and connection
Each installation creates an RSA device certificate and protects its PKCS#12 identity with Windows DPAPI for the current user. The user compares the complete 256-bit certificate fingerprint shown by both clients and explicitly trusts the peer on each device. LAN mode obtains its address and port through discovery; Direct VPN asks the user to enter the peer's numeric IPv4 address and port manually.

The listener requires a client certificate and validates it against the local trusted-device store before reading an application message. The sender also pins the receiver certificate fingerprint. Both checks must succeed before an offer is exchanged. Unknown peers cannot submit offers or file bytes. A user must approve each individual incoming file.

TLS is performed with .NET SslStream and the operating system's secure protocol selection (SslProtocols.None). Microsoft documents SslStream as authenticating the server and optionally the client before information is exchanged over the stream.

## Framing and offer
All protocol messages are sent inside TLS. A frame is a 4-byte big-endian length followed by UTF-8 JSON, capped at 64 KiB. Every envelope has protocol version 1. An offer contains transfer ID, sender/recipient device references, basename, byte length, SHA-256, creation/expiry time, and resume capability. The sender waits for an explicit recipient decision before writing any file bytes.

## Payload and integrity
After acceptance, exactly SizeBytes are read and written using a bounded 64 KiB buffer. The receiver streams into a uniquely named temporary file in the configured destination and computes SHA-256 while writing. It verifies the length and digest before moving the file into place. A name collision receives a numbered suffix; files are never overwritten. Path separators, rooted paths, invalid characters, and paths outside the destination are rejected. Received files are not opened.

Resume is advertised as unavailable for this local v1 protocol (CanResume=false). The transfer can be retried by sending the file again. Direct VPN applies a 15-second timeout to each TCP-connect and TLS-authentication stage, a three-minute approval timeout, and a two-minute network-idle timeout during streaming; cancellation or a dropped connection removes the partial destination file.

## History
Incoming, outgoing, rejected, completed, cancelled, and failed results are persisted in a local JSON history store. The store retains the most recent 1,000 entries.

## Current limitations
LAN discovery depends on multicast being allowed on the LAN. Direct VPN requires both clients to have the route provided by Radmin VPN, the receiver's displayed TCP port, and Windows Firewall to allow NOVA's listener on that network. Direct VPN does not use Rendezvous, Cloudflare, a public IPv4 address, or a file relay. Its loopback tests do not verify Radmin VPN or Windows Firewall behavior between two computers. Android, clipboard sync, and remote control are not implemented.
