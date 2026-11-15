# NOVA local transfer protocol v1

## Scope
This protocol transfers files between paired Windows NOVA clients on one local network. It has no cloud relay. Android can implement the same framed control messages and byte stream later; Windows-specific discovery and identity storage remain in the client adapter.

## Discovery
Clients announce device ID, display name, TCP listener port, SHA-256 certificate fingerprint, protocol version, and timestamp over IPv4 multicast group 239.255.42.99 on UDP port 45984. Announcements expire after eight seconds. Discovery supplies a connection candidate only; a UDP announcement never grants trust or transfer access.

## Trust and connection
Each installation creates an RSA device certificate and protects its PKCS#12 identity with Windows DPAPI for the current user. The user compares the complete 256-bit certificate fingerprint shown by both clients and explicitly trusts the peer on each device. No IP address or port is entered manually.

The listener requires a client certificate and validates it against the local trusted-device store before reading an application message. The sender also pins the receiver certificate fingerprint. Both checks must succeed before an offer is exchanged. Unknown peers cannot submit offers or file bytes. A user must approve each individual incoming file.

TLS is performed with .NET SslStream and the operating system's secure protocol selection (SslProtocols.None). Microsoft documents SslStream as authenticating the server and optionally the client before information is exchanged over the stream.

## Framing and offer
All protocol messages are sent inside TLS. A frame is a 4-byte big-endian length followed by UTF-8 JSON, capped at 64 KiB. Every envelope has protocol version 1. An offer contains transfer ID, sender/recipient device references, basename, byte length, SHA-256, creation/expiry time, and resume capability. The sender waits for an explicit recipient decision before writing any file bytes.

## Payload and integrity
After acceptance, exactly SizeBytes are read and written using a bounded 64 KiB buffer. The receiver streams into a uniquely named temporary file in the configured destination and computes SHA-256 while writing. It verifies the length and digest before moving the file into place. A name collision receives a numbered suffix; files are never overwritten. Path separators, rooted paths, invalid characters, and paths outside the destination are rejected. Received files are not opened.

Resume is advertised as unavailable for this local v1 protocol (CanResume=false). The transfer can be retried by sending the file again.

## History
Incoming, outgoing, rejected, completed, and failed results are persisted in a local JSON history store. The store retains the most recent 1,000 entries.

## Current limitations
Discovery depends on multicast being allowed on the LAN and Windows Firewall allowing the app's listener. This milestone does not implement remote-network relay, Android, clipboard sync, or remote control. Cross-machine behavior must still be verified on two Windows clients.
