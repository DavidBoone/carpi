using System.Text;
using System.Text.Json;

public class DidReportGenerator
{
    private readonly Dictionary<string, KnownPid> _knownPids = new();

    public void LoadKnownPids(string definitionPath)
    {
        if (!File.Exists(definitionPath)) return;

        foreach (var pid in BoltDefinitionLoader.Load(definitionPath))
        {
            if (string.IsNullOrEmpty(pid.ModeAndPid) || pid.ModeAndPid.Length < 4) continue;
            if (!pid.ModeAndPid.StartsWith("22", StringComparison.OrdinalIgnoreCase)) continue;

            var did = pid.ModeAndPid[2..].ToUpperInvariant();
            var priority = pid.Priority == ' ' ? "" : pid.Priority.ToString();

            _knownPids[$"{pid.Header}:{did}"] = new KnownPid
            {
                Name = pid.Name,
                ShortName = pid.ShortName,
                Did = did,
                Header = pid.Header,
                Equation = pid.Equation,
                Units = pid.Units,
                Priority = priority
            };
        }
    }

    public async Task GenerateAsync(string jsonPath, string outputPath)
    {
        if (!File.Exists(jsonPath))
        {
            Console.Error.WriteLine($"Scan results file not found: {jsonPath}");
            return;
        }

        var json = await File.ReadAllTextAsync(jsonPath);
        var scanResult = JsonSerializer.Deserialize(json, DidScanResultContext.Default.DidScanResult);
        if (scanResult == null || scanResult.Count == 0)
        {
            Console.Error.WriteLine("No scan data found.");
            return;
        }

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html><head>");
        html.AppendLine("<meta charset='utf-8'>");
        html.AppendLine("<title>CarPi DID Scan Report</title>");
        html.AppendLine("<style>");
        html.AppendLine(CSS);
        html.AppendLine("</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>CarPi DID Scan Report</h1>");
        html.AppendLine($"<p class='generated'>Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");

        // Summary table
        html.AppendLine("<h2>Summary</h2>");
        html.AppendLine("<table class='summary'><tr><th>ECU</th><th>Name</th><th>DIDs Scanned</th><th>Positive Responses</th><th>Never Responded</th></tr>");

        foreach (var (header, dids) in scanResult)
        {
            var ecuName = GetEcuName(header);
            var total = dids.Count;
            var positive = dids.Count(d => d.Value.LastPositiveResponse != null);
            var never = total - positive;
            html.AppendLine($"<tr><td><code>{header}</code></td><td>{ecuName}</td><td>{total}</td><td class='pos'>{positive}</td><td>{never}</td></tr>");
        }
        html.AppendLine("</table>");

        // Per-ECU detail tables (positive responses only)
        foreach (var (header, dids) in scanResult)
        {
            var ecuName = GetEcuName(header);
            var positiveDids = dids.Where(d => d.Value.LastPositiveResponse != null).ToList();

            html.AppendLine($"<h2>{header} — {ecuName}</h2>");

            if (positiveDids.Count == 0)
            {
                html.AppendLine("<p class='none'>No positive responses.</p>");
                continue;
            }

            html.AppendLine("<table class='dids'>");
            html.AppendLine("<tr><th>DID</th><th>Bytes</th><th>Data</th><th>Decimal</th><th>Known PID</th><th>Notes</th></tr>");

            foreach (var (didHex, entry) in positiveDids)
            {
                var resp = entry.LastPositiveResponse!;
                var dataBytes = resp.Data ?? "";
                var byteParts = dataBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Compute decimal value for small responses
                var decimalStr = "";
                if (byteParts.Length > 0 && byteParts.Length <= 4)
                {
                    long val = 0;
                    foreach (var b in byteParts)
                        val = (val << 8) | Convert.ToByte(b, 16);
                    decimalStr = val.ToString("N0");
                }

                // Look up known PID
                var knownName = "";
                var knownInfo = "";
                var key = $"{header}:{didHex}";
                if (_knownPids.TryGetValue(key, out var known))
                {
                    knownName = $"<span class='known'>{Escape(known.ShortName)}</span>";
                    knownInfo = $"{Escape(known.Name)}";
                    if (!string.IsNullOrEmpty(known.Units))
                        knownInfo += $" ({Escape(known.Units)})";
                }

                // Heuristic notes
                var notes = new List<string>();
                if (!string.IsNullOrEmpty(knownInfo))
                    notes.Add(knownInfo);
                notes.AddRange(GuessContent(byteParts, resp.ByteCount));

                var stale = entry.LastResponse.Data == null && entry.LastPositiveResponse != null;
                var rowClass = stale ? " class='stale'" : "";

                html.AppendLine($"<tr{rowClass}>");
                html.AppendLine($"<td><code>{didHex}</code></td>");
                html.AppendLine($"<td>{resp.ByteCount}</td>");
                html.AppendLine($"<td><code>{Escape(dataBytes)}</code></td>");
                html.AppendLine($"<td class='num'>{decimalStr}</td>");
                html.AppendLine($"<td>{knownName}</td>");
                html.AppendLine($"<td>{string.Join("<br>", notes)}</td>");
                html.AppendLine("</tr>");
            }
            html.AppendLine("</table>");
        }

        html.AppendLine("</body></html>");

        await File.WriteAllTextAsync(outputPath, html.ToString());
        Console.WriteLine($"Report written to {outputPath}");
    }

