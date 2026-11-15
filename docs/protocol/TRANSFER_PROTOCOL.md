# NOVA Internet transfer wire protocol v1

## Scope and status

This document specifies the language-neutral message encoding shared by Windows and Android DataChannel clients. Each WebRTC DataChannel message contains exactly one NOVA protocol message. The codec in `Nova.TransferProtocol` implements the bounded control envelope and file-block framing.

This is not yet an Internet transfer implementation: the libdatachannel native adapter, signalling/Core, authenticated device-proof verification, consent state machine, durable checkpoint store, sender backpressure, TURN fallback, and reconnect/resume session logic are still pending. A receiver must not accept `deviceProof` until its signed transcript and trusted-key validation are implemented.

The existing LAN listener and `LOCAL_TRANSFER.md` wire protocol remain independent and unchanged.

## Common limits

- Protocol version: integer `1`. Unknown versions and message types fail closed.
- Canonical transfer ID: lowercase GUID in 32-character `N` form, for example `00112233445566778899aabbccddeeff`.
- Maximum DataChannel message size used by this profile: 16,384 bytes, including headers.
- All JSON is UTF-8. Object member names are camelCase. JSON control messages are limited to 16,384 bytes and maximum nesting depth 32.
- DataChannel is configured reliable and ordered for file transfers. It is message-oriented; a file is never sent as a single message.
- Offsets and sizes are non-negative signed 64-bit byte counts. Implementations reject overflow and messages beyond the declared file size.

## Control envelope

A control message is one UTF-8 JSON object:

```json
{
  "version": 1,
  "type": "decision",
  "transferId": "00112233445566778899aabbccddeeff",
  "payload": {
    "transferId": "00112233445566778899aabbccddeeff",
    "accept": true,
    "destinationToken": null
  }
}
```

Registered types and payloads:

| Type | Payload |
|---|---|
| `offer` | Existing `TransferOffer` fields: sender, recipient, filename, size, content type, SHA-256, creation/expiry and resume capability. |
| `decision` | Existing `TransferDecision`; no file block may be sent before an accepted decision. |
| `deviceProof` | `TransferDeviceProofPayload` shape. It is reserved until the signed transcript and trust checks are specified and implemented. |
| `resume` | `TransferResumePayload { offsetBytes }`; must refer to a receiver-confirmed durable offset. |
| `blockAck` | `TransferBlockAcknowledgementPayload { durableOffsetBytes }`; only acknowledge data after it has been written and flushed to the partial file. |
| `complete` | `TransferCompletePayload { sha256 }`; sent only after size and whole-file digest validation. |
| `failure` | `TransferFailurePayload { code, message? }`; code is stable and machine-readable. |

The codec validates framing, version, type name, transfer ID and size. Each session handler must validate payload semantics and state transitions.

## Binary file block

A data block is one binary DataChannel message. All integer fields use network byte order. The fixed header is 36 bytes:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | ASCII magic `NVAF` |
| 4 | 1 | protocol version (`1`) |
| 5 | 1 | message type (`1` = file data block) |
| 6 | 2 | reserved; must be zero |
| 8 | 16 | transfer UUID, RFC 4122/network byte order |
| 24 | 8 | byte offset from start of file, unsigned encoding constrained to signed 64-bit range |
| 32 | 4 | payload length in bytes |
| 36 | 1–16,348 | file bytes |

The total message is at most 16,384 bytes. The maximum payload in one block is therefore 16,348 bytes. Zero-byte files have no data blocks and proceed directly to final digest validation after consent.

The receiver requires each block to match the active transfer ID, expected next offset and remaining file size. Duplicate or out-of-order blocks are rejected by the session layer. The codec checks header, reserved bits, length, bounds, and offset overflow.

## Flow control, persistence and integrity

The codec bounds individual messages; it does not itself throttle producers. The transport/session adapter must keep a bounded outbound queue, stop reading the source when the native DataChannel buffered amount crosses a high watermark, and resume only after a low-water notification. Native callbacks must hand data to a bounded receiver queue rather than block on file I/O or UI work.

The receiver acknowledges only durable block boundaries. On reconnect, the sender resumes from the latest acknowledged offset; the receiver truncates any unacknowledged tail before accepting more data. The final SHA-256 covers the complete file and is mandatory regardless of route. Temporary files are never exposed as completed files before size and digest validation.

These persistence and session rules are normative goals for the next protocol-session component, not behavior implemented by this framing codec yet.

## Identity binding

DTLS protects the DataChannel transport, but a per-session DTLS certificate does not establish a NOVA device identity. The eventual proof must bind both peers' fresh nonces, device IDs, transfer/session ID, and the negotiated DTLS certificate fingerprints to a signature verified against the user's trusted NOVA device key. Signalling data alone is not identity proof. This transcript is deliberately not accepted by the current codec as an authorization decision.
