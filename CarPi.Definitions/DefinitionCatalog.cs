using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public class DefinitionCatalog
{
    public int Version { get; set; } = 1;
    public string Vehicle { get; set; } = "";
    public List<SignalDefinition> Signals { get; set; } = [];

    public List<BoltPid> ToBoltPids()
    {
        var pids = new List<BoltPid>();

        foreach (var signal in Signals)
        {
            foreach (var binding in signal.Bindings)
            {
                var modeAndPid = binding.Query.RawCommand
                    ?? $"{binding.Query.Service}{binding.Query.Identifier}";

                foreach (var presentation in signal.Presentations)
                {
                    var decode = presentation.DecodeOverride ?? binding.Decode;
                    var equation = decode.LegacyEquation;
                    if (string.IsNullOrWhiteSpace(equation) && decode.Decoder != null)
                        equation = DecoderDefinitionLegacyConverter.ToLegacyEquation(decode.Decoder) ?? "";

                    pids.Add(new BoltPid
                    {
                        Name = presentation.Name,
                        ShortName = presentation.ShortName,
                        ModeAndPid = modeAndPid,
                        Equation = equation ?? "",
                        Decoder = decode.Decoder,
                        MinValue = presentation.MinValue ?? 0,
                        MaxValue = presentation.MaxValue ?? 0,
                        Units = presentation.Units,
                        Header = binding.Ecu.Header,
                        Priority = signal.Priority
                    });
                }
            }
        }

        return pids;
    }

    public List<ResolvedPid> ToResolvedPids()
    {
        var resolved = new List<ResolvedPid>();

        foreach (var signal in Signals)
        {
            foreach (var binding in signal.Bindings)
            {
                var modeAndPid = binding.Query.RawCommand
                    ?? $"{binding.Query.Service}{binding.Query.Identifier}";

                foreach (var presentation in signal.Presentations)
                {
                    var decode = presentation.DecodeOverride ?? binding.Decode;
                    var equation = decode.LegacyEquation;
                    if (string.IsNullOrWhiteSpace(equation) && decode.Decoder != null)
                        equation = DecoderDefinitionLegacyConverter.ToLegacyEquation(decode.Decoder) ?? "";

                    var pid = new BoltPid
                    {
                        Name = presentation.Name,
                        ShortName = presentation.ShortName,
                        ModeAndPid = modeAndPid,
                        Equation = equation ?? "",
                        Decoder = decode.Decoder,
                        MinValue = presentation.MinValue ?? 0,
                        MaxValue = presentation.MaxValue ?? 0,
                        Units = presentation.Units,
                        Header = binding.Ecu.Header,
                        Priority = signal.Priority
                    };

                    resolved.Add(new ResolvedPid(signal.Id, binding.Id, presentation.Id, pid));
                }
            }
        }

        return resolved;
    }

    public static DefinitionCatalog LoadFromJson(string filePath)
    {
        var options = CreateJsonOptions();
        var json = File.ReadAllText(filePath);
        var catalog = JsonSerializer.Deserialize<DefinitionCatalog>(json, options);
        return catalog ?? new DefinitionCatalog();
    }

    public void SaveToJson(string filePath)
    {
        var options = CreateJsonOptions();
        options.WriteIndented = true;
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(filePath, json + Environment.NewLine);
    }

    public static DefinitionCatalog FromBoltPids(IEnumerable<BoltPid> pids, string vehicle = "")
    {
        var normalizedSignals = NormalizeBoltPids(pids).Select(CreateSignalDefinition).ToList();
        return new DefinitionCatalog
        {
            Vehicle = vehicle,
            Signals = normalizedSignals
        };
    }

    private static SignalDefinition CreateSignalDefinition(NormalizedSignal normalized)
    {
        var primary = normalized.PrimaryPid;
        var category = InferCategory(primary);
        var signalId = CreateSignalId(normalized);
        List<SignalBinding> bindings = [];
        foreach (var bindingPid in normalized.BindingPids)
        {
            if (string.IsNullOrWhiteSpace(bindingPid.ModeAndPid))
                continue;

            bindings.Add(new SignalBinding(
                $"{bindingPid.Header}:{bindingPid.ModeAndPid}",
                new Applicability(),
                new EcuReference(bindingPid.Header, bindingPid.Header, Protocol: "obd"),
                new QueryDefinition(
                    bindingPid.ModeAndPid.Length >= 2 ? bindingPid.ModeAndPid[..2] : bindingPid.ModeAndPid,
                    bindingPid.ModeAndPid.Length > 2 ? bindingPid.ModeAndPid[2..] : "",
                    bindingPid.ModeAndPid,
                    bindingPid.ModeAndPid.StartsWith("22", StringComparison.OrdinalIgnoreCase) ? "62" : null),
                new DecodeDefinition(),
                bindingPid == normalized.PrimaryPid,
                normalized.BindingPids.IndexOf(bindingPid),
                "CSV import"));
        }

        return new SignalDefinition(
            signalId,
            null,
            normalized.CanonicalName,
            normalized.CanonicalShortName,
            null,
            primary.Priority,
            category,
            normalized.Index.HasValue ? SignalCardinality.Indexed : SignalCardinality.Scalar,
            normalized.Index.HasValue ? [new SignalDimension("index", normalized.Index, normalized.Index.Value.ToString("D2"))] : [],
            bindings,
            normalized.Presentations.Select((pid, index) => new SignalPresentation(
                $"{signalId}:{index}",
                pid.Name,
                pid.ShortName,
                pid.Units,
                pid.MinValue,
                pid.MaxValue,
                new DecodeDefinition(pid.Decoder, string.IsNullOrWhiteSpace(pid.Equation) ? null : pid.Equation),
                normalized.Index,
                index)).ToList(),
            [],
            1);
    }

    private static List<NormalizedSignal> NormalizeBoltPids(IEnumerable<BoltPid> pids)
    {
        var results = new List<NormalizedSignal>();

        foreach (var group in pids.GroupBy(CreateNormalizationKey))
        {
            var groupPids = group.ToList();
            var primary = groupPids[0];
            var parsed = ParseIndexedName(primary.Name, primary.ShortName);
            var bindingPids = groupPids
                .Where(p => !string.IsNullOrWhiteSpace(p.ModeAndPid))
                .GroupBy(p => $"{p.Header}:{p.ModeAndPid}")
                .Select(g => g.First())
                .ToList();

            results.Add(new NormalizedSignal(
                primary,
                parsed.BaseName ?? primary.Name,
                parsed.BaseShortName ?? primary.ShortName,
                parsed.Index,
                bindingPids,
                groupPids));
        }

        return results;
    }

    private static string CreateNormalizationKey(BoltPid pid)
    {
        var parsed = ParseIndexedName(pid.Name, pid.ShortName);
        var baseShortName = parsed.BaseShortName ?? pid.ShortName;

        if (!string.IsNullOrWhiteSpace(pid.ModeAndPid))
            return $"query:{pid.Header}:{pid.ModeAndPid}:{SanitizeId(baseShortName)}:{parsed.Index?.ToString() ?? ""}";

        return $"computed:{baseShortName}";
    }

    private static (string? BaseName, string? BaseShortName, int? Index) ParseIndexedName(string name, string shortName)
    {
        var match = Regex.Match(name, @"^(.*?)(?:\s+#|\s+-\s+)(\d+)(?:\s|$)");
        if (match.Success)
        {
            var baseName = match.Groups[1].Value.Trim().TrimEnd('-', '#').Trim();
            var index = int.Parse(match.Groups[2].Value);
            var shortMatch = Regex.Match(shortName, @"^(.*?)(?:\s+)(\d+)$");
            var baseShortName = shortMatch.Success ? shortMatch.Groups[1].Value.Trim() : shortName;
            return (baseName, baseShortName, index);
        }

        return (null, null, null);
    }

    private static SignalCategory InferCategory(BoltPid pid)
    {
        var source = $"{pid.Name} {pid.ShortName}".ToLowerInvariant();
        if (source.Contains("battery") || source.Contains("cell"))
            return SignalCategory.Battery;
        if (source.Contains("charge"))
            return SignalCategory.Charging;
        if (source.Contains("temp") || source.Contains("coolant") || source.Contains("heater"))
            return SignalCategory.Thermal;
        if (source.Contains("odometer") || source.Contains("speed"))
            return SignalCategory.Vehicle;
        return SignalCategory.Unknown;
    }

    private static string CreateSignalId(NormalizedSignal normalized)
    {
        if (normalized.BindingPids.Count > 0)
        {
            var binding = normalized.BindingPids[0];
            var suffix = normalized.Index.HasValue ? $":{normalized.Index.Value:D2}" : "";
            return $"{binding.Header}:{binding.ModeAndPid}:{SanitizeId(normalized.CanonicalShortName)}{suffix}";
        }

        return $"computed:{SanitizeId(normalized.CanonicalShortName)}";
    }

    private static string SanitizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unnamed";

        return new string(value
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray())
            .Trim('_');
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record ResolvedPid(
    string SignalId,
    string BindingId,
    string PresentationId,
    BoltPid Pid);

internal sealed record NormalizedSignal(
    BoltPid PrimaryPid,
    string CanonicalName,
    string CanonicalShortName,
    int? Index,
    List<BoltPid> BindingPids,
    List<BoltPid> Presentations);