    private static List<string> GuessContent(string[] bytes, int byteCount)
    {
        var notes = new List<string>();

        if (byteCount == 0 || bytes.Length == 0) return notes;

        // Check if all bytes are FF (uninitialized/max)
        if (bytes.All(b => b == "FF"))
        {
            notes.Add("All 0xFF (uninitialized or max)");
            return notes;
        }

        // Check if all bytes are 00 (zero/unused)
        if (bytes.All(b => b == "00"))
        {
            notes.Add("All zeros");
            return notes;
        }

        // Check if all bytes end with AA (padding pattern)
        if (bytes.Length > 4 && bytes.Skip(bytes.Length / 2).All(b => b == "AA"))
            notes.Add("AA padding — real data in first bytes only");

        // For 4-byte values, check against known odometer targets
        if (byteCount == 4)
        {
            long val = 0;
            foreach (var b in bytes)
                val = (val << 8) | Convert.ToByte(b, 16);

            // Check various odometer encodings (248,151 km)
            if (val > 200000 && val < 300000)
                notes.Add("Possible odometer (km, raw)?");
            if (val > 1500000 && val < 3000000)
                notes.Add("Possible odometer (km x10 or miles x10)?");
            if (val > 120000 && val < 200000)
                notes.Add("Possible odometer (miles, raw)?");
        }

        // For 2-byte values, check temperature patterns (offset by 40)
        if (byteCount == 2)
        {
            int val = (Convert.ToByte(bytes[0], 16) << 8) | Convert.ToByte(bytes[1], 16);
            if (val >= 0x0200 && val <= 0x0400)
                notes.Add("Possible voltage (typical cell range)");
        }

        return notes;
    }

    private static string GetEcuName(string header)
    {
        foreach (var (h, name) in DidScanner.KnownEcus)
        {
            if (h == header) return name;
        }
        return header switch
        {
            "7C0" => "IPC (Instrument Panel Cluster)",
            "7E2" => "BCM (Body Control Module)",
            "720" => "IPC (alternate)",
            _ => "Unknown"
        };
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private const string CSS = @"
        body { font-family: -apple-system, 'Segoe UI', Roboto, monospace; margin: 2em; background: #1a1a2e; color: #e0e0e0; }
        h1 { color: #00d4ff; border-bottom: 2px solid #00d4ff; padding-bottom: 0.3em; }
        h2 { color: #7fdbca; margin-top: 2em; }
        .generated { color: #888; font-size: 0.9em; }
        table { border-collapse: collapse; width: 100%; margin: 1em 0; }
        th, td { border: 1px solid #333; padding: 6px 10px; text-align: left; }
        th { background: #16213e; color: #00d4ff; font-weight: 600; }
        tr:nth-child(even) { background: #1a1a3e; }
        tr:hover { background: #222255; }
        code { color: #82aaff; }
        .num { text-align: right; color: #c3e88d; font-family: monospace; }
        .pos { color: #c3e88d; font-weight: bold; }
        .known { color: #ffcb6b; font-weight: 600; }
        .none { color: #666; font-style: italic; }
        .stale td { opacity: 0.6; }
        .stale td:first-child::after { content: ' (stale)'; color: #f78c6c; font-size: 0.8em; }
        .summary { max-width: 700px; }
        .dids { font-size: 0.9em; }
    ";
}

public class KnownPid
{
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Did { get; set; } = "";
    public string Header { get; set; } = "";
    public string Equation { get; set; } = "";
    public string Units { get; set; } = "";
    public string Priority { get; set; } = "";
}
