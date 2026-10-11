using System.Text.Json.Serialization;

namespace CarPi.Daemon;

/// <summary>
/// Source-generated JSON metadata for the wire contract. Native AOT has no reflection-based
/// serializer, so every type crossing an endpoint has to be listed here; without it the
/// minimal-API endpoint graph fails to build and *every* route 500s, not just the JSON one.
/// Harmless on the JIT build — it just takes the fast path instead.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConnectRequest))]
[JsonSerializable(typeof(ConnectResponse))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ScanDevice))]
[JsonSerializable(typeof(ScanResponse))]
[JsonSerializable(typeof(PortsResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class DaemonJsonContext : JsonSerializerContext
{
}

/// <summary>Acknowledgement body. A named type because AOT cannot serialize an anonymous one.</summary>
public class OkResponse
{
    public bool Ok { get; set; } = true;
}
