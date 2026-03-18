using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class DataSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Vehicle { get; set; } = "";
    public string? Command { get; set; }
    public string? DefinitionSource { get; set; }
    public string? Filter { get; set; }
    public List<EcuStatus> Ecus { get; set; } = [];
    public List<SignalReading> Readings { get; set; } = [];

    public void Save(string filePath)
    {
        var json = JsonSerializer.Serialize(this, DataSnapshotContext.Default.DataSnapshot);
        File.WriteAllText(filePath, json + Environment.NewLine);
    }

    public static string GenerateFileName(DateTimeOffset timestamp)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".carpi", "snapshots");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"snapshot_{timestamp:yyyyMMdd_HHmmss}.json");
    }
}

public sealed class EcuStatus
{
    public string Header { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Online { get; set; }
}

public sealed class SignalReading
{
    // Canonical IDs — populated when definitions are available
    public string? SignalId { get; set; }
    public string? BindingId { get; set; }
    public string? PresentationId { get; set; }

    // Always populated
    public string? Ecu { get; set; }
    public string? Did { get; set; }
    public string Name { get; set; } = "";

    // Decoded value — null for raw/unknown DIDs
    public double? Value { get; set; }
    public string? Units { get; set; }
    public string? FormattedValue { get; set; }

    // Raw response — always available
    public string? RawResponse { get; set; }
    public string? DataBytes { get; set; }
    public int? ByteCount { get; set; }
}
