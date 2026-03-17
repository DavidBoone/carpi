using System.IO.Ports;
using System.Text;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "CARPI_")
    .AddCommandLine(args, new Dictionary<string, string>
    {
        { "--port", "SerialPort" },
        { "--baud", "BaudRate" },
        { "--bluetooth-mac", "BluetoothMac" },
        { "--ble", "BleMac" },
        { "--csv", "PidCsv" },
        { "--defs", "PidCsv" },
        { "--pid-filter", "PidFilter" }
    });

var configuration = configBuilder.Build();

var appConfig = new AppConfig();
configuration.Bind(appConfig);

int verbosity = args.Count(a => a == "-v")
    + (args.Contains("-vv") ? 2 : 0)
    + (args.Contains("-vvv") ? 3 : 0);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddConfiguration(configuration.GetSection("Logging"))
        .AddSimpleConsole(options =>
        {
            options.IncludeScopes = false;
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });

    // Default: Warning+ only (quiet — just console output for results)
    // -v:   Info   (connect milestones, adapter identity)
    // -vv:  Debug  (ECU skips, NRC decodes, loading details)
    // -vvv: Trace  (every AT command, every response, stale '>' handling)
    var minLevel = verbosity switch
    {
        >= 3 => LogLevel.Trace,
        2 => LogLevel.Debug,
        1 => LogLevel.Information,
        _ => LogLevel.Warning
    };
    builder.SetMinimumLevel(minLevel);
});

var logger = loggerFactory.CreateLogger<Program>();

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("CarPi OBD2 CLI Tool");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  CarPi --list-ports              List available serial ports");
    Console.WriteLine("  CarPi --scan-bluetooth          Scan for paired Bluetooth OBD2 adapters");
    Console.WriteLine("  CarPi --scan-ble                Scan for BLE OBD2 adapters (Linux only)");
    Console.WriteLine("  CarPi --port <port>             Connect via serial port");
    Console.WriteLine("  CarPi --ble <mac>               Connect via BLE to device MAC address");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --port <port>       Serial port (e.g., /dev/ttyUSB0, COM3, /dev/rfcomm0)");
    Console.WriteLine("  --baud <rate>       Baud rate (default: 38400)");
    Console.WriteLine("  --ble <mac>         BLE device MAC address (Linux only)");
    Console.WriteLine("  --list-ports        List available serial ports");
    Console.WriteLine("  --scan-bluetooth    Scan for paired Bluetooth OBD2 devices");
    Console.WriteLine("  --scan-ble          Scan for BLE OBD2 devices (Linux only)");
    Console.WriteLine("  --bluetooth-mac <x> Bluetooth MAC for RFCOMM auto-binding on Linux");
    Console.WriteLine("  --csv <path>        PID definitions file (.csv or .json)");
    Console.WriteLine("  --defs <path>       Alias for --csv");
    Console.WriteLine("  --pid-filter <f>    PID priority filter (default: *!)");
    Console.WriteLine("                      * = common, ! = important, + = cell voltages");
    Console.WriteLine("                      - = advanced, ? = unknown, all = everything");
    Console.WriteLine("  --probe-ecus        Check which known ECUs are online");
    Console.WriteLine("  --scan-dids <header> <start> <end>  Scan DID range on an ECU");
    Console.WriteLine("                      e.g., --scan-dids 7E4 0000 FFFF");
    Console.WriteLine("  --scan-dids-all     Scan common DID ranges on all known ECUs");
    Console.WriteLine("  --scan-output <f>   Output file for DID scan results (default: scan_results.json)");
    Console.WriteLine("  --report [json]     Generate HTML report from scan results (default: scan_results.json)");
    Console.WriteLine("                      Outputs scan_report.html (no vehicle connection needed)");
    Console.WriteLine("  --import-csv <f>    Import legacy PID CSV and write canonical JSON");
    Console.WriteLine("  --normalize-defs <f> Normalize canonical JSON seed data");
    Console.WriteLine("  --validate <f>      Validate canonical JSON definitions");
    Console.WriteLine("  --out <f>           Output path for --import-csv (default: Chevrolet_Bolt_PIDs.json)");
    Console.WriteLine("  --vehicle <name>    Vehicle name metadata for --import-csv");
    Console.WriteLine("  -v                  Info logging (connect milestones)");
    Console.WriteLine("  -vv                 Debug logging (ECU skips, NRC decodes)");
    Console.WriteLine("  -vvv                Trace logging (AT commands, raw responses)");
    return 0;
}

