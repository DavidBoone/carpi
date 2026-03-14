using Microsoft.Extensions.Logging;

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

        var results = new List<string>();
        int foundCount = 0;
        int scanned = 0;

        for (int did = startDid; did <= endDid; did++)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            var cmd = $"22{did:X4}";
            var response = await _obd.SendCommandAsync(cmd, ct);

            // Filter out non-data responses:
            // - Empty or standard ELM327 errors
            // - UDS negative response: 7F XX YY (service rejected)
            //   Common NRC codes: 11=serviceNotSupported, 12=subFunctionNotSupported,
            //   13=incorrectMessageLength, 14=responseTooLong, 22=conditionsNotCorrect,
            //   31=requestOutOfRange, 33=securityAccessDenied, 72=generalProgrammingFailure
            if (string.IsNullOrEmpty(response) ||
                response.Contains("NO DATA") ||
                response.Contains("ERROR") ||
                response.Contains("UNABLE TO CONNECT") ||
                response.TrimStart().StartsWith("7F"))
            {
                // Progress every 256 DIDs
                if (did % 256 == 0)
                    Console.Write($"\r  Scanned {scanned}/{endDid - startDid + 1}, found {foundCount}...");
                await Task.Delay(20, ct);
                continue;
            }

            foundCount++;
            var hexBytes = ExtractHexBytes(response);
            var line = $"{header},{cmd},{hexBytes},{response.Replace('\n', ' ')}";
            results.Add(line);
            Console.WriteLine($"  FOUND: {cmd} -> {hexBytes}  (raw: {response.Replace('\n', ' ')})");

            await Task.Delay(30, ct);
        }

        Console.WriteLine();
        Console.WriteLine($"Scan complete: {foundCount} DIDs responded out of {scanned} scanned.");

        if (results.Count > 0 && !string.IsNullOrEmpty(outputFile))
        {
            var header_line = "Header,Command,DataBytes,RawResponse";
            await File.WriteAllLinesAsync(outputFile, results.Prepend(header_line), ct);
            Console.WriteLine($"Results saved to {outputFile}");
        }
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

    private static string ExtractHexBytes(string response)
    {
        var parts = new List<string>();
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("SEARCHING")) continue;
            parts.Add(trimmed);
        }
        return string.Join(" ", parts);
    }
}
