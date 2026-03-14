using System.IO.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "CARPI_")
    .AddCommandLine(args, new Dictionary<string, string>
    {
        { "--port", "SerialPort" },
        { "--baud", "BaudRate" }
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
    Console.WriteLine("  CarPi --port <port>             Connect to OBD2 device and dump values");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --port <port>     Serial port (e.g., /dev/ttyUSB0, COM3, /dev/rfcomm0)");
    Console.WriteLine("  --baud <rate>     Baud rate (default: 115200)");
    Console.WriteLine("  --list-ports      List available serial ports");
    Console.WriteLine("  --scan-bluetooth  Scan for paired Bluetooth OBD2 devices");
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
                Console.WriteLine("2. Bind: sudo rfcomm bind /dev/rfcomm0 <MAC>");
                Console.WriteLine("3. Then run with --port /dev/rfcomm0");
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

string? port = appConfig.SerialPort;
if (string.IsNullOrEmpty(port))
{
    Console.Error.WriteLine("Error: No port specified. Use --port or set SerialPort in config.");
    return 1;
}

Console.WriteLine($"Connecting to OBD2 device on {port}...");

try
{
    using var obd = new ObdDevice(port, appConfig.BaudRate, appConfig.ReadTimeout, appConfig.WriteTimeout, logger);
    
    await obd.ConnectAsync(TimeSpan.FromMilliseconds(appConfig.ConnectionTimeout));
    Console.WriteLine("Connected successfully!");
    
    Console.WriteLine();
    Console.WriteLine("Querying OBD2 PIDs...");
    Console.WriteLine();
    
    var results = await obd.DumpAllPidsAsync();
    
    foreach (var (pid, value) in results)
    {
        Console.WriteLine($"{pid}: {value}");
    }
    
    Console.WriteLine();
    Console.WriteLine("Disconnecting...");
    await obd.DisconnectAsync();
    Console.WriteLine("Done.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

return 0;

public class AppConfig
{
    public string SerialPort { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115200;
    public int ReadTimeout { get; set; } = 5000;
    public int WriteTimeout { get; set; } = 5000;
    public int ConnectionTimeout { get; set; } = 10000;
}

public class ObdDevice : IDisposable
{
    private readonly SerialPort _serial;
    private readonly ILogger _logger;
    private bool _connected;
    private readonly object _lock = new();

    public ObdDevice(string port, int baudRate, int readTimeout, int writeTimeout, ILogger logger)
    {
        _serial = new SerialPort(port, baudRate)
        {
            ReadTimeout = readTimeout,
            WriteTimeout = writeTimeout,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One
        };
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _serial.Open();
        _serial.DiscardInBuffer();
        _serial.DiscardOutBuffer();

        await SendCommandAsync("ATZ", ct);
        await Task.Delay(1000, ct);
        
        var echo = await SendCommandAsync("ATE0", ct);
        if (!echo.Contains("OK"))
        {
            _logger.LogWarning("AT echo disable failed, continuing...");
        }
        
        var headers = await SendCommandAsync("ATL0", ct);
        
        var spaces = await SendCommandAsync("ATSP0", ct);
        
        var protocol = await SendCommandAsync("0100", ct);
        
        _connected = true;
    }

    public async Task ConnectAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await ConnectAsync(cts.Token);
    }

    public async Task DisconnectAsync()
    {
        if (_serial.IsOpen)
        {
            try
            {
                await SendCommandAsync("ATPC", CancellationToken.None);
            }
            catch { }
            
            try
            {
                await SendCommandAsync("ATCV", CancellationToken.None);
            }
            catch { }
            
            _serial.Close();
        }
        _connected = false;
    }

    private async Task<string> SendCommandAsync(string command, CancellationToken ct)
    {
        lock (_lock)
        {
            _serial.WriteLine(command);
        }
        
        await Task.Delay(200, ct);
        
        var responses = new List<string>();
        var timeout = _serial.ReadTimeout;
        var endTime = DateTime.UtcNow.AddMilliseconds(timeout);
        
        while (DateTime.UtcNow < endTime)
        {
            try
            {
                var line = _serial.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                line = line.Trim();
                
                if (line == ">") break;
                
                if (line.StartsWith(command)) continue;
                
                responses.Add(line);
            }
            catch (TimeoutException)
            {
                break;
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

    public void Dispose()
    {
        if (_serial.IsOpen)
        {
            _serial.Close();
        }
        _serial.Dispose();
    }
}