if (args.Contains("--list-ports"))
{
    Console.WriteLine("Available serial ports:");
    var ports = SerialPort.GetPortNames();
    if (ports.Length == 0)
    {
        Console.WriteLine("  (none found)");
    }
    else
    {
        foreach (var p in ports)
        {
            Console.WriteLine($"  {p}");
        }
    }
    return 0;
}

if (args.Contains("--scan-ble"))
{
    if (!OperatingSystem.IsLinux())
    {
        Console.Error.WriteLine("BLE scanning requires Linux with BlueZ.");
        return 1;
    }

    Console.WriteLine("Scanning for BLE devices (10 seconds)...");
    Console.WriteLine();

    var adapters = await BlueZManager.GetAdaptersAsync();
    if (adapters.Count == 0)
    {
        Console.Error.WriteLine("No Bluetooth adapters found.");
        return 1;
    }

    var adapter = adapters[0];
    var found = new List<(string Address, string Name)>();

    adapter.DeviceFound += async (Adapter sender, DeviceFoundEventArgs eventArgs) =>
    {
        var device = eventArgs.Device;
        var address = await device.GetAddressAsync();
        var name = "(unknown)";
        try { name = await device.GetNameAsync(); } catch { }

        if (!found.Any(f => f.Address == address))
        {
            found.Add((address, name));
            var marker = IsLikelyObd(name) ? " <-- possible OBD2 adapter" : "";
            Console.WriteLine($"  {address}  {name}{marker}");
        }
    };

    await adapter.StartDiscoveryAsync();
    await Task.Delay(10000);
    await adapter.StopDiscoveryAsync();

    if (found.Count == 0)
    {
        Console.WriteLine("  (no devices found)");
    }

    Console.WriteLine();
    Console.WriteLine("Use --ble <MAC> to connect to a device.");
    return 0;
}

if (args.Contains("--import-csv"))
{
    var inputPath = GetArgumentValue(args, "--import-csv");
    if (string.IsNullOrWhiteSpace(inputPath))
    {
        Console.Error.WriteLine("Usage: --import-csv <input.csv> [--out output.json] [--vehicle \"Vehicle Name\"]");
        return 1;
    }

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"CSV file not found: {inputPath}");
        return 1;
    }

    var outputPath = GetArgumentValue(args, "--out") ?? "Chevrolet_Bolt_PIDs.json";
    var vehicleName = GetArgumentValue(args, "--vehicle") ?? "";
    var pids = BoltPidLoader.LoadFromCsv(inputPath);
    var catalog = DefinitionCatalog.FromBoltPids(pids, vehicleName);
    catalog.SaveToJson(outputPath);

    Console.WriteLine($"Imported {pids.Count} PID rows from {inputPath}");
    Console.WriteLine($"Wrote canonical JSON to {outputPath}");
    return 0;
}

if (args.Contains("--normalize-defs"))
{
    var inputPath = GetArgumentValue(args, "--normalize-defs");
    if (string.IsNullOrWhiteSpace(inputPath))
    {
        Console.Error.WriteLine("Usage: --normalize-defs <input.json> [--out output.json]");
        return 1;
    }

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Definitions file not found: {inputPath}");
        return 1;
    }

    var outputPath = GetArgumentValue(args, "--out") ?? inputPath;
    var catalog = DefinitionCatalog.LoadFromJson(inputPath);
    var normalized = DefinitionCatalogNormalizer.NormalizeSeedCatalog(catalog);
    normalized.SaveToJson(outputPath);

    Console.WriteLine($"Normalized canonical JSON from {inputPath}");
    Console.WriteLine($"Wrote normalized JSON to {outputPath}");
    return 0;
}

