using System.Globalization;
using System.Text;

public static class BoltDefinitionExporter
{
    public static string ExportToCsv(IEnumerable<BoltPid> pids)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Name,ShortName,ModeAndPID,Equation,Min Value,Max Value,Units,Header");

        foreach (var pid in pids)
        {
            var name = pid.Priority == ' ' ? pid.Name : $"{pid.Priority}{pid.Name}";
            var equation = pid.Equation;
            if (string.IsNullOrWhiteSpace(equation) && pid.Decoder != null)
                equation = DecoderDefinitionLegacyConverter.ToLegacyEquation(pid.Decoder) ?? "";

            builder.AppendLine(string.Join(",",
                Escape(name),
                Escape(pid.ShortName),
                Escape(pid.ModeAndPid),
                Escape(equation),
                Escape(pid.MinValue.ToString(CultureInfo.InvariantCulture)),
                Escape(pid.MaxValue.ToString(CultureInfo.InvariantCulture)),
                Escape(pid.Units),
                Escape(pid.Header)));
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
