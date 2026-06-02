# CarPi Development Guide

## Project Overview

CarPi is a .NET CLI tool for connecting to ELM327 OBD2 adapters via serial, classic Bluetooth, or BLE, running on Raspberry Pi Zero (and development on macOS).

> **Status:** Hobby/experimental, not production-ready. There is no automated test suite yet. Some features are incomplete (see "Known Limitations" below).

## Solution Structure

Three projects (`carpi.sln`):
- **CarPi.Cli** (`CarPi.Cli/CarPi.csproj`) — entry point, command-line surface (System.CommandLine).
- **CarPi.Obd** (`CarPi.Obd/CarPi.Obd.csproj`) — OBD/ELM327 transports (serial, BLE), DID scanner, PID query, equation evaluator.
- **CarPi.Definitions** (`CarPi.Definitions/CarPi.Definitions.csproj`) — canonical PID/signal definition model, loaders, normalizer, validator.

## Build & Run

### Build
```bash
dotnet build                    # Debug build
dotnet build -c Release         # Release build
```

### Run
The CLI uses subcommands (System.CommandLine). Run from `CarPi.Cli/`:
```bash
dotnet run -- --help                        # Show all commands and options
dotnet run -- list-ports                    # List available serial ports
dotnet run -- scan-bluetooth                # Scan for paired classic-Bluetooth adapters
dotnet run -- scan-ble                      # Scan for BLE OBD2 adapters
dotnet run -- query --port /dev/ttyUSB0     # Query PIDs over serial
dotnet run -- query --ble AA:BB:CC:DD:EE:FF # Query PIDs over BLE (MAC on Linux, CoreBluetooth UUID on macOS)
dotnet run -- probe-ecus --ble <id>         # Check which known ECUs are online
dotnet run -- scan-dids 7E4 0000 FFFF       # Scan a DID range on an ECU
dotnet run -- report scan_results.json      # Generate HTML report from scan results
```
Definition-tooling commands (no adapter needed): `import-csv`, `normalize-defs`, `validate`.

Global option `-v`/`-vv`/`-vvv` sets verbosity (Info/Debug/Trace).

### Tests
No test project exists yet. Do not assume `dotnet test` works.

## Code Style Guidelines

### General Principles
- **Avoid over-engineering**: Add abstractions only when they serve a specific purpose (swappable implementations, testing boundaries)
- **No interfaces for everything**: Use concrete types by default
- **Keep it simple**: Prefer simple, readable code over clever solutions
- **No comments unless requested**: Code should be self-documenting

### Imports
- Use explicit `using` statements (no implicit global usings unless necessary)
- Order: System namespaces first, then third-party, then project-specific
- Example:
```csharp
using System.IO.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
```

### Formatting
- 4 spaces for indentation (no tabs)
- Opening brace on same line: `if (condition) {`
- One statement per line
- Vertical spacing: blank line between logical sections
- Line length: try to keep under 120 characters

### Naming Conventions
- **Classes/Methods**: PascalCase (`ObdDevice`, `ConnectAsync`)
- **Fields/Variables**: camelCase (`_serial`, `bluetoothMac`)
- **Parameters**: camelCase (`string port`, `int baudRate`)
- **Constants**: PascalCase (`MaxRetries`)
- **Private fields**: prefix with underscore (`_serial`)

### Types
- Use explicit types over `var` for method signatures and public APIs
- Use `var` for local variables when type is obvious
- Use nullable reference types (`string?`) for potentially null values
- Prefer value types for simple data (int, bool, etc.)

### Error Handling
- Use try-catch for recoverable errors
- Catch specific exceptions, not generic `Exception`
- Log errors with context before rethrowing or returning error codes
- Use `CancellationToken` for async operations that need cancellation support

### Async/Await
- Use `async`/`await` for all I/O operations (serial, file, network)
- Always pass `CancellationToken` to async methods
- Use `Task.Delay` instead of `Thread.Sleep`
- Avoid blocking on async code (no `.Wait()` or `.Result`)

### Configuration
- Support multiple config sources in order: JSON file, environment variables, command-line args
- Use prefix for env vars: `CARPI_SERIALPORT`, `CARPI_BLUETOOTHMAC`
- Provide sensible defaults in code

### Logging
- Use `ILogger` from Microsoft.Extensions.Logging
- Log at appropriate levels: Debug for verbose, Information for normal, Warning for recoverable issues, Error for failures
- Include relevant context in log messages

### Cross-Platform Considerations
- Use `OperatingSystem.IsMacOS()`, `OperatingSystem.IsLinux()` for platform-specific code
- Use `System.IO.Ports.SerialPort` for cross-platform serial communication
- BLE is platform-split: **Linux** uses `Linux.Bluetooth` (BlueZ D-Bus); **macOS** uses `Plugin.BLE` via a `net10.0-maccatalyst` target (CoreBluetooth). Package refs are conditioned per-TFM in `CarPi.Obd.csproj`.
- Handle path separators appropriately (always use `/` in config, code handles OS differences)

### Transport Architecture
- `IObdTransport` interface abstracts serial vs BLE communication
- `SerialObdTransport` wraps `System.IO.Ports.SerialPort`
- `BleObdTransport` (Linux) uses `Linux.Bluetooth` (BlueZ D-Bus) with `Channel<char>` for notification buffering
- `MacBleObdTransport` (macOS/Mac Catalyst) uses `Plugin.BLE` (CoreBluetooth); `MacRunLoopHelper` drives the CoreBluetooth run loop
- ELM327 BLE GATT: service FFF0 (or FFE0), write to FFF2 (or FFE1), notify on FFF1 (or FFE1)
- Both transports implement `IAsyncDisposable` to avoid async-over-sync deadlocks

### Serial Communication
- Always configure ReadTimeout and WriteTimeout
- ELM327 uses `\r` as command terminator (set `NewLine = "\r"`)
- Add small delays (100-200ms) between commands for ELM327 response time

### Git Conventions
- Commit message format: Present tense, imperative mood (e.g., "Add feature" not "Added feature")
- One logical change per commit
- Reference issue numbers in commit body if applicable

## Known Limitations
- **OBD protocol is hardcoded** to `ATSP6` (ISO 15765-4 CAN, 500kbps, 11-bit) in `ObdDevice.cs`. Correct for the Chevy Bolt / most modern GM, but not configurable yet — other vehicles may need a different protocol.
- **Equation evaluator does not support cross-references.** `EquationEvaluator` substitutes `0` for `[XXXXXX]` bracket references (`EquationEvaluator.cs`), so any signal whose formula depends on another PID evaluates silently to a wrong value rather than erroring.
- **No automated tests.**
- Seed definitions cover the Chevy Bolt only; the canonical model is not yet exercised against a second vehicle (see `TODO.md`).
