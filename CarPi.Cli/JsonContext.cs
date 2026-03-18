using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DidScanResult))]
internal partial class DidScanResultContext : JsonSerializerContext;
