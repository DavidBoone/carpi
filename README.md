# CarPi

CarPi reads live data out of a Chevy Bolt over OBD2 — battery state of charge and
capacity, cell voltages, temperatures, charge status — by talking to a cheap ELM327
adapter over serial or BLE. It's a .NET 10 CLI plus a canonical PID/signal catalog,
built to run headless on a Raspberry Pi Zero 2 W within BLE range of the parked car so the numbers that
matter (chiefly battery health over time) can be logged instead of guessed at.

> Hobby/experimental. No automated tests yet, and some pieces are incomplete — see
> the "Known Limitations" section of [AGENTS.md](AGENTS.md).

## Solution layout

| Project | What it is |
|---|---|
| `CarPi.Cli` | Entry point and command surface (System.CommandLine) |
| `CarPi.Obd` | ELM327 transports (serial / BLE), DID scanner, PID query, equation evaluator |
| `CarPi.Definitions` | Canonical PID + signal model, loaders, normalizer, validator |
| `CarPi.Daemon` | HTTP front end for one adapter — runs on the Pi beside the car ([README](CarPi.Daemon/README.md)) |
| `CarPi.Client` | `RemoteObdSession` — talks to a daemon, implements the same `IObdSession` |

BLE is platform-split: Linux uses `Linux.Bluetooth` (BlueZ over D-Bus), macOS uses
`Plugin.BLE` (CoreBluetooth) via a `net10.0-maccatalyst` target. Building on Linux
only builds the `net10.0` target, so the macOS half is simply skipped.

## Build

```bash
dotnet build                 # Debug
dotnet build -c Release
```

Requires the .NET 10 SDK. `dotnet build` restores and builds all five projects.

## Run

From `CarPi.Cli/`:

```bash
dotnet run -- --help                          # all commands and options
dotnet run -- list-ports                      # serial ports
dotnet run -- scan-ble                        # find BLE OBD2 adapters
dotnet run -- query --port /dev/ttyUSB0       # query PIDs over serial
dotnet run -- query --ble AA:BB:CC:DD:EE:FF   # query PIDs over BLE
dotnet run -- probe-ecus --ble <id>           # which known ECUs answer
dotnet run -- scan-dids 7E4 0000 FFFF         # sweep a DID range on one ECU
dotnet run -- report scan_results.json        # HTML report from scan output
```

`-v` / `-vv` / `-vvv` raises verbosity to Info / Debug / Trace.

### Against a remote adapter

Add `--daemon <url>` to any of the adapter commands and the ELM327 conversation happens on
that daemon's box instead of locally. `--ble` / `--port` then describe what the *daemon*
should open:

```bash
dotnet run -- --daemon http://carpi.local:5252 --ble AA:BB:CC:DD:EE:FF
dotnet run -- probe-ecus --daemon http://carpi.local:5252 --ble AA:BB:CC:DD:EE:FF
dotnet run -- scan-dids 7E4 0000 FFFF --daemon http://carpi.local:5252 --ble AA:BB:CC:DD:EE:FF
```

Definitions, decoding, and output stay local — only commands and replies cross the network.

Exit code is `1` when the adapter couldn't be reached (car asleep, daemon down) and `0` on
success, so a scheduled pull can tell "no car" from "collected nothing".

### Without a car (or an adapter)

The definition-tooling commands touch no hardware and are the way to exercise the
catalog on a dev box:

```bash
dotnet run -- import-csv Chevrolet_Bolt_PIDs.csv    # legacy CSV -> canonical JSON
dotnet run -- normalize-defs Chevrolet_Bolt_PIDs.json
dotnet run -- validate Chevrolet_Bolt_PIDs.json     # 465 signals, currently valid
```

## Remote operation

The Pi runs `CarPi.Daemon`, which owns the adapter and does nothing else — no
scheduling, no logging, no retries. A process elsewhere (on `lilrook1`) drives it through
`CarPi.Client`:

```csharp
var request = new ConnectRequest { Transport = "ble", Address = "AA:BB:CC:DD:EE:FF" };
await using var session = new RemoteObdSession(new Uri("http://carpi.local:5252"), request, logger);
await session.ConnectAsync(ct);

var query = new BoltPidQuery(session, logger);   // same code path as a local adapter
```

The CLI's `--daemon` flag is exactly this, wired up — useful for poking at the car by hand.

`RemoteObdSession` implements `IObdSession`, so `BoltPidQuery` and `DidScanner` can't tell
the difference. Decoding and the signal catalog stay on the calling side; the Pi only
relays ELM327 commands and replies.

Periodic collection, storage, and retry policy are the caller's job on purpose. When the
car is asleep, `ConnectAsync` throws a `RemoteObdException` with `AdapterUnavailable` set —
that's the normal case, not a fault.

## Deployment

The Pi Zero 2 W (aarch64) is the intended host. [PI0-HOWTO.md](PI0-HOWTO.md) has generic notes on
AOT cross-compiling .NET to ARM64 via Docker — **they came from a different project**
and are reference material, not CarPi's actual build configuration.

## Docs

- [AGENTS.md](AGENTS.md) — code style, transport architecture, known limitations
- [TODO.md](TODO.md) — open threads on the canonical model
