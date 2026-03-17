public sealed record SignalDefinition(
    string Id,
    string? FamilyId,
    string Name,
    string ShortName,
    string? Description,
    char Priority,
    SignalCategory Category,
    SignalCardinality Cardinality,
    IReadOnlyList<SignalDimension> Dimensions,
    IReadOnlyList<SignalBinding> Bindings,
    IReadOnlyList<SignalPresentation> Presentations,
    IReadOnlyList<string> Tags,
    int SchemaVersion = 1);

public enum SignalCategory
{
    Unknown,
    Vehicle,
    Battery,
    Charging,
    Thermal,
    Drivetrain,
    Climate,
    Body,
    Diagnostic
}

public enum SignalCardinality
{
    Scalar,
    Indexed,
    Vector
}

public sealed record SignalDimension(
    string Name,
    int? Index = null,
    string? Label = null);

public sealed record SignalBinding(
    string Id,
    Applicability Applicability,
    EcuReference Ecu,
    QueryDefinition Query,
    DecodeDefinition Decode,
    bool Preferred = true,
    int Rank = 0,
    string? Provenance = null);

public sealed record Applicability(
    string? Make = null,
    string? Model = null,
    YearRange? ModelYears = null,
    string? Platform = null,
    string? Trim = null,
    string? Market = null);

public sealed record YearRange(
    int? Start = null,
    int? End = null);

public sealed record EcuReference(
    string LogicalId,
    string Header,
    string? Protocol = null,
    string? Role = null);

public sealed record QueryDefinition(
    string Service,
    string Identifier,
    string? RawCommand = null,
    string? ResponseService = null);

public sealed record DecodeDefinition(
    DecoderDefinition? Decoder = null,
    string? LegacyEquation = null,
    string? DecoderKind = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record SignalPresentation(
    string Id,
    string Name,
    string ShortName,
    string Units,
    double? MinValue = null,
    double? MaxValue = null,
    DecodeDefinition? DecodeOverride = null,
    int? Index = null,
    int DisplayOrder = 0);

public sealed record ResolvedSignal(
    SignalDefinition Signal,
    SignalBinding Binding,
    SignalPresentation Presentation);
