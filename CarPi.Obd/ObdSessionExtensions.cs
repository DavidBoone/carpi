using Microsoft.Extensions.Logging;

/// <summary>
/// Convenience helpers that build on nothing but <see cref="IObdSession.SendCommandAsync"/>,
/// so they work the same against a local adapter or a remote daemon.
/// </summary>
public static class ObdSessionExtensions
{
    public static async Task<Dictionary<string, string?>> DumpAllPidsAsync(this IObdSession obd, ILogger logger)
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
            var value = await QueryPidAsync(obd, logger, code, CancellationToken.None);
            results[$"{code} ({name})"] = value;
            await Task.Delay(50);
        }

        return results;
    }

    private static async Task<string?> QueryPidAsync(IObdSession obd, ILogger logger, string pid, CancellationToken ct)
    {
        try
        {
            var response = await obd.SendCommandAsync(pid, ct);

            if (response.Contains("NO DATA"))
                return null;

            if (response.Contains("UNABLE TO CONNECT") || response.Contains("ERROR"))
                return null;

            var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var dataStart = Array.IndexOf(parts, "41");
            if (dataStart >= 0 && dataStart + 1 < parts.Length)
                return string.Join(" ", parts.Skip(dataStart));

            return response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error querying PID {Pid}", pid);
            return null;
        }
    }
}
