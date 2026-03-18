#if !MACCATALYST
using System.Text;
using System.Threading.Channels;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Logging;

public class BleObdTransport : IObdTransport
{

    private static readonly Dictionary<string, object> EmptyOptions = new();

    private readonly string _deviceAddress;
    private readonly ILogger _logger;
    private readonly Channel<char> _readBuffer = Channel.CreateUnbounded<char>();

    private Device? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private bool _isOpen;

    public bool IsOpen => _isOpen;

    public BleObdTransport(string deviceAddress, ILogger logger)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("BLE transport requires Linux with BlueZ.");

        _deviceAddress = deviceAddress;
        _logger = logger;
    }

    public async Task OpenAsync(CancellationToken ct)
    {
        _logger.LogDebug("Getting Bluetooth adapter...");
        var adapters = await BlueZManager.GetAdaptersAsync();
        if (adapters.Count == 0)
            throw new InvalidOperationException("No Bluetooth adapters found.");

        var adapter = adapters[0];

        _logger.LogDebug("Looking for device {Address}...", _deviceAddress);
        _device = await adapter.GetDeviceAsync(_deviceAddress);

        if (_device == null)
        {
            _logger.LogDebug("Device not found in paired list, scanning...");
            _device = await ScanForDeviceAsync(adapter, ct);
        }

        _logger.LogDebug("Connecting to {Address}...", _deviceAddress);
        await _device.ConnectAsync();
        await _device.WaitForPropertyValueAsync("Connected", value: true, TimeSpan.FromSeconds(15));

        _logger.LogDebug("Waiting for GATT services...");
        await _device.WaitForPropertyValueAsync("ServicesResolved", value: true, TimeSpan.FromSeconds(15));

        var service = await _device.GetServiceAsync(BleConstants.ServiceUuid)
                   ?? await _device.GetServiceAsync(BleConstants.AltServiceUuid)
                   ?? throw new InvalidOperationException(
                       $"ELM327 GATT service not found (tried {BleConstants.ServiceUuid} and {BleConstants.AltServiceUuid}). " +
                       "This device may not be an ELM327 BLE adapter.");

        var serviceUuid = await service.GetUUIDAsync();
        _logger.LogDebug("Found GATT service: {UUID}", serviceUuid);

        // Determine characteristic UUIDs based on which service was found
        bool isAltService = serviceUuid.StartsWith("0000ffe0");
        string notifyUuid = isAltService ? BleConstants.AltCharUuid : BleConstants.NotifyCharUuid;
        string writeUuid = isAltService ? BleConstants.AltCharUuid : BleConstants.WriteCharUuid;

        _notifyChar = await service.GetCharacteristicAsync(notifyUuid)
            ?? throw new InvalidOperationException($"Notify characteristic {notifyUuid} not found.");

        if (writeUuid != notifyUuid)
        {
            _writeChar = await service.GetCharacteristicAsync(writeUuid)
                ?? throw new InvalidOperationException($"Write characteristic {writeUuid} not found.");
        }
        else
        {
            _writeChar = _notifyChar;
            _logger.LogDebug("Using single characteristic for both read and write.");
        }

        _notifyChar.Value += OnNotification;
        try
        {
            await _notifyChar.StartNotifyAsync();
        }
        catch
        {
            _notifyChar.Value -= OnNotification;
            throw;
        }

        _isOpen = true;
        _logger.LogInformation("BLE transport connected.");
    }

    private async Task<Device> ScanForDeviceAsync(Adapter adapter, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Device>();
        using var reg = ct.Register(() => tcs.TrySetCanceled());

        DeviceChangeEventHandlerAsync handler = async (Adapter sender, DeviceFoundEventArgs args) =>
        {
            var found = args.Device;
            var addr = await found.GetAddressAsync();
            if (string.Equals(addr, _deviceAddress, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(found);
        };

        adapter.DeviceFound += handler;
        try
        {
            await adapter.StartDiscoveryAsync();
            try
            {
                return await tcs.Task;
            }
            finally
            {
                await adapter.StopDiscoveryAsync();
            }
        }
        finally
        {
            adapter.DeviceFound -= handler;
        }
    }

    private async Task OnNotification(GattCharacteristic sender, GattCharacteristicValueEventArgs e)
    {
        foreach (var b in e.Value)
        {
            await _readBuffer.Writer.WriteAsync((char)b);
        }
    }

    public async Task WriteAsync(string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r");

        // BLE has a 20-byte default MTU; chunk if needed
        const int chunkSize = 20;
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = bytes.AsSpan(i, Math.Min(chunkSize, bytes.Length - i)).ToArray();
            await _writeChar!.WriteValueAsync(chunk, EmptyOptions);
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
                _notifyChar.Value -= OnNotification;
                await _notifyChar.StopNotifyAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping BLE notifications.");
            }
        }

        if (_device != null)
        {
            try { await _device.DisconnectAsync(); }
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
}
#endif
