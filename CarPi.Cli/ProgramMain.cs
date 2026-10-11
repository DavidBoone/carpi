using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO.Ports;
#if !MACCATALYST
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
#endif
using Microsoft.Extensions.Logging;

public static class ProgramMain
{
    // Global options — declared as fields so all commands can access them
    private static readonly Option<string?> PortOption = new("--port") { Description = "Serial port (e.g., /dev/ttyUSB0, COM3)" , Recursive = true };
    private static readonly Option<int> BaudOption = new("--baud") { Description = "Baud rate", DefaultValueFactory = _ => 38400 , Recursive = true };
    private static readonly Option<string?> BleOption = new("--ble") { Description = "BLE device MAC address (or CoreBluetooth UUID on macOS)" , Recursive = true };
    private static readonly Option<string?> BluetoothMacOption = new("--bluetooth-mac") { Description = "Bluetooth MAC for RFCOMM auto-binding on Linux" , Recursive = true };
    private static readonly Option<string?> DefsOption = new("--defs", "--csv") { Description = "PID definitions file (.csv or .json)" , Recursive = true };
    private static readonly Option<string?> PidFilterOption = new("--pid-filter") { Description = "PID priority filter (* = common, ! = important, + = cell voltages, all = everything)", DefaultValueFactory = _ => "*!" , Recursive = true };
    private static readonly Option<int> VerbosityOption = new("-v") { Description = "Verbosity level (1=Info, 2=Debug, 3=Trace)", Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => 0 , Recursive = true };

    public static async Task<int> RunAsync(string[] args)
    {
        // Pre-process: count -v/-vv/-vvv flags and normalize to -v <N>
        var (processedArgs, verbosity) = PreProcessVerbosity(args);

        var root = BuildRootCommand();
        var parseResult = root.Parse(processedArgs);
        var result = await parseResult.InvokeAsync();

        // Connection failures (car asleep, daemon unreachable) set Environment.ExitCode
        // from inside an action, where there's no return value to use. Surface it, so a
        // scheduled pull can tell "no car" from "collected nothing".
        return result != 0 ? result : Environment.ExitCode;
    }

    private static (string[] Args, int Verbosity) PreProcessVerbosity(string[] args)
    {
        int verbosity = 0;
        var remaining = new List<string>();

        foreach (var arg in args)
        {
            if (arg == "-vvv") verbosity = 3;
            else if (arg == "-vv") verbosity = Math.Max(verbosity, 2);
            else if (arg == "-v") verbosity = Math.Max(verbosity, 1);
            else remaining.Add(arg);
        }

        if (verbosity > 0)
        {
            remaining.Add("-v");
            remaining.Add(verbosity.ToString());
        }

        return (remaining.ToArray(), verbosity);
    }

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("CarPi OBD2 CLI Tool");

        root.Options.Add(PortOption);
        root.Options.Add(BaudOption);
        root.Options.Add(BleOption);
        root.Options.Add(BluetoothMacOption);
        root.Options.Add(DefsOption);
        root.Options.Add(PidFilterOption);
        root.Options.Add(VerbosityOption);

        root.Subcommands.Add(BuildListPortsCommand());
        root.Subcommands.Add(BuildScanBluetoothCommand());
        root.Subcommands.Add(BuildScanBleCommand());
        root.Subcommands.Add(BuildImportCsvCommand());
        root.Subcommands.Add(BuildNormalizeDefsCommand());
        root.Subcommands.Add(BuildValidateCommand());
        root.Subcommands.Add(BuildReportCommand());
        root.Subcommands.Add(BuildProbeEcusCommand());
        root.Subcommands.Add(BuildScanDidsCommand());
        root.Subcommands.Add(BuildScanDidsAllCommand());
        root.Subcommands.Add(BuildQueryCommand());

