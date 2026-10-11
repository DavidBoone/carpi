/// <summary>
/// Wire contract between CarPi.Daemon and CarPi.Client. Lives in CarPi.Obd because both
/// ends already reference it for <see cref="IObdSession"/> — a separate contracts project
/// would buy nothing here.
///
/// The session id is carried in the <c>X-CarPi-Session</c> header on every request after
/// connect. The daemon serves exactly one session at a time; a second connect attempt is
/// refused with 409 rather than queued.
/// </summary>
public static class ObdProtocol
{
    public const string SessionHeader = "X-CarPi-Session";
}

public class ConnectRequest
{
    /// <summary>"serial" or "ble".</summary>
    public string Transport { get; set; } = "ble";

    /// <summary>Serial port path, e.g. /dev/ttyUSB0. Serial transport only.</summary>
    public string? Port { get; set; }

    /// <summary>Serial baud rate. Serial transport only.</summary>
    public int Baud { get; set; } = 38400;

    /// <summary>BLE adapter MAC. BLE transport only.</summary>
    public string? Address { get; set; }

    /// <summary>Ceiling on the open + ELM327 init handshake.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 30;
}

public class ConnectResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>Target the daemon actually opened — port path or BLE MAC.</summary>
    public string Target { get; set; } = "";
}

public class CommandRequest
{
    public string Command { get; set; } = "";
    public int WriteDelayMs { get; set; }
    public int ReadTimeoutMs { get; set; } = 5000;
}

public class CommandResponse
{
    /// <summary>The adapter's reply, verbatim — the daemon interprets nothing.</summary>
    public string Response { get; set; } = "";

    /// <summary>Daemon-side round trip for the command, useful for spotting a sick link.</summary>
    public double ElapsedMs { get; set; }
}

public class StatusResponse
{
    public bool Connected { get; set; }
    public string? SessionId { get; set; }
    public string? Transport { get; set; }
    public string? Target { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
    public int IdleTimeoutSeconds { get; set; }
}

/// <summary>
/// One device the daemon's radio saw. Discovery has to happen on the Pi — the caller has
/// no radio in the car — so it's part of the wire contract even though no session exists
/// yet at that point.
/// </summary>
public class ScanDevice
{
    /// <summary>BLE MAC on Linux. Feed straight back as <see cref="ConnectRequest.Address"/>.</summary>
    public string Address { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Name matched a known adapter vendor. A hint for humans, not a guarantee.</summary>
    public bool IsLikelyObd { get; set; }
}

public class ScanResponse
{
    public List<ScanDevice> Devices { get; set; } = new();

    /// <summary>How long the radio actually listened.</summary>
    public double ElapsedSeconds { get; set; }
}

public class PortsResponse
{
    /// <summary>Serial port paths on the daemon's box, e.g. /dev/ttyUSB0.</summary>
    public List<string> Ports { get; set; } = new();
}

public class ErrorResponse
{
    public string Error { get; set; } = "";
    public string? Detail { get; set; }
}
