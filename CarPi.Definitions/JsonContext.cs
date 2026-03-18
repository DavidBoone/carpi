using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(DataSnapshot))]
internal partial class DataSnapshotContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(DefinitionCatalog))]
internal partial class DefinitionCatalogContext : JsonSerializerContext;
