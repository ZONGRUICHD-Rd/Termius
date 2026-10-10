# Conduit wire protocol (v1)

Phone ⇄ host over a WebSocket, either direct (`ws://<host>:47821/v1/connect`) or through a relay
(`wss://<relay>/v1/connect/<hostId>`). The relay only pipes frames; everything after the handshake is
end-to-end encrypted between the phone and the host.

Reference implementations: `windows/src/Conduit.Host.Core` (host), `ios/ConduitKit` (phone),
`windows/tests/Conduit.Host.Core.Tests/TestClient.cs` (minimal client).

## 1. Pairing

The host shows `conduit://pair?d=<base64url(JSON)>`:

```json
{ "v": 1, "id": "<hostId>", "n": "DESKTOP-01", "k": "<base64 X9.63 P-256 key-agreement pubkey>",
  "t": "<one-time pairing token>", "e": ["ws://192.168.1.20:47821", "ws://100.88.1.2:47821"],
  "r": "wss://relay.example.com" }
```

`hostId = base64url(SHA-256(hostSigningPub))[0..22]`. The token is valid for 30 minutes and is consumed by the
first device that pairs. The phone stores `k`, endpoints and relay; the host stores the phone's static public key.

## 2. Handshake (text frames)

```
phone → host  {"type":"hello","v":1,"hostId","name","clientStatic","clientEph","nonce"(16B),"pairToken"?}
host  → phone {"type":"welcome","v":1,"hostId","hostName","hostEph","nonce"(16B),"paired":bool,"route"}
           or {"type":"reject","reason":"unpaired|wrong_host|version|malformed","message"}
```

Keys are base64 X9.63 (65 bytes). An unknown `clientStatic` is accepted only with a valid `pairToken`.

```
ee  = ECDH(clientEph,    hostEph)       forward secrecy
es  = ECDH(clientEph,    hostStatic)    only the real host can derive this
ss  = ECDH(clientStatic, hostStatic)    only a paired phone can derive this
okm = HKDF-SHA256(ikm = ee‖es‖ss, salt = nonceC‖nonceH, info = "conduit/v1"‖clientEph‖hostEph, L = 64)
c2h = okm[0..32]   h2c = okm[32..64]
```

A shared test vector lives in `CryptoTests.cs` and `ConduitKitTests.swift` (also checked against Python `cryptography`).

## 3. Encrypted frames (binary)

`counter (8 bytes, big endian) ‖ AES-256-GCM ciphertext ‖ tag (16)`, nonce = `00000000 ‖ counter`.
Each direction starts at 0 and must increase by exactly 1; anything else closes the connection.

Plaintext is UTF-8 JSON:

```
request   {"t":"req","id":7,"m":"session.send","p":{…}}
response  {"t":"res","id":7,"ok":true,"r":{…}}  |  {"t":"res","id":7,"ok":false,"err":"…","code":"not_found"}
event     {"t":"evt","e":"session.event","p":{…}}
```

### Methods

| method | params | result |
|---|---|---|
| `ping` | – | `{now}` |
| `host.info` / `agents.refresh` | – | `{hostId, hostName, version, os, agents[], endpoints[], relay, apns}` |
| `sessions.list` | – | `{sessions: Summary[]}` |
| `session.create` | `{agent, cwd, title?, model?, prompt?}` | `{session}` |
| `session.send` | `{sessionId, text}` | `{}` (steers if a turn is running) |
| `session.interrupt` / `session.stop` / `session.delete` | `{sessionId}` | `{}` |
| `session.rename` | `{sessionId, title}` | `{}` |
| `session.subscribe` | `{sessionId, since}` | `{events: Logged[], lastSeq, session}` then live `session.event`s |
| `session.unsubscribe` | `{sessionId}` | `{}` |
| `approval.respond` | `{sessionId, approvalId, decision: allow\|allow_always\|deny}` | `{}` |
| `fs.list` | `{path?}` | `{path, parent, home, dirs[{name,path,isRepo}], roots[], recents[]}` |
| `push.register` | `{token, env: sandbox\|production, bundleId}` | `{apns}` |

`session.subscribe` is atomic with the event stream: every event after the returned backfill is delivered
live and after the response, so `since = lastSeq` resumes without gaps or duplicates.

### Events

- `session.event` `{sessionId, seq, ts, ev}` — only for subscribed sessions
- `session.summary` `{session: Summary}` — to every connected phone (drives the list and the Dynamic Island)
- `session.removed` `{sessionId}`

`Summary = {id, agent, title, cwd, state, detail, done, total, turnStartedAt, turnEndedAt, createdAt, updatedAt, preview, approvals, lastSeq}`;
`state ∈ starting | running | waiting | idle | error | stopped`.

## 4. Normalised agent events (`ev.k`)

| k | fields | notes |
|---|---|---|
| `user` | `text` | |
| `delta` | `mid, text` | append to assistant message `mid` (host coalesces to ≤ 30/s) |
| `message` | `mid, text` | authoritative text, replaces deltas |
| `reasoning` | `mid, text, delta` | |
| `tool` | `tid, name, title, status: running\|ok\|error, input?, output?` | partial updates merge by `tid` |
| `approval` | `aid, title, detail, kind, always` | |
| `approval_done` | `aid, decision` | |
| `plan` | `items[{text, status: pending\|in_progress\|completed}]` | latest wins |
| `status` | `state, detail?` | |
| `error`, `notice` | `text` | |

The host log is compacted (deltas dropped once the message lands, tool updates merged, single plan/status),
persisted to disk, and keeps its sequence numbers across restarts.

## 5. Relay

```
host control  GET /v1/host?id&pub&ts&sig            relay → host: {"type":"incoming","conn":"<id>"}
host data     GET /v1/accept/<conn>?id&pub&ts&sig    bridged 1:1 with the waiting phone
phone         GET /v1/connect/<hostId>               close 4404 host offline, 4408 host timeout
```

`sig = ECDSA-P256-SHA256("conduit-relay-v1|<id>|<ts>")` (IEEE P1363), `id` must equal the hash of `pub`, `ts` within ±5 min.