        // Default: if connection specified, run query; otherwise show help
        root.SetAction(async (parseResult, ct) =>
        {
            var port = parseResult.GetValue(PortOption);
            var ble = parseResult.GetValue(BleOption);

            if (string.IsNullOrEmpty(port) && string.IsNullOrEmpty(ble))
                return; // System.CommandLine shows help by default when no action matches

            using var lf = CreateLoggerFactory(parseResult.GetValue(VerbosityOption));
            var logger = lf.CreateLogger<Program>();

            await using var obd = await ConnectObdAsync(parseResult, logger, ct);
            if (obd == null) { Environment.ExitCode = 1; return; }

            await RunQuery(obd, parseResult.GetValue(DefsOption), parseResult.GetValue(PidFilterOption) ?? "*!", logger, ct);
            await obd.DisconnectAsync();
        });

        return root;
    }

    // --- Subcommands ---

    private static Command BuildListPortsCommand()
    {
        var cmd = new Command("list-ports", "List available serial ports");
        cmd.SetAction(_ =>
        {
            Console.WriteLine("Available serial ports:");
            var ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
                Console.WriteLine("  (none found)");
            else
                foreach (var p in ports)
                    Console.WriteLine($"  {p}");
        });
        return cmd;
    }

    private static Command BuildScanBluetoothCommand()
    {
        var cmd = new Command("scan-bluetooth", "Scan for paired Bluetooth OBD2 adapters");
        cmd.SetAction(_ => ScanBluetooth());
        return cmd;
    }

    private static Command BuildScanBleCommand()
    {
        var cmd = new Command("scan-ble", "Scan for BLE OBD2 adapters");
        cmd.SetAction(async (parseResult, ct) =>
        {
            using var lf = CreateLoggerFactory(parseResult.GetValue(VerbosityOption));
            var logger = lf.CreateLogger<Program>();
            await ScanBle(logger);
        });
        return cmd;
    }

    private static Command BuildImportCsvCommand()
    {
        var cmd = new Command("import-csv", "Import legacy PID CSV and write canonical JSON");
        var inputArg = new Argument<string>("input") { Description = "Input CSV file path" };
        var outOption = new Option<string?>("--out") { Description = "Output JSON path (default: Chevrolet_Bolt_PIDs.json)" };
        var vehicleOption = new Option<string?>("--vehicle") { Description = "Vehicle name metadata" };
        cmd.Arguments.Add(inputArg);
        cmd.Options.Add(outOption);
        cmd.Options.Add(vehicleOption);

        cmd.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg);
            var output = parseResult.GetValue(outOption) ?? "Chevrolet_Bolt_PIDs.json";
            var vehicle = parseResult.GetValue(vehicleOption) ?? "";

            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"CSV file not found: {input}");
                return;
            }

            var pids = BoltPidLoader.LoadFromCsv(input!);
            var catalog = DefinitionCatalog.FromBoltPids(pids, vehicle);
            catalog.SaveToJson(output);

            Console.WriteLine($"Imported {pids.Count} PID rows from {input}");
            Console.WriteLine($"Wrote canonical JSON to {output}");
        });
        return cmd;
    }

    private static Command BuildNormalizeDefsCommand()
    {
        var cmd = new Command("normalize-defs", "Normalize canonical JSON seed data");
        var inputArg = new Argument<string>("input") { Description = "Input JSON file path" };
        var outOption = new Option<string?>("--out") { Description = "Output path (default: overwrite input)" };
        cmd.Arguments.Add(inputArg);
        cmd.Options.Add(outOption);

        cmd.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg);
            var output = parseResult.GetValue(outOption) ?? input;

            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Definitions file not found: {input}");
                return;
            }

            var catalog = DefinitionCatalog.LoadFromJson(input!);
            var normalized = DefinitionCatalogNormalizer.NormalizeSeedCatalog(catalog);
            normalized.SaveToJson(output!);

            Console.WriteLine($"Normalized canonical JSON from {input}");
            Console.WriteLine($"Wrote normalized JSON to {output}");
        });
        return cmd;
    }

    private static Command BuildValidateCommand()
    {
        var cmd = new Command("validate", "Validate canonical JSON definitions");
        var inputArg = new Argument<string>("input") { Description = "Definitions JSON file path" };
        cmd.Arguments.Add(inputArg);

        cmd.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg);

            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Definitions file not found: {input}");
                return;
            }

            if (!input!.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Validation requires a canonical JSON file, got: {input}");
                return;
            }

            var catalog = DefinitionCatalog.LoadFromJson(input);
            var result = DefinitionCatalogValidator.Validate(catalog);

            Console.WriteLine($"Validated {catalog.Signals.Count} signals from {input}");
            Console.WriteLine(result.Summary());

            if (!result.IsValid)
            {
                Console.WriteLine();
                foreach (var error in result.Errors)
                    Console.WriteLine($"  [{error.Area}] {error.SignalId}: {error.Message}");
            }
        });
        return cmd;
    }

    private static Command BuildReportCommand()
    {
        var cmd = new Command("report", "Generate HTML report from scan results");
        var inputArg = new Argument<string>("input") { Description = "Scan results JSON file", DefaultValueFactory = _ => "scan_results.json" };
        cmd.Arguments.Add(inputArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var jsonFile = parseResult.GetValue(inputArg);
            var definitionPath = ResolveDefinitionPath(parseResult.GetValue(DefsOption));

            var report = new DidReportGenerator();
            if (File.Exists(definitionPath))
            {
                report.LoadKnownPids(definitionPath);
                Console.WriteLine($"Loaded known PIDs from {definitionPath}");
            }

            await report.GenerateAsync(jsonFile!, "scan_report.html");
            Console.WriteLine("Report written to scan_report.html");
        });
        return cmd;
    }

    private static Command BuildProbeEcusCommand()
    {
        var cmd = new Command("probe-ecus", "Check which known ECUs are online");
        cmd.SetAction(async (parseResult, ct) =>
        {
            using var lf = CreateLoggerFactory(parseResult.GetValue(VerbosityOption));
            var logger = lf.CreateLogger<Program>();

            await using var obd = await ConnectObdAsync(parseResult, logger, ct);
            if (obd == null) { Environment.ExitCode = 1; return; }

            var scanner = new DidScanner(obd, logger);
            var now = DateTimeOffset.UtcNow;
            var snapshot = new DataSnapshot { Timestamp = now, Command = "probe-ecus" };

            Console.WriteLine("Probing ECU addresses...");
            foreach (var (header, name) in DidScanner.ProbeAddresses)
            {
                var alive = await scanner.ProbeEcuAsync(header, ct);
                var status = alive ? "ONLINE" : "no response";
                Console.WriteLine($"  {header}  {name,-30} {status}");
                snapshot.Ecus.Add(new EcuStatus { Header = header, Name = name, Online = alive });
            }

            var snapshotPath = DataSnapshot.GenerateFileName(now);
            snapshot.Save(snapshotPath);
            logger.LogInformation("Snapshot saved to {Path}", snapshotPath);
            await obd.DisconnectAsync();
        });
        return cmd;
    }

    private static readonly Option<string> ScanOutputOption = new("--scan-output") { Description = "Output file for DID scan results", DefaultValueFactory = _ => "scan_results.json" };

    private static Command BuildScanDidsCommand()
    {
        var cmd = new Command("scan-dids", "Scan DID range on an ECU");
        var headerArg = new Argument<string>("header") { Description = "ECU header (e.g., 7E4)" };
        var startArg = new Argument<string>("start") { Description = "Start DID hex (e.g., 0000)" };
        var endArg = new Argument<string>("end") { Description = "End DID hex (e.g., FFFF)" };
        cmd.Arguments.Add(headerArg);
        cmd.Arguments.Add(startArg);
        cmd.Arguments.Add(endArg);
        cmd.Options.Add(ScanOutputOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            using var lf = CreateLoggerFactory(parseResult.GetValue(VerbosityOption));
            var logger = lf.CreateLogger<Program>();

            await using var obd = await ConnectObdAsync(parseResult, logger, ct);
            if (obd == null) { Environment.ExitCode = 1; return; }

            var scanOutput = parseResult.GetValue(ScanOutputOption);
            var header = parseResult.GetValue(headerArg);
            var startDid = Convert.ToInt32(parseResult.GetValue(startArg), 16);
            var endDid = Convert.ToInt32(parseResult.GetValue(endArg), 16);

            var (knownDids, scanDefsSource) = LoadKnownDids(parseResult.GetValue(DefsOption));
            var now = DateTimeOffset.UtcNow;
            var snapshot = new DataSnapshot
            {
                Timestamp = now,
                Command = "scan-dids",
                DefinitionSource = scanDefsSource
            };

            var scanner = new DidScanner(obd, logger);
            await scanner.ScanAsync(header!, startDid, endDid, scanOutput!,
                CreateDidFoundHandler(knownDids, snapshot), ct);

            var snapshotPath = DataSnapshot.GenerateFileName(now);
            snapshot.Save(snapshotPath);
            logger.LogInformation("Snapshot saved to {Path}", snapshotPath);
            await obd.DisconnectAsync();
        });
        return cmd;
    }

    private static Command BuildScanDidsAllCommand()
    {
        var cmd = new Command("scan-dids-all", "Scan common DID ranges on all known ECUs");
        cmd.Options.Add(ScanOutputOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            using var lf = CreateLoggerFactory(parseResult.GetValue(VerbosityOption));
            var logger = lf.CreateLogger<Program>();

            await using var obd = await ConnectObdAsync(parseResult, logger, ct);
            if (obd == null) { Environment.ExitCode = 1; return; }

            var scanOutput = parseResult.GetValue(ScanOutputOption);
            var (knownDids, scanDefsSource) = LoadKnownDids(parseResult.GetValue(DefsOption));
            var now = DateTimeOffset.UtcNow;
            var snapshot = new DataSnapshot
            {
                Timestamp = now,
                Command = "scan-dids-all",
                DefinitionSource = scanDefsSource
            };

            var scanner = new DidScanner(obd, logger);
            var onDidFound = CreateDidFoundHandler(knownDids, snapshot);

            Console.WriteLine("Scanning common DID ranges on all known ECUs...");
            Console.WriteLine("(This will take a while)");
            Console.WriteLine();

            foreach (var (ecuHeader, ecuName) in DidScanner.KnownEcus)
            {
                Console.WriteLine($"=== {ecuName} ({ecuHeader}) ===");
                foreach (var (start, end, desc) in DidScanner.CommonRanges)
                {
                    Console.WriteLine($"  Range: {desc} ({start:X4}-{end:X4})");
                    await scanner.ScanAsync(ecuHeader, start, end, scanOutput!, onDidFound, ct);
                }
                Console.WriteLine();
            }

            var snapshotPath = DataSnapshot.GenerateFileName(now);
            snapshot.Save(snapshotPath);
            logger.LogInformation("Snapshot saved to {Path}", snapshotPath);
            await obd.DisconnectAsync();
        });
        return cmd;
    }

    private static Command BuildQueryCommand()
    {
        var cmd = new Command("query", "Query PIDs from definitions file");
        cmd.SetAction(async (parseResult, ct) =>
        {
            using var lf = CreateLoggerFactory(parseResult.GetValue(VerbosityOption));
            var logger = lf.CreateLogger<Program>();

            await using var obd = await ConnectObdAsync(parseResult, logger, ct);
            if (obd == null) { Environment.ExitCode = 1; return; }

            await RunQuery(obd, parseResult.GetValue(DefsOption), parseResult.GetValue(PidFilterOption) ?? "*!", logger, ct);
            await obd.DisconnectAsync();
        });
        return cmd;
    }

    // --- Shared logic ---

    private static async Task RunQuery(IObdSession obd, string? defsOptionValue, string filter, ILogger logger, CancellationToken ct)
    {
        var defsPath = ResolveDefinitionPath(defsOptionValue);

        if (File.Exists(defsPath))
        {
            logger.LogDebug("Loading definitions from {Path}", defsPath);

            var catalog = defsPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? DefinitionCatalog.LoadFromJson(defsPath)
                : DefinitionCatalog.FromBoltPids(BoltPidLoader.LoadFromCsv(defsPath));

            var allResolved = catalog.ToResolvedPids();

            if (string.IsNullOrEmpty(filter)) filter = "*!";

            List<ResolvedPid> toQuery;
            if (filter == "all")
                toQuery = allResolved.Where(r => !r.Pid.IsComputed).ToList();
            else
                toQuery = allResolved
                    .Where(r => !r.Pid.IsComputed && filter.Contains(r.Pid.Priority))
                    .ToList();

            logger.LogDebug("Querying {Count} signals (filter: {Filter})", toQuery.Count, filter);

            var query = new BoltPidQuery(obd, logger);
            var results = await query.QueryResolvedAsync(toQuery, ct);

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
            var results = await obd.DumpAllPidsAsync(logger);
            foreach (var (pid, value) in results)
                Console.WriteLine($"{pid}: {value}");
        }
    }

    private static async Task<ObdDevice?> ConnectObdAsync(ParseResult parseResult, ILogger logger, CancellationToken ct)
    {
        var bleMac = parseResult.GetValue(BleOption);
        var port = parseResult.GetValue(PortOption);
        var baud = parseResult.GetValue(BaudOption);
        var btMac = parseResult.GetValue(BluetoothMacOption);

        bool useBle = !string.IsNullOrEmpty(bleMac);

        if (!useBle && string.IsNullOrEmpty(port))
        {
            Console.Error.WriteLine("Error: No connection specified. Use --port <port> or --ble <mac>.");
            return null;
        }

        IObdTransport transport;

        if (useBle)
        {
            logger.LogInformation("Connecting to BLE device {Mac}", bleMac);
#if MACCATALYST
            transport = new MacBleObdTransport(bleMac!, logger);
#else
            transport = new BleObdTransport(bleMac!, logger);
#endif
        }
        else
        {
            logger.LogInformation("Connecting to OBD2 device on {Port}", port);

            if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(btMac) && port!.StartsWith("/dev/rfcomm"))
            {
                Console.WriteLine($"Binding Bluetooth RFCOMM to {btMac}...");
                var bindResult = await RunRfcommBindAsync(btMac, port);
                if (!bindResult)
                {
                    Console.Error.WriteLine("Failed to bind RFCOMM device. Is the car in range?");
                    return null;
                }
                await Task.Delay(500, ct);
            }

            transport = new SerialObdTransport(port!, baud, 5000, 5000);
        }

        var obd = new ObdDevice(transport, logger);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(30));
        await obd.ConnectAsync(connectCts.Token);
        logger.LogInformation("Connected");
        return obd;
    }

    private static (Dictionary<string, List<ResolvedPid>> KnownDids, string? DefsSource) LoadKnownDids(string? defsOptionValue)
    {
        var knownDids = new Dictionary<string, List<ResolvedPid>>(StringComparer.OrdinalIgnoreCase);
        string? defsSource = null;
        var scanDefsPath = ResolveDefinitionPath(defsOptionValue);

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

            defsSource = Path.GetFileName(scanDefsPath);
            Console.WriteLine($"Loaded {knownDids.Count} known DIDs from {scanDefsPath}");
        }

        return (knownDids, defsSource);
    }

    private static Action<string, string, string, int> CreateDidFoundHandler(
        Dictionary<string, List<ResolvedPid>> knownDids, DataSnapshot snapshot)
    {
        return (string ecu, string did, string dataBytes, int byteCount) =>
        {
            var key = $"{ecu}:{did}";
            if (knownDids.TryGetValue(key, out var matches))
            {
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
        };
    }

    private static ILoggerFactory CreateLoggerFactory(int verbosity)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });

            var minLevel = verbosity switch
            {
                >= 3 => LogLevel.Trace,
                2 => LogLevel.Debug,
                1 => LogLevel.Information,
                _ => LogLevel.Warning
            };
            builder.SetMinimumLevel(minLevel);
        });
    }

    private static void ScanBluetooth()
    {
        Console.WriteLine("Scanning for Bluetooth OBD2 adapters...");
        Console.WriteLine();

        if (OperatingSystem.IsMacOS())
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
                    Console.WriteLine($"  {p}");
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
        else if (OperatingSystem.IsLinux())
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
                        Console.WriteLine($"  {p}");
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
                    Console.WriteLine($"  {p}");
            }
        }
        else
        {
            Console.WriteLine("Unknown platform - checking serial ports anyway:");
            foreach (var p in SerialPort.GetPortNames())
                Console.WriteLine($"  {p}");
        }
    }

    private static async Task ScanBle(ILogger logger)
    {
#if MACCATALYST
        Console.WriteLine("Scanning for BLE devices (10 seconds)...");
        Console.WriteLine();

        var devices = await MacBleObdTransport.ScanForDevicesAsync(TimeSpan.FromSeconds(10));

        if (devices.Count == 0)
        {
            Console.WriteLine("  (no devices found)");
        }
        else
        {
            foreach (var (id, name, isObd) in devices)
            {
                var marker = isObd ? " <-- possible OBD2 adapter" : "";
                Console.WriteLine($"  {id}  {name}{marker}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Use --ble <UUID> to connect to a device.");
        Console.WriteLine("(macOS uses CoreBluetooth UUIDs instead of MAC addresses)");
#else
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("BLE scanning requires Linux with BlueZ.");
            return;
        }

        Console.WriteLine("Scanning for BLE devices (10 seconds)...");
        Console.WriteLine();

        var adapters = await BlueZManager.GetAdaptersAsync();
        if (adapters.Count == 0)
        {
            Console.Error.WriteLine("No Bluetooth adapters found.");
            return;
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
                var marker = BleConstants.IsLikelyObd(name) ? " <-- possible OBD2 adapter" : "";
                Console.WriteLine($"  {address}  {name}{marker}");
            }
        };

        await adapter.StartDiscoveryAsync();
        await Task.Delay(10000);
        await adapter.StopDiscoveryAsync();

        if (found.Count == 0)
            Console.WriteLine("  (no devices found)");

        Console.WriteLine();
        Console.WriteLine("Use --ble <MAC> to connect to a device.");
#endif
    }

    private static string ResolveDefinitionPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configuredFromBase = Path.Combine(AppContext.BaseDirectory, configuredPath);
            if (File.Exists(configuredFromBase))
                return configuredFromBase;

#if MACCATALYST
            var configuredFromResources = Path.Combine(AppContext.BaseDirectory, "..", "Resources", configuredPath);
            if (File.Exists(configuredFromResources))
                return configuredFromResources;
#endif
        }

        var searchDirs = new List<string> { "", AppContext.BaseDirectory };

#if MACCATALYST
        var resourceDir = Path.Combine(AppContext.BaseDirectory, "..", "Resources");
        if (Directory.Exists(resourceDir))
            searchDirs.Add(resourceDir);
#endif

        var candidates = searchDirs
            .SelectMany(dir => new[]
            {
                Path.Combine(dir, "Chevrolet_Bolt_PIDs.json"),
                Path.Combine(dir, "Chevrolet_Bolt_PIDs.csv")
            })
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : "Chevrolet_Bolt_PIDs.csv";
    }

    private static async Task<bool> RunRfcommBindAsync(string mac, string device)
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
}
