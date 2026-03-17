public class BoltPid
{
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string ModeAndPid { get; set; } = "";
    public string Equation { get; set; } = "";
    public DecoderDefinition? Decoder { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public string Units { get; set; } = "";
    public string Header { get; set; } = "";
    public char Priority { get; set; }
    public bool IsUds => ModeAndPid.StartsWith("22", StringComparison.OrdinalIgnoreCase);
    public bool IsStandardObd => ModeAndPid.StartsWith("01", StringComparison.OrdinalIgnoreCase);
    public bool IsComputed => string.IsNullOrEmpty(ModeAndPid);
}

public static class BoltPidLoader
{
    private static readonly char[] PriorityChars = { '*', '!', '+', '-', '?' };

    public static List<BoltPid> LoadFromCsv(string filePath)
    {
        var pids = new List<BoltPid>();
        var lines = File.ReadAllLines(filePath);

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(',', 8);
            if (parts.Length < 8) continue;

            var name = parts[0].Trim();
            if (name.StartsWith('~')) continue;
            if (string.IsNullOrEmpty(name)) continue;

            char priority = ' ';
            if (name.Length > 0 && PriorityChars.Contains(name[0]))
            {
                priority = name[0];
                name = name[1..].Trim();
            }

            var modeAndPid = parts[2].Trim();
            var equation = parts[3].Trim();

            if (string.IsNullOrEmpty(modeAndPid) && string.IsNullOrEmpty(equation))
                continue;

            var pid = new BoltPid
            {
                Name = name,
                ShortName = parts[1].Trim(),
                ModeAndPid = modeAndPid,
                Equation = equation,
                Units = parts[6].Trim(),
                Header = parts[7].Trim(),
                Priority = priority
            };

            if (double.TryParse(parts[4].Trim(), out var min)) pid.MinValue = min;
            if (double.TryParse(parts[5].Trim(), out var max)) pid.MaxValue = max;

            pids.Add(pid);
        }

        return pids;
    }
}