if (args.Contains("--validate"))
{
    var inputPath = GetArgumentValue(args, "--validate");
    if (string.IsNullOrWhiteSpace(inputPath))
    {
        Console.Error.WriteLine("Usage: --validate <definitions.json>");
        return 1;
    }

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Definitions file not found: {inputPath}");
        return 1;
    }

    if (!inputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Validation requires a canonical JSON file, got: {inputPath}");
        return 1;
    }

    var catalog = DefinitionCatalog.LoadFromJson(inputPath);
    var result = DefinitionCatalogValidator.Validate(catalog);

    Console.WriteLine($"Validated {catalog.Signals.Count} signals from {inputPath}");
    Console.WriteLine(result.Summary());

    if (!result.IsValid)
    {
        Console.WriteLine();
        foreach (var error in result.Errors)
            Console.WriteLine($"  [{error.Area}] {error.SignalId}: {error.Message}");
        return 1;
    }

    return 0;
}

if (args.Contains("--report"))
{
    var jsonFile = GetArgumentValue(args, "--report") ?? "scan_results.json";
    var definitionPath = ResolveDefinitionPath(GetArgumentValue(args, "--defs") ?? GetArgumentValue(args, "--csv"));

    var report = new DidReportGenerator();
    if (File.Exists(definitionPath))
    {
        report.LoadKnownPids(definitionPath);
        Console.WriteLine($"Loaded known PIDs from {definitionPath}");
    }

    await report.GenerateAsync(jsonFile, "scan_report.html");
    return 0;
}

