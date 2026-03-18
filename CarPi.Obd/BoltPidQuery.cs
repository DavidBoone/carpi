using Microsoft.Extensions.Logging;

public class BoltPidResult
{
    public BoltPid Pid { get; set; } = null!;
    public double? Value { get; set; }
    public string RawResponse { get; set; } = "";
    public string? SignalId { get; set; }
    public string? BindingId { get; set; }
    public string? PresentationId { get; set; }
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

    public async Task<List<BoltPidResult>> QueryResolvedAsync(List<ResolvedPid> resolved, CancellationToken ct)
    {
        // Build a lookup from BoltPid identity to canonical IDs
        var idLookup = new Dictionary<BoltPid, ResolvedPid>();
        var pids = new List<BoltPid>();
        foreach (var r in resolved)
        {
            if (!r.Pid.IsComputed)
            {
                pids.Add(r.Pid);
                idLookup[r.Pid] = r;
            }
        }

        var results = await QueryPidsAsync(pids, ct);

        // Stamp canonical IDs onto results
        foreach (var result in results)
        {
            if (idLookup.TryGetValue(result.Pid, out var res))
            {
                result.SignalId = res.SignalId;
                result.BindingId = res.BindingId;
                result.PresentationId = res.PresentationId;
            }
        }

        return results;
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

                // Set flow control header and separation time per-ECU.
                // ATFCSH must match the request address so the adapter sends
                // FC frames to the right ECU during multi-frame ISO-TP responses.
                // ATFCSD 30 00 19 = continue all frames, 25ms separation.
                // The separation time gives the adapter's small buffer time to
                // drain each frame over BLE before the next CAN frame arrives.
                await _obd.SendCommandAsync($"ATFCSH {header}", ct);
                await _obd.SendCommandAsync("ATFCSD 30 00 19", ct);
                await _obd.SendCommandAsync("ATFCSM1", ct);
                _currentHeader = header;

                // Probe ECU with TesterPresent before querying.
                // Skips the entire ECU if it's offline — avoids wasting time on
                // individual NO DATA responses for every PID.
                var probeResp = await _obd.SendCommandAsync("3E00", 100, 2000, ct);
                if (string.IsNullOrEmpty(probeResp) || probeResp.Contains("NO DATA") ||
                    probeResp.Contains("ERROR") || probeResp.Contains("UNABLE"))
                {
                    _logger.LogDebug("ECU {Header} not responding, skipping", header);
                    continue;
                }
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
                    // Large ISO-TP responses (multi-frame) need a longer read timeout.
                    // A 192-byte cell voltage response is ~30 BLE notifications.
                    // Check all PIDs sharing this DID to find the true expected size.
                    var expectedBytes = pidGroup.Max(p => EstimateExpectedBytes(p));
                    var readTimeout = expectedBytes > 50 ? 10000 : 5000;

                    var response = await _obd.SendCommandAsync(pidGroup.First().ModeAndPid, 200, readTimeout, ct);
                    var data = ParseResponseBytes(response, pidGroup.First());

                    // Don't retry UDS negative responses (7F xx xx) — they're definitive.
                    bool isNegativeResponse = response.TrimStart().StartsWith("7F")
                        || response.Replace(" ", "").TrimStart().StartsWith("7F");

                    if (isNegativeResponse)
                    {
                        var nrc = ParseNrc(response);
                        _logger.LogDebug("{Pid}: {Nrc}", modeAndPid, nrc);
                    }

                    // If we know the expected size and got fewer bytes, retry.
                    // Truncation from BLE/ELM327 buffer issues is intermittent —
                    // large ISO-TP responses (e.g., 192-byte cell voltages) may need
                    // several attempts on cheap ELM327 clones over BLE.
                    // If the response is identical on retry, it's not truncation —
                    // the ECU genuinely returns fewer bytes than the equation expects.
                    var maxRetries = expectedBytes > 50 ? 4 : 2;
                    string? prevResponse = null;
                    for (int retry = 0; retry < maxRetries && !isNegativeResponse &&
                        expectedBytes > 0 && data.Length < expectedBytes; retry++)
                    {
                        if (prevResponse != null && response == prevResponse)
                        {
                            _logger.LogDebug("{Pid}: ECU returns {Actual} bytes, definition expects {Expected}",
                                modeAndPid, data.Length, expectedBytes);
                            break; // Consistent short response — not truncation
                        }
                        prevResponse = response;
                        _logger.LogWarning("Truncated response for {Pid}: got {Actual} bytes, expected {Expected}. Raw: [{Raw}]. Retry {Retry}...",
                            modeAndPid, data.Length, expectedBytes, response.Replace('\n', '|'), retry + 1);
                        await Task.Delay(500, ct);
                        response = await _obd.SendCommandAsync(pidGroup.First().ModeAndPid, 300, readTimeout, ct);
                        data = ParseResponseBytes(response, pidGroup.First());
                    }
                    if (!isNegativeResponse && expectedBytes > 0 && data.Length < expectedBytes
                        && response != prevResponse)
                        _logger.LogWarning("Response for {Pid} still truncated after retries: got {Actual}/{Expected} bytes",
                            modeAndPid, data.Length, expectedBytes);

                    cached = (response, data);
                    responseCache[cacheKey] = cached;
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
                            result.Value = pid.Decoder != null
                                ? pid.Decoder.Evaluate(cached.Data)
                                : EquationEvaluator.Evaluate(pid.Equation, cached.Data);
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
        // Response may be multi-line for long responses; concatenate all hex data.
        // Handles both spaced ("0E 3D 0E") and compact ("0E3D0E") formats (ATS1/ATS0).
        var allBytes = new List<byte>();

        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            // Skip non-hex lines
            if (trimmed.Contains("SEARCHING") || trimmed.Contains("NO DATA") ||
                trimmed.Contains("ERROR") || trimmed.Contains("UNABLE"))
                continue;

            if (trimmed.Contains(' '))
            {
                // Spaced format: "62 2C 28 0E 3D"
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.Length == 2 && IsHex(part))
                        allBytes.Add(Convert.ToByte(part, 16));
                }
            }
            else
            {
                // Compact format (ATS0): "622C280E3D" or with ISO-TP
                // sequence prefix: "0:622C280E3D", "A:0E3F0E3D"
                // Skip ISO-TP First Frame length lines like "0C3" (odd length, 3 chars)
                var hex = trimmed;
                var colonIdx = hex.IndexOf(':');
                if (colonIdx >= 0 && colonIdx <= 2)
                    hex = hex[(colonIdx + 1)..]; // Strip "0:", "A:", "1F:" prefix
                else if (hex.Length <= 3 && IsHex(hex))
                    continue; // ISO-TP First Frame PCI (length indicator), not data

                for (int i = 0; i + 1 < hex.Length; i += 2)
                {
                    var pair = hex.Substring(i, 2);
                    if (IsHex(pair))
                        allBytes.Add(Convert.ToByte(pair, 16));
                    else
                        break;
                }
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

    /// <summary>
    /// Estimates expected data byte count from the equation's byte references.
    /// Returns 0 if unknown. Used to detect truncated ISO-TP responses.
    /// </summary>
    private static int EstimateExpectedBytes(BoltPid pid)
    {
        if (pid.Decoder != null)
            return pid.Decoder.StartIndex + pid.Decoder.Length;

        if (string.IsNullOrWhiteSpace(pid.Equation))
            return 0;

        int maxIndex = -1;

        // Check $N indexed byte references
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(pid.Equation, @"\$(\d+)"))
        {
            var idx = int.Parse(m.Groups[1].Value);
            if (idx > maxIndex) maxIndex = idx;
        }

        // Check A/B/C/D letter references — must be standalone (not inside
        // function names like "Signed" or "SIGNED" which contain D/A/etc.)
        var eq = pid.Equation;
        for (int i = 0; i < eq.Length; i++)
        {
            int idx = eq[i] switch { 'A' => 0, 'B' => 1, 'C' => 2, 'D' => 3, _ => -1 };
            if (idx < 0) continue;

            bool prevIsLetter = i > 0 && char.IsLetter(eq[i - 1]);
            bool nextIsLetter = i + 1 < eq.Length && char.IsLetter(eq[i + 1]);
            if (!prevIsLetter && !nextIsLetter && idx > maxIndex)
                maxIndex = idx;
        }

        return maxIndex >= 0 ? maxIndex + 1 : 0;
    }

    private static string ParseNrc(string response)
    {
        // Parse UDS negative response: 7F <service> <nrc>
        var hex = response.Replace(" ", "").Trim();
        if (hex.Length >= 6 && hex.StartsWith("7F"))
        {
            var nrcByte = Convert.ToByte(hex[4..6], 16);
            var name = nrcByte switch
            {
                0x10 => "generalReject",
                0x11 => "serviceNotSupported",
                0x12 => "subFunctionNotSupported",
                0x13 => "incorrectMessageLength",
                0x14 => "responseTooLong",
                0x22 => "conditionsNotCorrect",
                0x24 => "requestSequenceError",
                0x31 => "requestOutOfRange",
                0x33 => "securityAccessDenied",
                0x35 => "invalidKey",
                0x72 => "generalProgrammingFailure",
                0x78 => "requestCorrectlyReceivedResponsePending",
                _ => $"0x{nrcByte:X2}"
            };
            return $"negative response: {name}";
        }
        return $"negative response: {response}";
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
