using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

public class DidResponse
{
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("byteCount")]
    public int ByteCount { get; set; }

    [JsonPropertyName("raw")]
    public string Raw { get; set; } = "";

    [JsonPropertyName("at")]
    public DateTime At { get; set; }
}

public class DidEntry
{
    [JsonPropertyName("lastResponse")]
    public DidResponse LastResponse { get; set; } = new();

    [JsonPropertyName("lastPositiveResponse")]
    public DidResponse? LastPositiveResponse { get; set; }
}

// Root format: { "7E0": { "2068": { ... }, "2069": { ... } }, "7E4": { ... } }
// Using sorted dictionaries so ECUs and DIDs appear in order
public class DidScanResult : SortedDictionary<string, SortedDictionary<string, DidEntry>>
{
}

public class DidScanner
{
    private readonly ObdDevice _obd;
    private readonly ILogger _logger;

    public DidScanner(ObdDevice obd, ILogger logger)
    {
        _obd = obd;
        _logger = logger;
    }

    public async Task ScanAsync(string header, int startDid, int endDid, string outputFile, CancellationToken ct)
    {
        Console.WriteLine($"Scanning ECU {header}: DIDs {startDid:X4}-{endDid:X4} ({endDid - startDid + 1} DIDs)...");
        Console.WriteLine();

        // Set ECU header
        var headerResp = await _obd.SendCommandAsync($"ATSH {header}", ct);
        if (!headerResp.Contains("OK"))
        {
            Console.Error.WriteLine($"Failed to set header {header}: {headerResp}");
            return;
        }

        // Verify ECU is reachable before scanning
        if (!await ProbeEcuAsync(header, ct))
        {
            Console.Error.WriteLine($"ECU {header} is not reachable (TesterPresent got no response). Is the vehicle on?");
            return;
        }
        Console.WriteLine($"ECU {header} is online.");

        // Speed up scanning: reduce ELM327's own response timeout
        // ATST sets timeout in units of 4ms. 0A = 40ms (vs default ~200ms)
        await _obd.SendCommandAsync("ATST 0A", ct);

        // Load existing results to merge with
        var scanResult = await LoadExistingAsync(outputFile);
        if (!scanResult.TryGetValue(header, out var ecuDids))
        {
            ecuDids = new SortedDictionary<string, DidEntry>();
            scanResult[header] = ecuDids;
        }

        var now = DateTime.UtcNow;
        int foundCount = 0;
        int scanned = 0;

        for (int did = startDid; did <= endDid; did++)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            var didHex = $"{did:X4}";
            var cmd = $"22{didHex}";
            // Use fast timing: 50ms write delay, 500ms read timeout
            var response = await _obd.SendCommandAsync(cmd, 50, 500, ct);

            var isNegative = string.IsNullOrEmpty(response) ||
                response.Contains("NO DATA") ||
                response.Contains("ERROR") ||
                response.Contains("UNABLE TO CONNECT") ||
                response.TrimStart().StartsWith("7F");

            if (isNegative)
            {
                // Record the negative response
                var negResponse = new DidResponse
                {
                    Data = null,
                    ByteCount = 0,
                    Raw = response?.Replace('\n', ' ') ?? "",
                    At = now
                };

                if (ecuDids.TryGetValue(didHex, out var existing))
                    existing.LastResponse = negResponse;
                else
                    ecuDids[didHex] = new DidEntry { LastResponse = negResponse };

                if (scanned % 64 == 0)
                    Console.Write($"\r  Scanned {scanned}/{endDid - startDid + 1}, found {foundCount}...");
                continue;
            }

            foundCount++;
            var hexBytes = ExtractDataBytes(response);
            var dataByteCount = hexBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            var posResponse = new DidResponse
            {
                Data = hexBytes,
                ByteCount = dataByteCount,
                Raw = response.Replace('\n', ' '),
                At = now
            };

            if (ecuDids.TryGetValue(didHex, out var entry))
            {
                entry.LastResponse = posResponse;
                entry.LastPositiveResponse = posResponse;
            }
            else
            {
                ecuDids[didHex] = new DidEntry
                {
                    LastResponse = posResponse,
                    LastPositiveResponse = posResponse
                };
            }

            Console.WriteLine($"  FOUND: {cmd} -> {hexBytes} ({dataByteCount} bytes)");
        }

        // Restore default ELM327 timeout
        await _obd.SendCommandAsync("ATST FF", ct);

        // Verify ECU is still reachable after scanning
        if (!await ProbeEcuAsync(header, ct))
            Console.Error.WriteLine($"WARNING: ECU {header} is no longer reachable — vehicle may have turned off during scan. Results may be incomplete.");

        Console.WriteLine();
        Console.WriteLine($"Scan complete: {foundCount} DIDs responded out of {scanned} scanned.");

        if (!string.IsNullOrEmpty(outputFile))
        {
            await SaveAsync(outputFile, scanResult);
            Console.WriteLine($"Results saved to {outputFile}");
        }
    }

    /// <summary>
    /// Sends UDS TesterPresent (3E00) to check if the ECU is alive.
    /// Any response (positive 7E or negative 7F) means online. NO DATA means offline.
    /// </summary>
    private async Task<bool> ProbeEcuAsync(string header, CancellationToken ct)
    {
        await _obd.SendCommandAsync($"ATSH {header}", ct);
        var response = await _obd.SendCommandAsync("3E00", ct);
        return !string.IsNullOrEmpty(response)
            && !response.Contains("NO DATA")
            && !response.Contains("UNABLE TO CONNECT")
            && !response.Contains("ERROR");
    }

    private static async Task<DidScanResult> LoadExistingAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new DidScanResult();

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<DidScanResult>(json) ?? new DidScanResult();
        }
        catch
        {
            return new DidScanResult();
        }
    }

    private static async Task SaveAsync(string path, DidScanResult result)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(result, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static readonly (string Header, string Name)[] KnownEcus =
    [
        ("7E0", "ECM (Engine/Powertrain)"),
        ("7E1", "HPCM2 (Hybrid Powertrain)"),
        ("7E3", "TCM (Transmission)"),
        ("7E4", "BECM (Battery Energy)"),
        ("7E6", "EBCM (Brake)"),
        ("7E7", "BECM2 (Battery Extended)"),
    ];

    // Common GM DID ranges worth scanning
    public static readonly (int Start, int End, string Description)[] CommonRanges =
    [
        (0x0000, 0x00FF, "Standard OBD"),
        (0x0100, 0x01FF, "Extended OBD"),
        (0x1000, 0x1FFF, "GM Local IDs"),
        (0x2000, 0x2FFF, "GM Data IDs"),
        (0x4000, 0x4FFF, "GM Extended Data"),
        (0xC000, 0xC0FF, "GM Calibration"),
        (0xF100, 0xF1FF, "UDS Identification"),
        (0xF190, 0xF19F, "UDS VIN/Serial Area"),
    ];

    private static string ExtractDataBytes(string response)
    {
        var allBytes = new List<string>();
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("SEARCHING")) continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Length == 2 && IsHex(part))
                    allBytes.Add(part);
            }
        }

        // Strip the UDS response prefix: 62 XX XX (3 bytes)
        if (allBytes.Count >= 3 && allBytes[0] == "62")
            return string.Join(" ", allBytes.Skip(3));

        return string.Join(" ", allBytes);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
