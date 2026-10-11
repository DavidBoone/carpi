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
    private readonly IObdSession _obd;
    private readonly ILogger _logger;

    public DidScanner(IObdSession obd, ILogger logger)
    {
        _obd = obd;
        _logger = logger;
    }

    public Task ScanAsync(string header, int startDid, int endDid, string outputFile, CancellationToken ct)
        => ScanAsync(header, startDid, endDid, outputFile, null, ct);

    public async Task ScanAsync(string header, int startDid, int endDid, string outputFile,
        Action<string, string, string, int>? onPositiveResponse, CancellationToken ct)
    {
        _logger.LogInformation("Scanning ECU {Header}: DIDs {Start:X4}-{End:X4} ({Count} DIDs)",
            header, startDid, endDid, endDid - startDid + 1);

        // Set ECU header
        var headerResp = await _obd.SendCommandAsync($"ATSH {header}", ct);
        if (!headerResp.Contains("OK"))
        {
            _logger.LogError("Failed to set header {Header}: {Response}", header, headerResp);
            return;
        }

        // Verify ECU is reachable before scanning
        if (!await ProbeEcuAsync(header, ct))
        {
            _logger.LogError("ECU {Header} is not reachable (TesterPresent got no response). Is the vehicle on?", header);
            return;
        }
        _logger.LogInformation("ECU {Header} is online", header);

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
        int totalDids = endDid - startDid + 1;

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
                    _logger.LogInformation("Scanned {Scanned}/{Total}, found {Found}...", scanned, totalDids, foundCount);
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

            onPositiveResponse?.Invoke(header, didHex, hexBytes, dataByteCount);
            _logger.LogInformation("FOUND: {Command} -> {Data} ({ByteCount} bytes)", cmd, hexBytes, dataByteCount);
        }

        // Restore default ELM327 timeout
        await _obd.SendCommandAsync("ATST FF", ct);

        // Verify ECU is still reachable after scanning
        if (!await ProbeEcuAsync(header, ct))
            _logger.LogWarning("ECU {Header} is no longer reachable — vehicle may have turned off during scan. Results may be incomplete.", header);

        _logger.LogInformation("Scan complete: {Found} DIDs responded out of {Scanned} scanned", foundCount, scanned);

        if (!string.IsNullOrEmpty(outputFile))
        {
            await SaveAsync(outputFile, scanResult);
            _logger.LogInformation("Results saved to {OutputFile}", outputFile);
        }
    }

    /// <summary>
    /// Sends UDS TesterPresent (3E00) to check if the ECU is alive.
    /// Any response (positive 7E or negative 7F) means online. NO DATA means offline.
    /// </summary>
    public async Task<bool> ProbeEcuAsync(string header, CancellationToken ct)
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
            return JsonSerializer.Deserialize(json, DidScanResultContext.Default.DidScanResult) ?? new DidScanResult();
        }
        catch
        {
            return new DidScanResult();
        }
    }

    private static async Task SaveAsync(string path, DidScanResult result)
    {
        var json = JsonSerializer.Serialize(result, DidScanResultContext.Default.DidScanResult);
        await File.WriteAllTextAsync(path, json);
    }

    // ECUs confirmed responsive on the Bolt via prior DID scanning
    public static readonly (string Header, string Name)[] KnownEcus =
    [
        ("7E0", "ECM (Engine/Powertrain)"),
        ("7E1", "HPCM2 (Hybrid Powertrain)"),
        ("7E3", "TCM (Transmission)"),
        ("7E4", "BECM (Battery Energy)"),
        ("7E6", "EBCM (Brake)"),
        ("7E7", "BECM2 (Battery Extended)"),
    ];

    // Broader set of GM CAN addresses to probe for ECU discovery.
    // Covers the standard OBD2 diagnostic range (7E0-7E7) plus common GM
    // body/chassis/comfort module addresses seen across the platform.
    public static readonly (string Header, string Name)[] ProbeAddresses =
    [
        // Standard OBD2 diagnostic range
        ("7E0", "ECM (Engine/Powertrain)"),
        ("7E1", "HPCM2 (Hybrid Powertrain)"),
        ("7E2", "Powertrain (aux)"),
        ("7E3", "TCM (Transmission)"),
        ("7E4", "BECM (Battery Energy)"),
        ("7E5", "Battery/Body (aux)"),
        ("7E6", "EBCM (Brake)"),
        ("7E7", "BECM2 (Battery Extended)"),
        // GM body/chassis/comfort addresses
        ("720", "Chassis (aux)"),
        ("740", "IPC (Instrument Cluster)"),
        ("741", "IPC (alt)"),
        ("744", "HVAC"),
        ("750", "SDM (Airbag/Sensing)"),
        ("760", "RCDLR (Radio/Infotainment)"),
        ("7A0", "BCM (Body Control)"),
        ("7A1", "BCM (alt)"),
        ("7C0", "BCM2 (Body Control 2)"),
        ("7C4", "TPMS"),
        ("7C8", "Park Assist"),
        ("7CC", "Telematics/OnStar"),
        ("7D0", "Charging Module"),
        ("7D4", "PEPS (Keyless Entry)"),
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

            if (trimmed.Contains(' '))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.Length == 2 && IsHex(part))
                        allBytes.Add(part);
                }
            }
            else
            {
                // Compact format (ATS0): "622C280E3D" or with ISO-TP
                // sequence prefix: "0:622C280E3D", "A:0E3F0E3D"
                var hex = trimmed;
                var colonIdx = hex.IndexOf(':');
                if (colonIdx >= 0 && colonIdx <= 2)
                    hex = hex[(colonIdx + 1)..];
                else if (hex.Length <= 3 && IsHex(hex))
                    continue; // ISO-TP First Frame PCI

                for (int i = 0; i + 1 < hex.Length; i += 2)
                {
                    var pair = hex.Substring(i, 2);
                    if (IsHex(pair))
                        allBytes.Add(pair);
                    else
                        break;
                }
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
