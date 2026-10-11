using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CarPi.Daemon;

public class ObdSessionException : Exception
{
    public int StatusCode { get; }

    public ObdSessionException(int statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Owns the one adapter this box has. Exactly one session may hold it; commands within a
/// session are serialized, because an ELM327 is strictly request/response and interleaving
/// two commands corrupts both replies.
///
/// Deliberately dumb: no scheduling, no logging of vehicle data, no retry loop. If the car
/// is asleep the connect simply fails and the caller decides what to do about it.
/// </summary>
public class ObdSessionManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ObdSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _idleTimeout;

    private ObdDevice? _device;
    private string? _sessionId;
    private string? _transport;
    private string? _target;
    private DateTimeOffset _connectedAt;
    private DateTimeOffset _lastActivity;

    public ObdSessionManager(ILogger<ObdSessionManager> logger, ILoggerFactory loggerFactory, TimeSpan idleTimeout)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _idleTimeout = idleTimeout;
    }

    public TimeSpan IdleTimeout => _idleTimeout;

    public StatusResponse GetStatus()
    {
        // Read-only snapshot — intentionally not gated, so /status still answers while a
        // long scan holds the semaphore. That's the whole point of having a status endpoint.
        var device = _device;
        return new StatusResponse {
            Connected = device != null,
            SessionId = _sessionId,
            Transport = _transport,
            Target = _target,
            ConnectedAt = device != null ? _connectedAt : null,
            LastActivity = device != null ? _lastActivity : null,
            IdleTimeoutSeconds = (int)_idleTimeout.TotalSeconds,
        };
    }

