/// <summary>
/// Shared BLE constants used by both Linux and macOS transport implementations.
/// </summary>
public static class BleConstants
{
    public const string ServiceUuid = "0000fff0-0000-1000-8000-00805f9b34fb";
    public const string AltServiceUuid = "0000ffe0-0000-1000-8000-00805f9b34fb";
    public const string NotifyCharUuid = "0000fff1-0000-1000-8000-00805f9b34fb";
    public const string WriteCharUuid = "0000fff2-0000-1000-8000-00805f9b34fb";
    public const string AltCharUuid = "0000ffe1-0000-1000-8000-00805f9b34fb";

    public static bool IsLikelyObd(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("obd") || lower.Contains("elm") || lower.Contains("v-link")
            || lower.Contains("veepeak") || lower.Contains("vgate") || lower.Contains("konnwei");
    }
}