if (args.Contains("--scan-bluetooth"))
{
    Console.WriteLine("Scanning for Bluetooth OBD2 adapters...");
    Console.WriteLine();

    var isMac = OperatingSystem.IsMacOS();
    var isLinux = OperatingSystem.IsLinux();

    if (isMac)
    {
        Console.WriteLine("Platform: macOS");
        Console.WriteLine();
        Console.WriteLine("Looking for paired Bluetooth devices that may be OBD2 adapters...");

        var ttyPorts = SerialPort.GetPortNames()
            .Where(p => p.Contains("Bluetooth") || p.Contains("Serial"))
            .ToList();

        if (ttyPorts.Any())
        {
            Console.WriteLine("Possible Bluetooth serial ports:");
            foreach (var p in ttyPorts)
            {
                Console.WriteLine($"  {p}");
            }
        }
        else
        {
            Console.WriteLine("No Bluetooth serial ports found.");
            Console.WriteLine();
            Console.WriteLine("To use a Bluetooth OBD2 adapter on macOS:");
            Console.WriteLine("1. Pair the device in System Preferences > Bluetooth");
            Console.WriteLine("2. Set up as 'Serial Port Device' in Bluetooth settings");
            Console.WriteLine("3. The port will appear here (e.g., /dev/cu.Bluetooth-MAC)");
        }
    }
    else if (isLinux)
    {
        Console.WriteLine("Platform: Linux");
        Console.WriteLine();

        if (Directory.Exists("/dev/rfcomm"))
        {
            var rfcommPorts = Directory.GetFiles("/dev/rfcomm");
            if (rfcommPorts.Any())
            {
                Console.WriteLine("RFCOMM devices:");
                foreach (var p in rfcommPorts)
                {
                    Console.WriteLine($"  {p}");
                }
            }
            else
            {
                Console.WriteLine("No RFCOMM devices bound.");
                Console.WriteLine();
                Console.WriteLine("To connect a Bluetooth OBD2 adapter:");
                Console.WriteLine("1. Pair: bluetoothctl -> pair <MAC>");
                Console.WriteLine("2. The CLI will auto-bind when connecting with --bluetooth-mac");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Also checking for USB serial adapters...");
        var usbPorts = SerialPort.GetPortNames()
            .Where(p => p.Contains("USB") || p.Contains("ACM"))
            .ToList();

        if (usbPorts.Any())
        {
            Console.WriteLine("USB serial ports:");
            foreach (var p in usbPorts)
            {
                Console.WriteLine($"  {p}");
            }
        }
    }
    else
    {
        Console.WriteLine("Unknown platform - checking serial ports anyway:");
        foreach (var p in SerialPort.GetPortNames())
        {
            Console.WriteLine($"  {p}");
        }
    }

    return 0;
}

// Determine transport mode: BLE or serial
bool useBle = !string.IsNullOrEmpty(appConfig.BleMac);
string? port = appConfig.SerialPort;

if (!useBle && string.IsNullOrEmpty(port))
{
    Console.Error.WriteLine("Error: No connection specified. Use --port <port> or --ble <mac>.");
    return 1;
}

IObdTransport transport;
bool boundRfcomm = false;

try
{
    if (useBle)
    {
        logger.LogInformation("Connecting to BLE device {Mac}", appConfig.BleMac);
        transport = new BleObdTransport(appConfig.BleMac, logger);
    }
    else
    {
        logger.LogInformation("Connecting to OBD2 device on {Port}", port);

        string? bluetoothMac = appConfig.BluetoothMac;
        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(bluetoothMac) && port!.StartsWith("/dev/rfcomm"))
        {
            Console.WriteLine($"Binding Bluetooth RFCOMM to {bluetoothMac}...");
            var bindResult = await RunRfcommBindAsync(bluetoothMac, port);
            if (!bindResult)
            {
                Console.Error.WriteLine("Failed to bind RFCOMM device. Is the car in range?");
                return 1;
            }
            boundRfcomm = true;
            await Task.Delay(500);
        }

        transport = new SerialObdTransport(port!, appConfig.BaudRate, appConfig.ReadTimeout, appConfig.WriteTimeout);
    }

    await using var obd = new ObdDevice(transport, logger);

    await obd.ConnectAsync(CancellationToken.None);
    logger.LogInformation("Connected");

    // ECU probe mode
    if (args.Contains("--probe-ecus"))
    {
        var scanner = new DidScanner(obd, logger);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DataSnapshot
        {
            Timestamp = now,
            Command = "probe-ecus"
        };

        Console.WriteLine("Probing ECU addresses...");

        foreach (var (header, name) in DidScanner.ProbeAddresses)
        {
            var alive = await scanner.ProbeEcuAsync(header, CancellationToken.None);
            var status = alive ? "ONLINE" : "no response";
            Console.WriteLine($"  {header}  {name,-30} {status}");
            snapshot.Ecus.Add(new EcuStatus { Header = header, Name = name, Online = alive });
        }

        var snapshotPath = DataSnapshot.GenerateFileName(now);
        snapshot.Save(snapshotPath);
        logger.LogInformation("Snapshot saved to {Path}", snapshotPath);
        logger.LogDebug("Disconnecting");
        await obd.DisconnectAsync();
        return 0;
    }

    // DID scanning mode
    if (args.Contains("--scan-dids-all") || args.Contains("--scan-dids"))
    {
        var scanOutput = GetArgumentValue(args, "--scan-output") ?? "scan_results.json";
        var scanner = new DidScanner(obd, logger);
        var now = DateTimeOffset.UtcNow;

        // Load definitions for cross-referencing discovered DIDs
        var scanDefsPath = ResolveDefinitionPath(appConfig.PidCsv);
        var knownDids = new Dictionary<string, List<ResolvedPid>>(StringComparer.OrdinalIgnoreCase);
        string? scanDefsSource = null;

        if (File.Exists(scanDefsPath))
        {
            var catalog = scanDefsPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? DefinitionCatalog.LoadFromJson(scanDefsPath)
                : DefinitionCatalog.FromBoltPids(BoltPidLoader.LoadFromCsv(scanDefsPath));

            foreach (var rp in catalog.ToResolvedPids())
            {
                if (!rp.Pid.IsUds) continue;
                var key = $"{rp.Pid.Header}:{rp.Pid.ModeAndPid[2..]}";
                if (!knownDids.TryGetValue(key, out var list))
                {
                    list = [];
                    knownDids[key] = list;
                }
                list.Add(rp);
            }

            scanDefsSource = Path.GetFileName(scanDefsPath);
            Console.WriteLine($"Loaded {knownDids.Count} known DIDs from {scanDefsPath}");
        }

        var snapshot = new DataSnapshot
        {
            Timestamp = now,
            Command = args.Contains("--scan-dids-all") ? "scan-dids-all" : "scan-dids",
            DefinitionSource = scanDefsSource
        };

        void OnDidFound(string ecu, string did, string dataBytes, int byteCount)
        {
            var key = $"{ecu}:{did}";
            if (knownDids.TryGetValue(key, out var matches))
            {
                // Parse data bytes for decoding
                var bytes = dataBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(h => h.Length == 2)
                    .Select(h => Convert.ToByte(h, 16))
                    .ToArray();

                foreach (var rp in matches)
                {
                    double? value = null;
                    try
                    {
                        value = rp.Pid.Decoder != null
                            ? rp.Pid.Decoder.Evaluate(bytes)
                            : !string.IsNullOrWhiteSpace(rp.Pid.Equation)
                                ? EquationEvaluator.Evaluate(rp.Pid.Equation, bytes)
                                : null;
                    }
                    catch { }

                    snapshot.Readings.Add(new SignalReading
                    {
                        SignalId = rp.SignalId,
                        BindingId = rp.BindingId,
                        PresentationId = rp.PresentationId,
                        Ecu = ecu,
                        Did = did,
                        Name = rp.Pid.ShortName,
                        Value = value,
                        Units = rp.Pid.Units,
                        FormattedValue = value.HasValue ? $"{value.Value:F2} {rp.Pid.Units}".Trim() : null,
                        DataBytes = dataBytes,
                        ByteCount = byteCount
                    });
                }
            }
            else
            {
                snapshot.Readings.Add(new SignalReading
                {
                    Ecu = ecu,
                    Did = did,
                    Name = $"{ecu}:22{did}",
                    DataBytes = dataBytes,
                    ByteCount = byteCount
                });
            }
        }

        if (args.Contains("--scan-dids-all"))
        {
            Console.WriteLine("Scanning common DID ranges on all known ECUs...");
            Console.WriteLine("(This will take a while)");
            Console.WriteLine();

            foreach (var (ecuHeader, ecuName) in DidScanner.KnownEcus)
            {
                Console.WriteLine($"=== {ecuName} ({ecuHeader}) ===");
                foreach (var (start, end, desc) in DidScanner.CommonRanges)
                {
                    Console.WriteLine($"  Range: {desc} ({start:X4}-{end:X4})");
                    await scanner.ScanAsync(ecuHeader, start, end, scanOutput, OnDidFound, CancellationToken.None);
                }
                Console.WriteLine();
            }
        }
        else
        {
            var scanHeader = GetArgumentValue(args, "--scan-dids");
            if (string.IsNullOrEmpty(scanHeader))
            {
                Console.Error.WriteLine("Usage: --scan-dids <header> <start> <end>");
                Console.Error.WriteLine("Known ECUs:");
                foreach (var (h, n) in DidScanner.KnownEcus)
                    Console.Error.WriteLine($"  {h}  {n}");
                return 1;
            }

            // Parse start/end from remaining args after the header
            var headerIdx = Array.IndexOf(args, "--scan-dids");
            if (headerIdx + 3 >= args.Length)
            {
                Console.Error.WriteLine("Usage: --scan-dids <header> <start-hex> <end-hex>");
                return 1;
            }

            var startHex = args[headerIdx + 2];
            var endHex = args[headerIdx + 3];
            var startDid = Convert.ToInt32(startHex, 16);
            var endDid = Convert.ToInt32(endHex, 16);

            await scanner.ScanAsync(scanHeader, startDid, endDid, scanOutput, OnDidFound, CancellationToken.None);
        }

        var snapshotPath = DataSnapshot.GenerateFileName(now);
        snapshot.Save(snapshotPath);
        logger.LogInformation("Snapshot saved to {Path}", snapshotPath);
        logger.LogDebug("Disconnecting");
        await obd.DisconnectAsync();
        return 0;
    }

    var defsPath = ResolveDefinitionPath(appConfig.PidCsv);

    if (File.Exists(defsPath))
    {
        logger.LogDebug("Loading definitions from {Path}", defsPath);

        var catalog = defsPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? DefinitionCatalog.LoadFromJson(defsPath)
            : DefinitionCatalog.FromBoltPids(BoltPidLoader.LoadFromCsv(defsPath));

        var allResolved = catalog.ToResolvedPids();

        // Filter by priority
        var filter = appConfig.PidFilter;
        if (string.IsNullOrEmpty(filter)) filter = "*!";

        List<ResolvedPid> toQuery;
        if (filter == "all")
        {
            toQuery = allResolved.Where(r => !r.Pid.IsComputed).ToList();
        }
        else
        {
            toQuery = allResolved
                .Where(r => !r.Pid.IsComputed && filter.Contains(r.Pid.Priority))
                .ToList();
        }

        logger.LogDebug("Querying {Count} signals (filter: {Filter})", toQuery.Count, filter);

        var query = new BoltPidQuery(obd, logger);
        var results = await query.QueryResolvedAsync(toQuery, CancellationToken.None);

        // Display grouped by header
        foreach (var headerGroup in results.GroupBy(r => r.Pid.Header))
        {
            Console.WriteLine($"--- ECU {headerGroup.Key} ---");
            foreach (var r in headerGroup)
            {
                if (r.Value.HasValue)
                    Console.WriteLine($"  {r.Pid.ShortName,-20} {r.FormattedValue}");
                else
                    Console.WriteLine($"  {r.Pid.ShortName,-20} NO DATA");
            }
            Console.WriteLine();
        }

        // Write timestamped snapshot
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DataSnapshot
        {
            Timestamp = now,
            Vehicle = catalog.Vehicle,
            Command = "query",
            DefinitionSource = Path.GetFileName(defsPath),
            Filter = filter,
            Readings = results.Select(r => new SignalReading
            {
                SignalId = r.SignalId,
                BindingId = r.BindingId,
                PresentationId = r.PresentationId,
                Ecu = r.Pid.Header,
                Did = r.Pid.IsUds ? r.Pid.ModeAndPid[2..] : null,
                Name = r.Pid.ShortName,
                Value = r.Value,
                Units = r.Pid.Units,
                FormattedValue = r.Value.HasValue ? r.FormattedValue : null,
                RawResponse = r.RawResponse
            }).ToList()
        };

        var snapshotPath = DataSnapshot.GenerateFileName(now);
        snapshot.Save(snapshotPath);
        logger.LogInformation("Snapshot saved to {Path}", snapshotPath);
    }
    else
    {
        logger.LogDebug("No definitions found, querying standard OBD2 PIDs");

        var results = await obd.DumpAllPidsAsync();
        foreach (var (pid, value) in results)
        {
            Console.WriteLine($"{pid}: {value}");
        }
    }

    logger.LogDebug("Disconnecting");
    await obd.DisconnectAsync();

    if (boundRfcomm && OperatingSystem.IsLinux() && !string.IsNullOrEmpty(port))
    {
        Console.WriteLine("Releasing RFCOMM binding...");
        await RunRfcommReleaseAsync(port);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

return 0;

string? GetArgumentValue(string[] a, string key)
{
    for (int i = 0; i < a.Length - 1; i++)
    {
        if (a[i] == key)
            return a[i + 1];
    }
    return null;
}

string ResolveDefinitionPath(string? configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        return configuredPath;

    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        var configuredFromBase = Path.Combine(AppContext.BaseDirectory, configuredPath);
        if (File.Exists(configuredFromBase))
            return configuredFromBase;
    }

    var candidates = new[]
    {
        "Chevrolet_Bolt_PIDs.json",
        Path.Combine(AppContext.BaseDirectory, "Chevrolet_Bolt_PIDs.json"),
        "Chevrolet_Bolt_PIDs.csv",
        Path.Combine(AppContext.BaseDirectory, "Chevrolet_Bolt_PIDs.csv")
    };

    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
            return candidate;
    }

    return !string.IsNullOrWhiteSpace(configuredPath)
        ? configuredPath
        : "Chevrolet_Bolt_PIDs.csv";
}

bool IsLikelyObd(string name)
{
    var lower = name.ToLowerInvariant();
    return lower.Contains("obd") || lower.Contains("elm") || lower.Contains("v-link")
        || lower.Contains("veepeak") || lower.Contains("vgate") || lower.Contains("konnwei");
}

async Task<bool> RunRfcommBindAsync(string mac, string device)
{
    try
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rfcomm",
                Arguments = $"bind {device} {mac}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

async Task RunRfcommReleaseAsync(string device)
{
    try
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rfcomm",
                Arguments = $"release {device}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.Start();
        await process.WaitForExitAsync();
    }
    catch { }
}

public class AppConfig
{
    public string SerialPort { get; set; } = string.Empty;
    public string BluetoothMac { get; set; } = string.Empty;
    public string BleMac { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 38400;
    public int ReadTimeout { get; set; } = 5000;
    public int WriteTimeout { get; set; } = 5000;
    public int ConnectionTimeout { get; set; } = 10000;
    public string PidCsv { get; set; } = string.Empty;
    public string PidFilter { get; set; } = string.Empty;
}

public class ObdDevice : IAsyncDisposable
{
    private readonly IObdTransport _transport;
    private readonly ILogger _logger;

    public ObdDevice(IObdTransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _transport.OpenAsync(ct);
        _transport.DiscardBuffers();

        // Reset adapter
        var atzResp = await SendCommandAsync("ATZ", ct);
        _logger.LogTrace("ATZ: {Response}", atzResp);
        await Task.Delay(1000, ct);

        // Disable echo, linefeeds, spaces — reduces BLE traffic significantly.
        // ATS0 alone cuts ~33% of bytes for large responses.
        var echo = await SendCommandAsync("ATE0", ct);
        _logger.LogTrace("ATE0: {Response}", echo);
        if (!echo.Contains("OK"))
            _logger.LogWarning("AT echo disable failed, continuing...");

        _logger.LogTrace("ATL0: {Response}", await SendCommandAsync("ATL0", ct));
        _logger.LogTrace("ATS0: {Response}", await SendCommandAsync("ATS0", ct));

        // TODO: Make protocol configurable (e.g., via --protocol or appsettings).
        // ATSP6 = ISO 15765-4 CAN 500kbps / 11-bit — correct for Chevy Bolt and most
        // modern GM vehicles. Hardcoding skips the slow auto-detect search (ATSP0 + 0100)
        // which can take 10-15+ seconds over BLE and is unreliable with tight timeouts.
        // Other protocols: 7 = CAN 500k/29-bit, 8 = CAN 250k/11-bit.
        _logger.LogTrace("ATSP6: {Response}", await SendCommandAsync("ATSP6", ct));

        // ATSTFF: Max timeout (255 * 5ms = 1275ms). Cheap clones can timeout
        // mid-response with the default 75ms when multi-frame ISO-TP takes longer.
        _logger.LogTrace("ATSTFF: {Response}", await SendCommandAsync("ATSTFF", ct));

        // NOTE: ATAL (Allow Long) was tried but causes this clone to output raw
        // ISO-TP frames with sequence numbers instead of reassembled data, making
        // frame drops more likely. Omitting it lets the adapter reassemble internally.

        // Enable ISO-TP flow control. ATCFC1 lets the adapter manage FC frames.
        _logger.LogTrace("ATCFC1: {Response}", await SendCommandAsync("ATCFC1", ct));

        _logger.LogInformation("ELM327 initialized ({Adapter})", atzResp.Trim());
    }

    public async Task ConnectAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await ConnectAsync(cts.Token);
    }

    public async Task DisconnectAsync()
    {
        if (_transport.IsOpen)
        {
            try
            {
                await SendCommandAsync("ATPC", CancellationToken.None);
            }
            catch { }

            await _transport.CloseAsync();
        }
    }

    public Task<string> SendCommandAsync(string command, CancellationToken ct)
        => SendCommandAsync(command, 0, 5000, ct);

    public async Task<string> SendCommandAsync(string command, int writeDelayMs, int readTimeoutMs, CancellationToken ct)
    {
        _transport.DiscardBuffers();
        await _transport.WriteAsync(command, ct);
        _logger.LogTrace("Sent: {Command}", command);

        if (writeDelayMs > 0)
            await Task.Delay(writeDelayMs, ct);

        var responses = new List<string>();
        var buffer = new StringBuilder();
        bool receivedAnyData = false;

        using var timeout = new CancellationTokenSource(readTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            var ch = await _transport.ReadCharAsync(linked.Token);
            if (ch == null)
                break;

            if (ch == '>')
            {
                if (!receivedAnyData)
                {
                    // Stale '>' from previous command arrived late over BLE.
                    // Skip it and keep reading for the real response.
                    _logger.LogTrace("Skipped stale '>' for {Command}", command);
                    continue;
                }

                var remaining = buffer.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining) && !remaining.StartsWith(command))
                    responses.Add(remaining);

                // Cheap ELM327 clones can emit a premature '>' mid-response
                // when their internal buffer overflows. Peek briefly for more data.
                using var peekTimeout = new CancellationTokenSource(250);
                using var peekLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, peekTimeout.Token);
                var next = await _transport.ReadCharAsync(peekLinked.Token);
                if (next == null || next == '>')
                    break; // Real end-of-response

                // More data after '>': premature prompt, keep reading.
                _logger.LogTrace("Premature '>' detected for {Command}, continuing read", command);
                buffer.Append(next);
                receivedAnyData = true;
                continue;
            }

            if (ch == '\r' || ch == '\n')
            {
                var line = buffer.ToString().Trim();
                buffer.Clear();

                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith(command)) continue;

                responses.Add(line);
                receivedAnyData = true;
            }
            else
            {
                buffer.Append(ch);
                receivedAnyData = true;
            }
        }

        var result = string.Join("\n", responses);
        _logger.LogTrace("Response for {Command}: {Length} chars, {Lines} lines",
            command, result.Length, responses.Count);
        return result;
    }

    private async Task<string?> QueryPidAsync(string pid, CancellationToken ct)
    {
        try
        {
            var response = await SendCommandAsync(pid, ct);

            if (response.Contains("NO DATA"))
            {
                return null;
            }

            if (response.Contains("UNABLE TO CONNECT") || response.Contains("ERROR"))
            {
                return null;
            }

            var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var dataStart = Array.IndexOf(parts, "41");
            if (dataStart >= 0 && dataStart + 1 < parts.Length)
            {
                return string.Join(" ", parts.Skip(dataStart));
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error querying PID {Pid}", pid);
            return null;
        }
    }

    public async Task<Dictionary<string, string?>> DumpAllPidsAsync()
    {
        var results = new Dictionary<string, string?>();

        var pids = new (string Code, string Name)[]
        {
            ("010C", "Engine RPM"),
            ("010D", "Vehicle Speed"),
            ("0105", "Coolant Temp"),
            ("010B", "Intake Manifold Pressure"),
            ("010F", "Inlet Air Temp"),
            ("0111", "Throttle Position"),
            ("0110", "MAF Air Flow Rate"),
            ("012F", "Fuel Tank Level"),
            ("0132", "Barometric Pressure"),
            ("0100", "PIDs Supported 00"),
            ("0120", "PIDs Supported 01"),
            ("0140", "PIDs Supported 02"),
            ("0103", "Fuel System Status"),
            ("0104", "Engine Load"),
            ("010A", "Fuel Pressure"),
            ("010E", "Timing Advance"),
            ("0112", "O2 Sensor Voltage"),
            ("0113", "Run Time"),
            ("0114", "Distance with MIL"),
            ("0115", "Fuel Rail Pressure"),
            ("0116", "Fuel Rail Gauge Pressure"),
            ("0117", "Commanded EGR"),
            ("0118", "Fuel Trim"),
            ("0119", "O2 Sensors Present"),
            ("011A", "Aux Input Status"),
            ("011B", "Runtime Since Start"),
            ("011C", "Distance Since Clear"),
            ("011D", "Fuel Rail Pressure"),
            ("011E", "Fuel Rail Gauge Pressure"),
            ("011F", "Engine Run Time"),
        };

        foreach (var (code, name) in pids)
        {
            var value = await QueryPidAsync(code, CancellationToken.None);
            results[$"{code} ({name})"] = value;
            await Task.Delay(50);
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
    }
}