    /// <summary>
    /// Lists what the radio can see, so a caller with no adapter MAC can find one. Takes the
    /// gate because BlueZ discovery and an open GATT link contend for the same radio — but
    /// refuses immediately rather than queueing, since a caller waiting behind a live session
    /// would just time out.
    /// </summary>
    public async Task<ScanResponse> ScanAsync(TimeSpan duration, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct))
            throw new ObdSessionException(409, $"Adapter busy — session {_sessionId} holds the radio");

        try {
            if (_device != null)
                throw new ObdSessionException(409, $"Adapter already held by session {_sessionId}");

            var sw = Stopwatch.StartNew();
            IReadOnlyList<(string Address, string Name, bool IsLikelyObd)> found;
            try {
                found = await BleObdTransport.ScanForDevicesAsync(duration, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObdSessionException) {
                // No radio, BlueZ down, D-Bus unreachable. Same class as "can't reach the
                // adapter", so the caller gets the same 503 rather than a 500.
                _logger.LogInformation("Scan failed: {Message}", ex.Message);
                throw new ObdSessionException(503, "Could not scan — no usable Bluetooth adapter", ex);
            }

            _logger.LogInformation("Scan found {Count} device(s) in {Elapsed:F1}s", found.Count, sw.Elapsed.TotalSeconds);

            return new ScanResponse {
                Devices = found.Select(d => new ScanDevice {
                    Address = d.Address, Name = d.Name, IsLikelyObd = d.IsLikelyObd,
                }).ToList(),
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        finally {
            _gate.Release();
        }
    }

    /// <summary>Serial ports on this box. Cheap and stateless, so it doesn't touch the gate.</summary>
    public static PortsResponse GetPorts()
        => new() { Ports = System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p).ToList() };

    public async Task<ConnectResponse> ConnectAsync(ConnectRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try {
            if (_device != null)
                throw new ObdSessionException(409, $"Adapter already held by session {_sessionId}");

            IObdTransport transport;
            string target;

            if (string.Equals(request.Transport, "ble", StringComparison.OrdinalIgnoreCase)) {
                if (string.IsNullOrWhiteSpace(request.Address))
                    throw new ObdSessionException(400, "BLE transport requires 'address'");

                target = request.Address;
                transport = new BleObdTransport(request.Address, _loggerFactory.CreateLogger<BleObdTransport>());
            }
            else if (string.Equals(request.Transport, "serial", StringComparison.OrdinalIgnoreCase)) {
                if (string.IsNullOrWhiteSpace(request.Port))
                    throw new ObdSessionException(400, "Serial transport requires 'port'");

                target = request.Port;
                transport = new SerialObdTransport(request.Port, request.Baud, 5000, 5000);
            }
            else if (string.Equals(request.Transport, "fake", StringComparison.OrdinalIgnoreCase)) {
                target = request.Address ?? "(built-in)";
                transport = new FakeObdTransport(LoadCannedResponses(request.Address),
                    _loggerFactory.CreateLogger<FakeObdTransport>());
            }
            else {
                throw new ObdSessionException(400, $"Unknown transport '{request.Transport}' (expected 'serial', 'ble', or 'fake')");
            }

            var device = new ObdDevice(transport, _loggerFactory.CreateLogger<ObdDevice>());

            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(request.ConnectTimeoutSeconds));
                await device.ConnectAsync(timeout.Token);
            }
            catch (Exception ex) {
                await SafeDisposeAsync(device);

                // The expected failure when the car is asleep or out of range. 503 rather
                // than 500 so the caller can tell "no adapter" from "daemon is broken".
                _logger.LogInformation("Connect to {Target} failed: {Message}", target, ex.Message);
                throw new ObdSessionException(503, $"Could not reach adapter at {target}", ex);
            }

            _device = device;
            _transport = request.Transport.ToLowerInvariant();
            _target = target;
            _sessionId = Guid.NewGuid().ToString("n");
            _connectedAt = DateTimeOffset.UtcNow;
            _lastActivity = _connectedAt;

            _logger.LogInformation("Session {Session} opened on {Transport} {Target}", _sessionId, _transport, target);
            return new ConnectResponse { SessionId = _sessionId, Target = target };
        }
        finally {
            _gate.Release();
        }
    }

    public async Task<CommandResponse> SendCommandAsync(string sessionId, CommandRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            throw new ObdSessionException(400, "'command' is required");

        await _gate.WaitAsync(ct);
        try {
            var device = RequireSession(sessionId);

            var sw = Stopwatch.StartNew();
            string response;
            try {
                response = await device.SendCommandAsync(request.Command, request.WriteDelayMs, request.ReadTimeoutMs, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                // A transport-level throw means the link is gone; the session can't be
                // trusted any more, so tear it down rather than leave a zombie holding
                // the adapter until the idle reaper notices.
                _logger.LogWarning(ex, "Command '{Command}' failed, dropping session {Session}", request.Command, sessionId);
                await TeardownAsync();
                throw new ObdSessionException(503, "Adapter link failed, session closed", ex);
            }

            _lastActivity = DateTimeOffset.UtcNow;
            return new CommandResponse { Response = response, ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }
        finally {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(string sessionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try {
            RequireSession(sessionId);
            _logger.LogInformation("Session {Session} closed by client", sessionId);
            await TeardownAsync();
        }
        finally {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops a session that has gone quiet. Without this a client that crashes mid-session
    /// would hold the adapter until the daemon restarts.
    /// </summary>
    public async Task ReapIfIdleAsync()
    {
        if (_device == null) return;
        if (DateTimeOffset.UtcNow - _lastActivity < _idleTimeout) return;

        if (!await _gate.WaitAsync(TimeSpan.Zero)) return; // busy — it isn't idle after all
        try {
            if (_device == null) return;
            if (DateTimeOffset.UtcNow - _lastActivity < _idleTimeout) return;

            _logger.LogWarning("Session {Session} idle for over {Timeout}, reaping", _sessionId, _idleTimeout);
            await TeardownAsync();
        }
        finally {
            _gate.Release();
        }
    }

    private static Dictionary<string, string>? LoadCannedResponses(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (!File.Exists(path))
            throw new ObdSessionException(400, $"Canned response file not found: {path}");

        try {
            return System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), DaemonJsonContext.Default.DictionaryStringString);
        }
        catch (System.Text.Json.JsonException ex) {
            throw new ObdSessionException(400, $"Could not parse canned response file: {path}", ex);
        }
    }

    private ObdDevice RequireSession(string sessionId)
    {
        if (_device == null)
            throw new ObdSessionException(409, "No active session — connect first");

        if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
            throw new ObdSessionException(409, "Session id does not hold the adapter");

        return _device;
    }

    /// <summary>Caller must hold the gate.</summary>
    private async Task TeardownAsync()
    {
        var device = _device;
        _device = null;
        _sessionId = null;
        _transport = null;
        _target = null;

        if (device != null)
            await SafeDisposeAsync(device);
    }

    private async Task SafeDisposeAsync(ObdDevice device)
    {
        try { await device.DisconnectAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error during adapter disconnect"); }

        try { await device.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error during adapter dispose"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await TeardownAsync(); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}
