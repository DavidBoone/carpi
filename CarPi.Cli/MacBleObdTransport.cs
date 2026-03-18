#if MACCATALYST
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

public class MacBleObdTransport : IObdTransport
{
    private const string ServiceUuid = "0000fff0-0000-1000-8000-00805f9b34fb";
    private const string AltServiceUuid = "0000ffe0-0000-1000-8000-00805f9b34fb";
    private const string NotifyCharUuid = "0000fff1-0000-1000-8000-00805f9b34fb";
    private const string WriteCharUuid = "0000fff2-0000-1000-8000-00805f9b34fb";
    private const string AltCharUuid = "0000ffe1-0000-1000-8000-00805f9b34fb";

    private readonly string _deviceAddress;
    private readonly ILogger _logger;
    private readonly Channel<char> _readBuffer = Channel.CreateUnbounded<char>();

    private IAdapter? _adapter;
    private IDevice? _device;
    private ICharacteristic? _writeChar;
    private ICharacteristic? _notifyChar;
    private bool _isOpen;

    public bool IsOpen => _isOpen;

    public MacBleObdTransport(string deviceAddress, ILogger logger)
    {
        _deviceAddress = deviceAddress;
        _logger = logger;
    }

    public async Task OpenAsync(CancellationToken ct)
    {
        var ble = CrossBluetoothLE.Current;
        _adapter = ble.Adapter;

        await WaitForBluetoothReadyAsync(ble, ct);

        _logger.LogDebug("Scanning for BLE device {Address}...", _deviceAddress);
        _device = await ScanForDeviceAsync(ct);

        _logger.LogDebug("Connecting to {Address}...", _deviceAddress);
        await _adapter.ConnectToDeviceAsync(_device, cancellationToken: ct);

        _logger.LogDebug("Discovering GATT services...");
        var services = await _device.GetServicesAsync();

        var service = services.FirstOrDefault(s => s.Id == Guid.Parse(ServiceUuid))
                   ?? services.FirstOrDefault(s => s.Id == Guid.Parse(AltServiceUuid))
                   ?? throw new InvalidOperationException(
                       $"ELM327 GATT service not found (tried {ServiceUuid} and {AltServiceUuid}). " +
                       "This device may not be an ELM327 BLE adapter.");

        bool isAltService = service.Id == Guid.Parse(AltServiceUuid);
        string notifyUuid = isAltService ? AltCharUuid : NotifyCharUuid;
        string writeUuid = isAltService ? AltCharUuid : WriteCharUuid;

        _logger.LogDebug("Found GATT service: {UUID}", service.Id);

        var characteristics = await service.GetCharacteristicsAsync();

        _notifyChar = characteristics.FirstOrDefault(c => c.Id == Guid.Parse(notifyUuid))
            ?? throw new InvalidOperationException($"Notify characteristic {notifyUuid} not found.");

        if (writeUuid != notifyUuid)
        {
            _writeChar = characteristics.FirstOrDefault(c => c.Id == Guid.Parse(writeUuid))
                ?? throw new InvalidOperationException($"Write characteristic {writeUuid} not found.");
        }
        else
        {
            _writeChar = _notifyChar;
            _logger.LogDebug("Using single characteristic for both read and write.");
        }

        _notifyChar.ValueUpdated += OnNotification;
        await _notifyChar.StartUpdatesAsync();

        _isOpen = true;
        _logger.LogInformation("BLE transport connected (macOS).");
    }

    private async Task<IDevice> ScanForDeviceAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IDevice>();
        using var reg = ct.Register(() => tcs.TrySetCanceled());

        // On macOS/CoreBluetooth, devices are identified by a system-assigned UUID, not MAC address.
        // The user can provide either the CoreBluetooth UUID or we match by name/advertisement.
        // Try parsing as GUID first (CoreBluetooth UUID), otherwise match by name containing the address.
        Guid? targetGuid = Guid.TryParse(_deviceAddress, out var g) ? g : null;

        void handler(object? sender, DeviceEventArgs args)
        {
            var found = args.Device;
            if (targetGuid.HasValue && found.Id == targetGuid.Value)
            {
                tcs.TrySetResult(found);
            }
            else if (!targetGuid.HasValue)
            {
                // Match by name or advertised data containing the address string
                var name = found.Name ?? "";
                if (name.Contains(_deviceAddress, StringComparison.OrdinalIgnoreCase)
                    || found.Id.ToString().Contains(_deviceAddress, StringComparison.OrdinalIgnoreCase))
                {
                    tcs.TrySetResult(found);
                }
            }
        }

