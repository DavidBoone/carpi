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

    private Adapter? _adapter;
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

        // Every proxy these calls hand back registers a match rule on the shared system-bus
        // connection, which holds a reference to it. Undisposed, they are unreachable to the
        // GC as garbage but reachable from D-Bus forever — so an adapter that is asleep, and
        // therefore a connect that fails every few minutes, leaks steadily. Take ownership of
        // the one we use and drop the rest immediately.
        var adapter = adapters[0];
        for (int i = 1; i < adapters.Count; i++)
            adapters[i].Dispose();
        _adapter = adapter;

        try {
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
        catch {
            // A half-open transport is never handed back to the caller, so nothing else will
            // ever call CloseAsync on it. Unwind here or the proxies are lost.
            await TearDownAsync();
            throw;
        }
    }

    /// <summary>
    /// Releases every BlueZ proxy this transport owns. Safe to call twice, and safe to call
    /// on a transport that never finished opening.
    /// </summary>
    private async Task TearDownAsync()
    {
        if (_notifyChar != null) {
            try {
                _notifyChar.Value -= OnNotification;
                if (_isOpen)
                    await _notifyChar.StopNotifyAsync();
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Error stopping BLE notifications.");
            }
        }

        if (_device != null) {
            try {
                if (_isOpen)
                    await _device.DisconnectAsync();
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Error disconnecting BLE device.");
            }
        }

        if (!ReferenceEquals(_writeChar, _notifyChar))
            Try(() => _writeChar?.Dispose());
        Try(() => _notifyChar?.Dispose());
        Try(() => _device?.Dispose());
        Try(() => _adapter?.Dispose());

        _writeChar = null;
        _notifyChar = null;
        _device = null;
        _adapter = null;
        _isOpen = false;

        void Try(Action a) { try { a(); } catch { } }
    }

    /// <summary>
    /// Lists every BLE device the radio sees. The counterpart to the targeted scan below —
    /// used when the adapter's MAC isn't known yet, which is the normal state the first time
    /// a Pi meets a car.
    /// </summary>
    public static async Task<IReadOnlyList<(string Address, string Name, bool IsLikelyObd)>> ScanForDevicesAsync(
        TimeSpan duration, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("BLE scanning requires Linux with BlueZ.");

        var adapters = await BlueZManager.GetAdaptersAsync();
        if (adapters.Count == 0)
            throw new InvalidOperationException("No Bluetooth adapters found.");

        var adapter = adapters[0];
        for (int i = 1; i < adapters.Count; i++)
            adapters[i].Dispose();
        var found = new List<(string Address, string Name, bool IsLikelyObd)>();

        try {

        DeviceChangeEventHandlerAsync handler = async (Adapter sender, DeviceFoundEventArgs args) =>
        {
            var address = await args.Device.GetAddressAsync();
            var name = "(unknown)";
            try { name = await args.Device.GetNameAsync(); } catch { }

            lock (found)
            {
                if (found.Any(f => f.Address == address)) return;
                found.Add((address, name, BleConstants.IsLikelyObd(name)));
            }
        };

        // Devices BlueZ already knows about — paired, or cached from an earlier scan. These
        // never raise DeviceFound again, so a discovery-only scan is blind to exactly the
        // adapter you use most. Seed from the known list first, then discover on top.
        foreach (var known in await adapter.GetDevicesAsync())
        {
            try
            {
                var address = await known.GetAddressAsync();
                var name = "(unknown)";
                try { name = await known.GetNameAsync(); } catch { }
                lock (found)
                {
                    if (!found.Any(f => f.Address == address))
                        found.Add((address, name, BleConstants.IsLikelyObd(name)));
                }
            }
            catch { }
            finally { try { known.Dispose(); } catch { } }
        }

        adapter.DeviceFound += handler;
        try
        {
            await adapter.StartDiscoveryAsync();
            try
            {
                await Task.Delay(duration, ct);
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

        lock (found)
            return found.ToList();
        }
        finally {
            adapter.Dispose();
        }
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
        await TearDownAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync();
    }

    public void Dispose()
    {
        TearDownAsync().GetAwaiter().GetResult();
    }
}
#endif
