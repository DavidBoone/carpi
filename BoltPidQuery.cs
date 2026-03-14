using Microsoft.Extensions.Logging;

public class BoltPidResult
{
    public BoltPid Pid { get; set; } = null!;
    public double? Value { get; set; }
    public string RawResponse { get; set; } = "";
    public string FormattedValue => Value.HasValue
        ? $"{Value.Value:F2} {Pid.Units}".Trim()
        : "NO DATA";
}

public class BoltPidQuery
{
    private readonly ObdDevice _obd;
    private readonly ILogger _logger;
    private string _currentHeader = "";

    public BoltPidQuery(ObdDevice obd, ILogger logger)
    {
        _obd = obd;
        _logger = logger;
    }

    public async Task<List<BoltPidResult>> QueryPidsAsync(List<BoltPid> pids, CancellationToken ct)
    {
        var results = new List<BoltPidResult>();

        // Filter to only queryable PIDs (non-computed, with a ModeAndPid)
        var queryable = pids.Where(p => !p.IsComputed).ToList();

        // Deduplicate: same ModeAndPid+Header only needs one query
        var grouped = queryable
            .GroupBy(p => p.Header)
            .OrderBy(g => g.Key);

        // Store raw responses so duplicate PIDs (e.g., C vs F) reuse the same query
        var responseCache = new Dictionary<string, (string Response, byte[] Data)>();

        foreach (var headerGroup in grouped)
        {
            var header = headerGroup.Key;

            // Switch ECU header if needed
            if (!string.IsNullOrEmpty(header) && header != _currentHeader)
            {
                var headerResp = await _obd.SendCommandAsync($"ATSH {header}", ct);
                if (!headerResp.Contains("OK"))
                {
                    _logger.LogWarning("Failed to set header {Header}: {Response}", header, headerResp);
                }
                _currentHeader = header;
                await Task.Delay(50, ct);
            }

            // Deduplicate PIDs within this header group
            var uniquePids = headerGroup
                .GroupBy(p => p.ModeAndPid.ToUpperInvariant())
                .ToList();

            foreach (var pidGroup in uniquePids)
            {
                var modeAndPid = pidGroup.Key;
                var cacheKey = $"{header}:{modeAndPid}";

                if (!responseCache.TryGetValue(cacheKey, out var cached))
                {
                    var response = await _obd.SendCommandAsync(pidGroup.First().ModeAndPid, ct);
                    var data = ParseResponseBytes(response, pidGroup.First());
                    cached = (response, data);
                    responseCache[cacheKey] = cached;
                    await Task.Delay(50, ct);
                }

                foreach (var pid in pidGroup)
                {
                    var result = new BoltPidResult
                    {
                        Pid = pid,
                        RawResponse = cached.Response
                    };

                    if (cached.Data.Length > 0 &&
                        !cached.Response.Contains("NO DATA") &&
                        !cached.Response.Contains("ERROR") &&
                        !cached.Response.Contains("UNABLE TO CONNECT"))
                    {
                        try
                        {
                            result.Value = EquationEvaluator.Evaluate(pid.Equation, cached.Data);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to evaluate equation for {Pid}: {Equation}",
                                pid.ShortName, pid.Equation);
                        }
                    }

                    results.Add(result);
                }
            }
        }

        return results;
    }

    private static byte[] ParseResponseBytes(string response, BoltPid pid)
    {
        // Response may be multi-line for long responses; concatenate all hex data
        var allBytes = new List<byte>();

        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            // Skip non-hex lines
            if (trimmed.Contains("SEARCHING") || trimmed.Contains("NO DATA") ||
                trimmed.Contains("ERROR") || trimmed.Contains("UNABLE"))
                continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Length == 2 && IsHex(part))
                    allBytes.Add(Convert.ToByte(part, 16));
            }
        }

        if (allBytes.Count == 0)
            return [];

        // Strip the response prefix to get data bytes
        // Mode 22 (UDS): response starts with 62 + 2-byte DID = 3 bytes to skip
        // Mode 01 (standard): response starts with 41 + 1-byte PID = 2 bytes to skip
        int skipBytes;
        if (pid.IsUds)
            skipBytes = 3; // 62 XX XX
        else if (pid.IsStandardObd)
            skipBytes = 2; // 41 XX
        else
            skipBytes = 0;

        if (allBytes.Count <= skipBytes)
            return [];

        return allBytes.Skip(skipBytes).ToArray();
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