        _adapter!.DeviceDiscovered += handler;
        try
        {
            await _adapter.StartScanningForDevicesAsync(cancellationToken: ct);
            var device = await tcs.Task;
            await _adapter.StopScanningForDevicesAsync();
            return device;
        }
        finally
        {
            _adapter.DeviceDiscovered -= handler;
        }
    }

    private void OnNotification(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var bytes = e.Characteristic.Value;
        if (bytes == null) return;
        foreach (var b in bytes)
        {
            _readBuffer.Writer.TryWrite((char)b);
        }
    }

    public async Task WriteAsync(string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r");

        const int chunkSize = 20;
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = bytes.AsSpan(i, Math.Min(chunkSize, bytes.Length - i)).ToArray();
            await _writeChar!.WriteAsync(chunk, ct);
        }
    }

    public async Task<char?> ReadCharAsync(CancellationToken ct)
    {
        try
        {
            return await _readBuffer.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void DiscardBuffers()
    {
        while (_readBuffer.Reader.TryRead(out _)) { }
    }

    public async Task CloseAsync()
    {
        if (_notifyChar != null)
        {
            try
            {
                _notifyChar.ValueUpdated -= OnNotification;
                await _notifyChar.StopUpdatesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping BLE notifications.");
            }
        }

        if (_device != null && _adapter != null)
        {
            try { await _adapter.DisconnectDeviceAsync(_device); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting BLE device.");
            }
        }

        _isOpen = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isOpen)
            await CloseAsync();
    }

    public void Dispose()
    {
        if (_isOpen)
            CloseAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Scans for BLE devices and prints them to the console. Used by --scan-ble on macOS.
    /// </summary>
    public static async Task ScanAndListDevicesAsync(ILogger logger)
    {
        var ble = CrossBluetoothLE.Current;
        var adapter = ble.Adapter;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await WaitForBluetoothReadyAsync(ble, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        Console.WriteLine("Scanning for BLE devices (10 seconds)...");
        Console.WriteLine();

        var found = new List<(Guid Id, string Name)>();

        adapter.DeviceDiscovered += (sender, args) =>
        {
            var device = args.Device;
            var name = string.IsNullOrEmpty(device.Name) ? "(unknown)" : device.Name;

            if (!found.Any(f => f.Id == device.Id))
            {
                found.Add((device.Id, name));
                var marker = IsLikelyObd(name) ? " <-- possible OBD2 adapter" : "";
                Console.WriteLine($"  {device.Id}  {name}{marker}");
            }
        };

        adapter.ScanTimeout = 10000;
        await adapter.StartScanningForDevicesAsync();

        if (found.Count == 0)
        {
            Console.WriteLine("  (no devices found)");
        }

        Console.WriteLine();
        Console.WriteLine("Use --ble <UUID> to connect to a device.");
        Console.WriteLine("(macOS uses CoreBluetooth UUIDs instead of MAC addresses)");
    }

    /// <summary>
    /// CoreBluetooth starts in Unknown state and transitions asynchronously.
    /// Wait for it to settle before checking availability.
    /// </summary>
    private static async Task WaitForBluetoothReadyAsync(IBluetoothLE ble, CancellationToken ct)
    {
        if (ble.State == BluetoothState.On)
            return;

        if (ble.State != BluetoothState.Unknown)
            throw new InvalidOperationException($"Bluetooth is not available (state: {ble.State}). Enable Bluetooth in System Settings.");

        // State is Unknown — CBCentralManager hasn't finished initializing yet. Wait for it.
        var tcs = new TaskCompletionSource<BluetoothState>();
        using var reg = ct.Register(() => tcs.TrySetCanceled());

        void handler(object? sender, BluetoothStateChangedArgs args) => tcs.TrySetResult(args.NewState);

        ble.StateChanged += handler;
        try
        {
            // Double-check in case it changed between our check and subscribing
            if (ble.State != BluetoothState.Unknown)
            {
                tcs.TrySetResult(ble.State);
            }

            var state = await tcs.Task;
            if (state != BluetoothState.On)
                throw new InvalidOperationException($"Bluetooth is not available (state: {state}). Enable Bluetooth in System Settings.");
        }
        finally
        {
            ble.StateChanged -= handler;
        }
    }

    private static bool IsLikelyObd(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("obd") || lower.Contains("elm") || lower.Contains("v-link")
            || lower.Contains("veepeak") || lower.Contains("vgate") || lower.Contains("konnwei");
    }
}
#endif
