# ShortP2P.LoRa — Meshatastic transport / wire requirements

Parent: [SMT-1](https://botticelli.youtrack.cloud/issues/SMT-1) (ShortP2P LoRa + Meshtastic). Tracked in [SMT-2](https://botticelli.youtrack.cloud/issues/SMT-2).

This document fixes the structure of the Meshatastic transport and physical layer, its wire messages, the chunk / CRC / retry rules, and the NetworkId–MeshId directory. It does not invent a second chat protocol: ShortP2P wire frames from SMT-1 still apply. Radio frequency / power / duty-cycle limits live in [requirements-rf.md](requirements-rf.md).

Sources:

- [SMT-1](https://botticelli.youtrack.cloud/issues/SMT-1)
- ShortP2P `dev/0.2`: `ShortP2P.Transport.Abstractions` (`ITransport`, `TransportAddress`, `TransportKind`, `TransportReceiveMessage`), `ChatInviteCodec`, `ChatWireCodec`, `P2PHandshake`, `P2PSession`

---

## REQ-CFG-1 — Packaging

The component ships two ways, and both must be usable at the same version:

1. **NuGet package** `ShortP2P.LoRa`, referenced from ShortP2P.
2. **Git submodule** inside the ShortP2P tree.

The package `Version` **X.Y must match** the ShortP2P version X.Y it is bound to (currently `0.2`, see ShortP2P `Directory.Build.props`). A bump of one side bumps the other.

`ShortP2P.LoRa.csproj` takes a dependency on `ShortP2P.Transport.Abstractions`. The interface is not forked.

## REQ-CFG-2 — Board identity

Board **name and model are not part of the config**. The board is already hidden behind the Meshtastic firmware; this layer opens a serial port. After the port opens, the board itself reports its hardware model (`hw_model`) and firmware; that is the board identity and it is written to the connect log. An operator label may name the node for the UI, but it does not change radio parameters. Config holds the COM port and the jurisdiction profile, nothing about the model.

## REQ-CFG-3 — Retry settings

Retry settings live in the same config as the COM port (`MeshatasticTransportOptions` / `RetryConfig`).
They are stretched by the RX duty-cycle share so ack / next-chunk waits match the channel budget
(TX 10% / RX 90% by default, REQ-RF-2):

`Timeout = Tb × (1 + 0.015 × Nrx)` with `Tb = 60 s` and `Nrx = 100 − TX%` (default 90 → **≈ 141 s**).

| Setting | Default | Meaning |
| --- | --- | --- |
| `BaseTimeout` (Tb) | **60 s** | Base wait for a receipt / next chunk before duty scaling. |
| `RxDutyPercent` (Nrx) | **90** | RX share of the airtime budget (`100 − TX%`). |
| `RxTimeout` | **Timeout** | No next chunk for this long → ask for resend. |
| `SendRetryIntervalBase` | **Timeout** | Base of the send backoff. |
| `SendMaxRetries` | **3** | Send attempts before `SendAsync` fails. Adjustable. |
| `RxRetryIntervalBase` | **Timeout** | Base of the receive-request backoff. |
| `RxMaxRetries` | **3** | Receive-side resend requests before the message is dropped. Adjustable. |

Backoff is exponential: `interval(retry) = Timeout · 2^retry`.
Use `RetryConfig.FromTxDutyPercent(txPercent)` when starting the transport so Tb/Nrx follow the configured duty cycle.

---

## REQ-TR-1 — ITransport

`ShortP2P.LoRa` contains `MeshatasticTransport` that implements `ShortP2P.Transport.Abstractions.ITransport`:

- `Kind == 5` (`TransportKind.Meshtastic`, already reserved in this repo; must not collide with `Udp` = 1, `Bluetooth` = 2, `Infrared` = 3, `MessengerServer` = 4);
- `StartAsync` / `StopAsync` open and close the serial link;
- `SendAsync(ReadOnlyMemory<byte>, TransportAddress, CancellationToken)` sends raw ShortP2P bytes;
- `Inbound` (`ChannelReader<TransportReceiveMessage>`) yields received datagrams.

One `TransportAddress` of kind 5 is one **MeshId** (see REQ-DIR-1). Radio broadcast is a link destination and is not stored as a MeshId.

There is no UDP port split on this transport. Invite and data share one pipe.

---

## REQ-WIRE-1 — Frames this transport carries

Meshatastic does not define its own chat, invite, or handshake layout. `SendAsync` / `Inbound` carry the same bytes as ShortP2P on `dev/0.2`. The messenger demuxes by the first byte. Source of truth is the codecs in that tree. (`protocol.md` §3–§6 in ShortP2P still shows a 16-byte id; on `dev/0.2` `CompressedNetworkId.WireLength` is **12**.)

| First byte | Name | Layout |
| --- | --- | --- |
| `0x30` | Chat invite | `0x30` \| ASCII `SP2I` \| version `1` \| NetworkId 12 \| nickLen `uint16 BE` \| nick UTF-8 \| rsaJsonLen `uint32 BE` \| RSA public key JSON (`{"M","E"}` base64) \| hostLen `uint16 BE` \| host UTF-8 \| replyPort `uint16 BE`. Codec: `ChatInviteCodec`. |
| `0x01` | RSA handshake | `0x01` \| **128** bytes RSA-OAEP(SHA-1) of AES-128 key (16) \|\| HMAC key (32). Exactly **129** bytes. Sent only by the leader (smaller NetworkId). |
| `0x04` | Session setup request | `0x04` \| NetworkId 12. Exactly **13** bytes. Sent only by the follower. |
| `0x02` | Cipher | `0x02` \| `P2PSession` packet: IV 16 \|\| AES-CBC/PKCS7 ciphertext \|\| HMAC-SHA256 tag 16. |

Plaintext inside `0x02` is either legacy raw UTF-8 or `ChatWireCodec`. Magic ASCII `S2P1`, then a kind byte. Lengths inside this codec are **little-endian**.

| Kind | Name | After the kind byte |
| --- | --- | --- |
| `0x01` | Text | `uint32 LE` length \| UTF-8 |
| `0x02` | Image | `uint16 LE` mimeLen \| mime UTF-8 \| `uint32 LE` dataLen \| bytes |
| `0x03` | File | `uint16 LE` mimeLen \| mime UTF-8 \| `uint16 LE` nameLen \| file name UTF-8 \| `uint32 LE` dataLen \| bytes |
| `0x04` | Transfer offer | `uint32 LE` jsonLen \| UTF-8 JSON of `ChatWireTransferOffer` |
| `0x05` | Transfer control | `uint32 LE` jsonLen \| UTF-8 JSON of `ChatWireTransferControl` |
| `0x06` | User info | `uint16 LE` aboutLen \| AboutMe UTF-8 \| `uint32 LE` avatarLen \| avatar bytes |

The crypto probe is not a new type. It is a text frame (`S2P1` / `0x01`) whose UTF-8 is `CHAT <srcShort> <tgtShort> ACK` or `CHAT <srcShort> <tgtShort> OK`.

### Size

Chat plaintext target per radio frame is **80 bytes** (SMT-1). That plaintext, once encrypted and prefixed with `0x02`, still fits in one Meshtastic data payload (about 233 bytes). The handshake frame is 129 bytes and also fits in one payload. Client-to-client binary limit stays **20 KB** (SMT-1).

### Frames this transport does not carry

Presence `0x31`, LAN FIND/FOUND/RELAY `0x10` / `0x11` / `0x22`, and discovery `0x40`–`0x45` stay off this pipe. Mesh routing is Meshtastic's. Chunk frames, `0x32`, and `0x33` are consumed inside this component. A chat payload on `Inbound` has first byte `0x30`, `0x01`, `0x04`, or `0x02`.

---

## REQ-CHUNK-1 — Chunking

A datagram that fits in one radio payload is sent as itself. Its first byte stays `0x30`, `0x01`, `0x04`, `0x02`, or `0x32`.

A datagram that does not fit is split. Every chunk of that datagram carries the same compressed id, its chunk number, the total number of chunks, the CRC type, and the CRC of the whole original datagram. The messenger still receives one original datagram after reassembly. The pair `(TX MeshId, compressed id)` is the **mesh message id**.

| Offset | Field |
| --- | --- |
| 0–5 | Compressed id: **6** characters from `A–Z`, `a–z`, and `0–9`. Case-sensitive. The first character is a letter (`A–Z` or `a–z`); the other five may be letters or digits. The same six characters on every chunk of this datagram. |
| 6–7 | Chunk number, `uint16 BE`, **1-based** (`1` … total). Number `0` is not used. |
| 8–9 | Total chunk count, `uint16 BE`, **≥ 2**. |
| 10 | CRC type, `uint8`. `1` = CRC32-IEEE. Same value on every chunk. |
| 11–14 | CRC of the **whole original datagram**, `uint32 BE`, in the algorithm the type byte names. The same value on every chunk. |
| 15… | Bytes of this chunk, in order. |

The id is chosen per datagram. Reassembly groups chunks by `(source MeshId, compressed id)` and concatenates chunk `1` through `total`. Chunk order on the wire does not matter. A chunk starts with a letter. Digits are allowed only after that first character, because ASCII `0`, `1`, and `2` are the frame bytes `0x30`, `0x31`, and `0x32`. Chunk number and total are binary `uint16 BE`, not decimal text.

A single-frame datagram (chunking not needed) has no CRC header of its own.

## REQ-CRC-1 — CRC

The CRC is the standard CRC-32 (polynomial `0xEDB88320`, reflected, initial `0xFFFFFFFF`, final XOR `0xFFFFFFFF`) — the same value as ZIP / `System.IO.Hashing.Crc32`.

It is computed with the **hardware CRC instruction** when the target exposes one for this polynomial (ARMv8 `crc32x` via intrinsics). Note that the x86 SSE4.2 `crc32` instruction computes CRC-32C (Castagnoli polynomial), **not** this CRC-32, so x86 targets use the software path. Where there is no hardware CRC, it falls back to a **nibble table**: a 16-entry `uint32` table, one nibble (4 bits) at a time. Both paths give the same bytes; the wire value does not depend on which path the node used. The table is a fixed constant, not generated per target.

Every chunk of one datagram carries the same CRC type byte and the same CRC value. A chunk whose CRC type is not known, or whose CRC type / value disagrees with the rest of that datagram, is treated as corrupt and triggers REQ-RX-1; it is not guessed.

## REQ-RETRY-1 — Send-Retry

- On any TX error of a chunk (serial write, board NAK, timeout), resend that chunk.
- `SendRetryInterval(retry) = SendRetryIntervalBase · 2^retry`. Base default **1 s** → 1 s, 2 s, 4 s.
- `SendMaxRetries` default **3**, adjustable. After that, `SendAsync` fails and the message is dropped.

## REQ-RX-1 — Receive-Retry

- **CRC disagreement:** a full set whose recomputed CRC does not match, or an incoming chunk whose CRC type / CRC value disagrees with the rest of that datagram — ask the sender to resend after `RxRetryDelay(retry) = RxRetryIntervalBase · 2^retry`. Base default **1 s**. Discard that set.
- **Stalled assembly:** if for `RxTimeout` after the last received chunk the next one has not come — chunk order does not matter here, and the case "N−1 of N arrived but the last one is missing" is the same stall — send the same resend request with `RxRetryDelay(retry)`. `RxTimeout` default **10 s**, adjustable.
- `RxMaxRetries` default **3**, adjustable. After that, the partial message is dropped.

## REQ-RETRY-2 — Retry request frame `0x33`

This asks the TX node to send the whole message again. It is not a chunk and not a chat frame.

| Offset | Field |
| --- | --- |
| 0 | `0x33` |
| 1–4 | ASCII `SP2R` |
| 5 | version `1` |
| 6–13 | TX MeshId: `meshIdLen uint8` (1–8) + Mesh Id bytes. The original sender's radio id. |
| 14–19 | compressed id, 6 characters. Together with the MeshId this is the mesh message id to resend. |
| 20 | reason, `uint8`: `1` = CRC mismatch on a complete set, `2` = chunk timeout (incomplete set), `3` = inconsistent CRC type / value across the chunks of one datagram |
| 21 | retry number, `uint8` (1 … `RxMaxRetries`) |

On receiving `0x33`, if the node is that TX MeshId and the message is still kept, it re-sends the **whole** message from chunk 1 at the next TX window, with a new compressed id. After `RxMaxRetries` requests the receiver stops asking.

---

## REQ-DIR-1 — MeshId

One MeshId is the internal id of one Meshatastic/LoRa device. Opaque bytes, length 1–8. When the device exposes a hardware address, this is its **MAC** (6 bytes). It is what a `TransportAddress` of kind 5 carries.

## REQ-DIR-2 — NetworkId → MeshId directory

The component keeps a local map **NetworkId (12 bytes) → many Mesh Ids**, for self, peers, and servers (`MeshDirectory`). Persistence is behind `IMeshDirectoryStore` (default: a local file); the ShortP2P host may replace it with SQLite.

Unique key is `(NetworkId, MeshId)`.

| Field | Meaning |
| --- | --- |
| NetworkId | Owner. Self, a peer, or a messenger server (`MessengerServerEntity.NetworkId`). |
| MeshId | One radio device of that owner (REQ-DIR-1). |
| Subject | `Self`, `Peer`, or `Server`. The host registers which NetworkIds are servers. |
| IsActive | When true, this MeshId is a current destination for that NetworkId. Several ids of one owner may be active together. Inactive rows stay stored and are not used for sending. |
| Confirmed | True when this device was the local radio, or a cleartext frame from that MeshId carried this NetworkId (`0x30`, `0x04`). False when the binding was only inside someone else's snapshot. |
| LastSeenUtc | Last time this binding was learned or refreshed. |

A confirmed observation moves a MeshId to the new owner. An unconfirmed snapshot may add a MeshId and must not remove or replace a confirmed one. A new local device id is added as active; hearing that device again sets `IsActive` back to true.

`SendAsync` to a NetworkId uses every stored MeshId of that owner whose `IsActive` is true. Confirmed ids are tried first.

## REQ-DIR-3 — How the map grows

1. After the serial link is up, the device id from the board (MAC when that is what the board reports) is added under the local NetworkId with subject `Self`, confirmed, `IsActive = true`.
2. An inbound `0x30` or `0x04` from MeshId M whose frame contains NetworkId N adds `(N, M)` as confirmed. Subject is `Server` when the host has registered N as a server, otherwise `Peer`.
3. An inbound `0x32` snapshot is merged: every listed binding is stored under its NetworkId and subject. Confirmed bits in the snapshot do not override a local confirmed row.
4. A cipher frame `0x02` does not name a NetworkId. The host, after it knows the sender, calls `Remember(networkId, meshId)` with the `RemoteAddress` of that receive.

## REQ-DIR-4 — Snapshot frame `0x32`

The snapshot is every stored binding (self, peers, and servers), not only our own radios. Sent when the directory changes and once when the link comes up:

- On the mesh, unicast one snapshot to each **active** MeshId of each peer and each server.
- When no unicast target is known yet, one broadcast carries only the local NetworkId and its own Mesh Ids, so other nodes can learn us. The full map is not broadcast.
- To messenger servers, the host posts the same bytes. This package exposes `EncodeSnapshot()` and does not call HTTP. The server stores the latest snapshot per announcer NetworkId and returns it to clients (new forward kind `MeshDirectory`, or an equivalent read). Implementing that HTTP route stays in ShortP2P / SMT-1.

Cleartext frame `0x32` (so the map is usable before a session exists):

| Offset | Field |
| --- | --- |
| 0 | `0x32` |
| 1–4 | ASCII `SP2M` |
| 5 | version `1` |
| 6–17 | announcer NetworkId, 12 bytes |
| 18–19 | record count, `uint16 BE` |
| repeated | NetworkId 12 \| subject `uint8` (`1` self, `2` peer, `3` server) \| meshCount `uint16 BE` \| then for each MeshId: `meshIdLen uint8` (1–8) \| MeshId bytes \| `IsActive uint8` (`0` or `1`) \| `Confirmed uint8` (`0` or `1`) |

A snapshot larger than one radio payload is split into chunks (REQ-CHUNK-1) and gets the same CRC and retry behavior. The component does not deliver `0x32` to the messenger.

## REQ-TX-PRIO-1 — Outbound TX priority queues

Outbound datagrams are classified into six priorities. Each priority has its own FIFO queue.
The transport TX pump always drains a higher priority before a lower one (1 → 6). An already
in-flight datagram (including its chunk train) finishes under the single TX window before the
next queued item starts. Duty-cycle deferral in `MeshtasticGate` still applies per radio frame
and orders due deferred frames with the same classifier.

| Priority | Class | Rule |
| --- | --- | --- |
| 1 | Control / signaling | Invite `0x30`, handshake `0x01`, session setup `0x04`, presence `0x31`, directory `0x32`, retry `0x33`. |
| 2 | Short text | Text (or default cipher `0x02`) length **≤ 200** bytes (exactly 200 → 2). |
| 3 | Medium text | Text length **> 200** and **&lt; 1000**. |
| 4 | Large text | Text length **≥ 1000** (exactly 1000 → 4). |
| 5 | Small binary | Binary length **≤ 5 KiB** (5120). |
| 6 | Large binary | Binary length **> 5 KiB**. |

Size is the full datagram length enqueued on the transport. Cipher `0x02` defaults to the text
bands because ChatWire is encrypted; hosts may pass `MeshtasticTxContentKind.Binary` (image /
file / transfer) via the `SendAsync` / `SendOpaqueAsync` overloads. Strict priority has no
anti-starvation round-robin in this revision — continuous control traffic can delay text/binary.

---

## Out of scope (here)

- MessengerServer HTTP route and hybrid gateway behavior (the byte layout above is the contract; the route stays in ShortP2P / SMT-1).
- UI for the at-own-risk override flow (REQ-RF-3), beyond storing the selected profile and its caps.
- Replacing ShortP2P framing or crypto.
- A parallel on-air chat protocol.
