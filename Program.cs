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
        { "--pid-filter", "PidFilter" }
    });

var configuration = configBuilder.Build();

var appConfig = new AppConfig();
configuration.Bind(appConfig);

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
    Console.WriteLine("  --csv <path>        PID definitions CSV (default: Chevrolet_Bolt_PIDs.csv)");
    Console.WriteLine("  --pid-filter <f>    PID priority filter (default: *!)");
    Console.WriteLine("                      * = common, ! = important, + = cell voltages");
    Console.WriteLine("                      - = advanced, ? = unknown, all = everything");
    Console.WriteLine("  --scan-dids <header> <start> <end>  Scan DID range on an ECU");
    Console.WriteLine("                      e.g., --scan-dids 7E4 0000 FFFF");
    Console.WriteLine("  --scan-dids-all     Scan common DID ranges on all known ECUs");
    Console.WriteLine("  --scan-output <f>   Output file for DID scan results (default: scan_results.json)");
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
        Console.WriteLine($"Connecting to BLE device {appConfig.BleMac}...");
        transport = new BleObdTransport(appConfig.BleMac, logger);
    }
    else
    {
        Console.WriteLine($"Connecting to OBD2 device on {port}...");

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

    await obd.ConnectAsync(TimeSpan.FromMilliseconds(appConfig.ConnectionTimeout));
    Console.WriteLine("Connected successfully!");
    Console.WriteLine();

    // DID scanning mode
    if (args.Contains("--scan-dids-all") || args.Contains("--scan-dids"))
    {
        var scanOutput = GetArgumentValue(args, "--scan-output") ?? "scan_results.json";
        var scanner = new DidScanner(obd, logger);

        if (args.Contains("--scan-dids-all"))
        {
            Console.WriteLine("Scanning common DID ranges on all known ECUs...");
            Console.WriteLine("(This will take a while)");
            Console.WriteLine();

            var allResults = new List<string> { "Header,Command,DataBytes,RawResponse" };

            foreach (var (ecuHeader, ecuName) in DidScanner.KnownEcus)
            {
                Console.WriteLine($"=== {ecuName} ({ecuHeader}) ===");
                foreach (var (start, end, desc) in DidScanner.CommonRanges)
                {
                    Console.WriteLine($"  Range: {desc} ({start:X4}-{end:X4})");
                    await scanner.ScanAsync(ecuHeader, start, end, scanOutput, CancellationToken.None);
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

            await scanner.ScanAsync(scanHeader, startDid, endDid, scanOutput, CancellationToken.None);
        }

        Console.WriteLine();
        Console.WriteLine("Disconnecting...");
        await obd.DisconnectAsync();
        Console.WriteLine("Done.");
        return 0;
    }

    // Look for PID CSV file
    var csvPath = !string.IsNullOrEmpty(appConfig.PidCsv)
        ? appConfig.PidCsv
        : Path.Combine(AppContext.BaseDirectory, "Chevrolet_Bolt_PIDs.csv");

    // Also check working directory
    if (!File.Exists(csvPath))
        csvPath = "Chevrolet_Bolt_PIDs.csv";

    if (File.Exists(csvPath))
    {
        Console.WriteLine($"Loading PIDs from {csvPath}...");
        var allPids = BoltPidLoader.LoadFromCsv(csvPath);

        // Filter by priority
        var filter = appConfig.PidFilter;
        if (string.IsNullOrEmpty(filter)) filter = "*!";

        List<BoltPid> pidsToQuery;
        if (filter == "all")
        {
            pidsToQuery = allPids.Where(p => !p.IsComputed).ToList();
        }
        else
        {
            pidsToQuery = allPids
                .Where(p => !p.IsComputed && filter.Contains(p.Priority))
                .ToList();
        }

        Console.WriteLine($"Querying {pidsToQuery.Count} PIDs (filter: {filter})...");
        Console.WriteLine();

        var query = new BoltPidQuery(obd, logger);
        var results = await query.QueryPidsAsync(pidsToQuery, CancellationToken.None);

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
    }
    else
    {
        Console.WriteLine("Querying standard OBD2 PIDs...");
        Console.WriteLine();

        var results = await obd.DumpAllPidsAsync();
        foreach (var (pid, value) in results)
        {
            Console.WriteLine($"{pid}: {value}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Disconnecting...");
    await obd.DisconnectAsync();
    Console.WriteLine("Done.");

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

        Console.WriteLine("Sending ATZ...");
        var atzResp = await SendCommandAsync("ATZ", ct);
        Console.WriteLine($"ATZ response: '{atzResp}'");
        Console.WriteLine("ATZ done, waiting...");
        await Task.Delay(1000, ct);

        Console.WriteLine("Sending ATE0...");
        var echo = await SendCommandAsync("ATE0", ct);
        Console.WriteLine($"ATE0 response: {echo}");
        if (!echo.Contains("OK"))
        {
            _logger.LogWarning("AT echo disable failed, continuing...");
        }

        Console.WriteLine("Sending ATL0...");
        var headers = await SendCommandAsync("ATL0", ct);
        Console.WriteLine($"ATL0 response: {headers}");

        Console.WriteLine("Sending ATSP0...");
        var spaces = await SendCommandAsync("ATSP0", ct);
        Console.WriteLine($"ATSP0 response: {spaces}");

        Console.WriteLine("Sending 0100...");
        var protocol = await SendCommandAsync("0100", ct);
        Console.WriteLine($"0100 response: {protocol}");
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
        => SendCommandAsync(command, 200, 5000, ct);

    public async Task<string> SendCommandAsync(string command, int writeDelayMs, int readTimeoutMs, CancellationToken ct)
    {
        _transport.DiscardBuffers();
        await _transport.WriteAsync(command, ct);

        if (writeDelayMs > 0)
            await Task.Delay(writeDelayMs, ct);

        var responses = new List<string>();
        var buffer = new StringBuilder();

        using var timeout = new CancellationTokenSource(readTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            var ch = await _transport.ReadCharAsync(linked.Token);
            if (ch == null)
                break;

            if (ch == '>')
            {
                var remaining = buffer.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining) && !remaining.StartsWith(command))
                    responses.Add(remaining);
                break;
            }

            if (ch == '\r' || ch == '\n')
            {
                var line = buffer.ToString().Trim();
                buffer.Clear();

                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith(command)) continue;

                responses.Add(line);
            }
            else
            {
                buffer.Append(ch);
            }
        }

        return string.Join("\n", responses);
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
