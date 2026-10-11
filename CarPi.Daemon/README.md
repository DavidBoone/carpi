# CarPi.Daemon

A deliberately dumb HTTP front end for one ELM327 adapter. It runs on the Pi within BLE range of the car,
owns the adapter, and does nothing else — no scheduling, no logging of vehicle data, no
retries. A remote process (on `lilrook1`, say) connects, sends ELM327 commands, and gets
the adapter's replies back verbatim. All decoding, definitions, and persistence live on
the caller's side.

## Why the boundary is here

`IObdTransport` is char-at-a-time, which is fine over a local UART and hopeless over a
network — a 40-character response would be 40 round trips. The daemon proxies
`IObdSession` instead: one request/response per *command*, with the character-level read
loop staying local to the Pi.

The upshot is that `CarPi.Definitions` and its 465-signal catalog never ship to the Pi.
Fixing a wrong decode formula is a change on the calling side with no redeploy to the car.

## Endpoints

No authentication, by design — the trust boundary is the tailnet/LAN. **Do not expose this
to the internet**; it sends arbitrary commands to a vehicle.

| | |
|---|---|
| `GET /status` | Whether a session holds the adapter, and what it's connected to. Never blocks, so it still answers during a long scan. |
| `GET /scan?seconds=10` | BLE devices the daemon's radio can see, each flagged if the name looks like an OBD2 adapter. Discovery has to happen here — the caller has no radio near the car. Clamped to 1–60s. `409` if a session holds the adapter, since BlueZ discovery and a live GATT link contend for the same radio; it refuses rather than queueing, because a caller waiting behind a DID sweep would just time out. |
| `GET /ports` | Serial ports on this box. Stateless, never blocks. |
| `POST /connect` | Opens the adapter and runs the ELM327 init handshake. Returns a `sessionId`. |
| `POST /command` | Sends one command, returns the response verbatim. Requires the `X-CarPi-Session` header. |
| `POST /disconnect` | Releases the adapter. |

### Status codes

| Code | Meaning |
|---|---|
| `400` | Malformed request — unknown transport, missing port/address |
| `409` | Adapter already held by another session, or a stale/absent session id |
| `503` | **Couldn't reach the adapter.** The expected result when the car is asleep or out of range — distinct from `500` so callers can tell "no car" from "daemon is broken" |

A scan lists devices BlueZ already knows (paired or cached) *as well as* newly discovered
ones. Without that, a bonded adapter is invisible to discovery — BlueZ raises `DeviceFound`
only for devices it hasn't seen before, so the adapter you use every day is exactly the one
a naive scan misses.

### Session rules

One session at a time; a second `POST /connect` gets `409` rather than queueing. Commands
within a session are serialized — an ELM327 is strictly request/response and interleaving
corrupts both replies.

A session that goes quiet for `IdleTimeoutSeconds` (default 300) is reaped, so a client
that crashes mid-session doesn't hold the adapter until the daemon restarts. Any
transport-level failure also drops the session immediately rather than leaving a zombie.

## Running

```bash
dotnet run                                  # http://0.0.0.0:5252
Urls=http://0.0.0.0:9000 dotnet run         # different port
IdleTimeoutSeconds=60 dotnet run            # shorter reap window
```

Binds all interfaces by default — remote access is the entire point.

## Trying it without a car

`"transport": "fake"` gives a canned ELM327: `OK` to AT commands, `NO DATA` otherwise.
Point `address` at a JSON file of `{"COMMAND": "response"}` to script specific replies.
This is how the daemon, the client, and anything built on them get exercised while the
car is asleep — which is most of the time.

```bash
echo '{"222487": "7EC 62 24 87 12 34"}' > canned.json

SID=$(curl -s -X POST localhost:5252/connect -H 'content-type: application/json' \
  -d '{"transport":"fake","address":"canned.json"}' | jq -r .sessionId)

curl -s -X POST localhost:5252/command -H 'content-type: application/json' \
  -H "X-CarPi-Session: $SID" -d '{"command":"222487"}'
# {"response":"7EC 62 24 87 12 34","elapsedMs":251.3}

curl -s -X POST localhost:5252/disconnect -H "X-CarPi-Session: $SID"
```

Note that `System.IO.Ports` will not open a pty, so a socat virtual serial pair is *not* a
usable stand-in for a real adapter — hence the fake transport.

## Real transports

```bash
# BLE (Linux/BlueZ)
curl -X POST localhost:5252/connect -H 'content-type: application/json' \
  -d '{"transport":"ble","address":"AA:BB:CC:DD:EE:FF"}'

# Serial
curl -X POST localhost:5252/connect -H 'content-type: application/json' \
  -d '{"transport":"serial","port":"/dev/ttyUSB0","baud":38400}'
```

## Deploying to the Pi

On pi0 the daemon runs as a **`systemd --user` unit** at
`~/.config/systemd/user/carpi-daemon.service`, and `loginctl enable-linger` starts it
at boot without root. Deploy = publish here,
`rsync` the output, `systemctl --user restart carpi-daemon`. BlueZ is reachable over the
session bus, so BLE needs no group changes.

See [../PI0-HOWTO.md](../PI0-HOWTO.md) for cross-compiling to ARM64. Native AOT is a
closed question — don't retry: cross-compiling x64→arm64 with `-p:PublishAot=true` builds
and runs (17.5 MB, 27 MB RSS on pi0), but `Linux.Bluetooth` goes through `Tmds.DBus`, whose
`Reflection.Emit` proxies die at runtime under AOT, so BLE is dead. `DaemonJsonContext.cs`
came out of that attempt and stays regardless. Verified 2026-08-19; see
[../CLAUDE.md](../CLAUDE.md).
