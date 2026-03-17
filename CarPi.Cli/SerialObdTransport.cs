using System.IO.Ports;

public class SerialObdTransport : IObdTransport
{
    private readonly string _port;
    private readonly int _baudRate;
    private readonly int _readTimeout;
    private readonly int _writeTimeout;
    private SerialPort? _serial;

    public bool IsOpen => _serial?.IsOpen ?? false;

    public SerialObdTransport(string port, int baudRate, int readTimeout, int writeTimeout)
    {
        _port = port;
        _baudRate = baudRate;
        _readTimeout = readTimeout;
        _writeTimeout = writeTimeout;
    }

    public Task OpenAsync(CancellationToken ct)
    {
        _serial = new SerialPort(_port, _baudRate)
        {
            ReadTimeout = _readTimeout,
            WriteTimeout = _writeTimeout,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            NewLine = "\r",
            DtrEnable = true,
            RtsEnable = true
        };
        _serial.Open();
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (_serial?.IsOpen == true)
            _serial.Close();
        return Task.CompletedTask;
    }

    public Task WriteAsync(string command, CancellationToken ct)
    {
        _serial!.WriteLine(command);
        return Task.CompletedTask;
    }

    public async Task<char?> ReadCharAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_serial!.BytesToRead > 0)
                return (char)_serial.ReadChar();

            await Task.Delay(20, ct);
        }
        return null;
    }

    public void DiscardBuffers()
    {
        if (_serial?.IsOpen == true)
        {
            _serial.DiscardInBuffer();
            _serial.DiscardOutBuffer();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_serial?.IsOpen == true)
            _serial.Close();
        _serial?.Dispose();
    }
}
