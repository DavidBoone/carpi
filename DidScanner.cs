using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

public class DidScanEntry
{
    [JsonPropertyName("header")]
    public string Header { get; set; } = "";

    [JsonPropertyName("did")]
    public string Did { get; set; } = "";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("dataBytes")]
    public string DataBytes { get; set; } = "";

    [JsonPropertyName("byteCount")]
    public int ByteCount { get; set; }

    [JsonPropertyName("rawResponse")]
    public string RawResponse { get; set; } = "";

    [JsonPropertyName("scannedAt")]
    public DateTime ScannedAt { get; set; }
}

public class DidScanResult
{
    [JsonPropertyName("entries")]
    public List<DidScanEntry> Entries { get; set; } = [];

    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }
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

        // Speed up scanning: reduce ELM327's own response timeout
        // ATST sets timeout in units of 4ms. 0A = 40ms (vs default ~200ms)
        await _obd.SendCommandAsync("ATST 0A", ct);

        // Load existing results to merge with
        var scanResult = await LoadExistingAsync(outputFile);
        var now = DateTime.UtcNow;
        int foundCount = 0;
        int scanned = 0;

        for (int did = startDid; did <= endDid; did++)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            var cmd = $"22{did:X4}";
            // Use fast timing: 50ms write delay, 500ms read timeout
            var response = await _obd.SendCommandAsync(cmd, 50, 500, ct);

            // Filter out non-data responses:
            // - Empty or standard ELM327 errors
            // - UDS negative response: 7F XX YY (service rejected)
            if (string.IsNullOrEmpty(response) ||
                response.Contains("NO DATA") ||
                response.Contains("ERROR") ||
                response.Contains("UNABLE TO CONNECT") ||
                response.TrimStart().StartsWith("7F"))
            {
                if (did % 256 == 0)
                    Console.Write($"\r  Scanned {scanned}/{endDid - startDid + 1}, found {foundCount}...");
                continue;
            }

            foundCount++;
            var hexBytes = ExtractDataBytes(response);
            var dataByteCount = hexBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            var entry = new DidScanEntry
            {
                Header = header,
                Did = $"{did:X4}",
                Command = cmd,
                DataBytes = hexBytes,
                ByteCount = dataByteCount,
                RawResponse = response.Replace('\n', ' '),
                ScannedAt = now
            };

            // Update or add entry (keyed by header + DID)
            var existingIdx = scanResult.Entries.FindIndex(
                e => e.Header == header && e.Did == entry.Did);
            if (existingIdx >= 0)
                scanResult.Entries[existingIdx] = entry;
            else
                scanResult.Entries.Add(entry);

            Console.WriteLine($"  FOUND: {cmd} -> {hexBytes} ({dataByteCount} bytes)");
        }

        // Restore default ELM327 timeout
        await _obd.SendCommandAsync("ATST FF", ct);

        Console.WriteLine();
        Console.WriteLine($"Scan complete: {foundCount} DIDs responded out of {scanned} scanned.");
        Console.WriteLine($"Total known DIDs across all scans: {scanResult.Entries.Count}");

        if (!string.IsNullOrEmpty(outputFile))
        {
            scanResult.LastUpdated = now;
            // Sort entries by header then DID for readability
            scanResult.Entries.Sort((a, b) =>
            {
                var hcmp = string.Compare(a.Header, b.Header, StringComparison.Ordinal);
                return hcmp != 0 ? hcmp : string.Compare(a.Did, b.Did, StringComparison.Ordinal);
            });
            await SaveAsync(outputFile, scanResult);
            Console.WriteLine($"Results saved to {outputFile}");
        }
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
