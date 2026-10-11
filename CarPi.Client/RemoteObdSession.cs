using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace CarPi.Client;

public class RemoteObdException : Exception
{
    public HttpStatusCode StatusCode { get; }

    /// <summary>True when the daemon reached for the adapter and it wasn't there — the
    /// normal outcome with the car asleep, not a fault worth alarming about.</summary>
    public bool AdapterUnavailable => StatusCode == HttpStatusCode.ServiceUnavailable;

    public RemoteObdException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// An <see cref="IObdSession"/> backed by a CarPi.Daemon over HTTP. Drop-in for
/// <c>ObdDevice</c>, so <c>BoltPidQuery</c> and <c>DidScanner</c> run unchanged against a
/// car that's somewhere else — all decoding stays on this side of the link.
/// </summary>
public class RemoteObdSession : IObdSession
{
    private readonly HttpClient _http;
    private readonly ConnectRequest _request;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;
    private string? _sessionId;

    public RemoteObdSession(Uri daemonUri, ConnectRequest request, ILogger logger)
        : this(new HttpClient { BaseAddress = daemonUri }, request, logger, ownsHttpClient: true)
    {
    }

    public RemoteObdSession(HttpClient http, ConnectRequest request, ILogger logger, bool ownsHttpClient = false)
    {
        _http = http;
        _request = request;
        _logger = logger;
        _ownsHttpClient = ownsHttpClient;

        // A DID sweep can sit on one command for a while; don't let the HTTP layer be the
        // thing that gives up first.
        if (_http.Timeout < TimeSpan.FromMinutes(2))
            _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public string? SessionId => _sessionId;

    /// <summary>
    /// Asks a daemon what its radio can see. Static because there's no session yet — this is
    /// how a caller gets the address it will later connect to.
    /// </summary>
    public static async Task<ScanResponse> ScanAsync(Uri daemonUri, TimeSpan duration, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = daemonUri, Timeout = duration + TimeSpan.FromSeconds(30) };
        var response = await http.GetAsync($"/scan?seconds={(int)duration.TotalSeconds}", ct);
        await ThrowIfErrorAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ScanResponse>(ct))!;
    }

    /// <summary>Serial ports on the daemon's box.</summary>
    public static async Task<PortsResponse> GetPortsAsync(Uri daemonUri, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = daemonUri };
        var response = await http.GetAsync("/ports", ct);
        await ThrowIfErrorAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PortsResponse>(ct))!;
    }

    public async Task<StatusResponse> GetStatusAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("/status", ct);
        await ThrowIfErrorAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StatusResponse>(ct))!;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("/connect", _request, ct);
        await ThrowIfErrorAsync(response, ct);

        var connected = (await response.Content.ReadFromJsonAsync<ConnectResponse>(ct))!;
        _sessionId = connected.SessionId;
        _logger.LogInformation("Remote session {Session} on {Target}", _sessionId, connected.Target);
    }

    public Task<string> SendCommandAsync(string command, CancellationToken ct)
        => SendCommandAsync(command, 0, 5000, ct);

    public async Task<string> SendCommandAsync(string command, int writeDelayMs, int readTimeoutMs, CancellationToken ct)
    {
        if (_sessionId == null)
            throw new InvalidOperationException("Not connected — call ConnectAsync first");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/command") {
            Content = JsonContent.Create(new CommandRequest {
                Command = command,
                WriteDelayMs = writeDelayMs,
                ReadTimeoutMs = readTimeoutMs,
            }),
        };
        request.Headers.Add(ObdProtocol.SessionHeader, _sessionId);

        var response = await _http.SendAsync(request, ct);
        await ThrowIfErrorAsync(response, ct);

        var result = (await response.Content.ReadFromJsonAsync<CommandResponse>(ct))!;
        _logger.LogTrace("{Command} -> {Length} chars in {Elapsed:F0}ms",
            command, result.Response.Length, result.ElapsedMs);
        return result.Response;
    }

    public async Task DisconnectAsync()
    {
        if (_sessionId == null) return;

        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/disconnect");
            request.Headers.Add(ObdProtocol.SessionHeader, _sessionId);
            await _http.SendAsync(request);
        }
        catch (Exception ex) {
            // Best effort. If this doesn't land, the daemon's idle reaper cleans up.
            _logger.LogDebug(ex, "Remote disconnect failed");
        }

        _sessionId = null;
    }

    private static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string message;
        try {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(ct);
            message = error?.Error ?? response.ReasonPhrase ?? "Request failed";
            if (!string.IsNullOrEmpty(error?.Detail))
                message += $" ({error.Detail})";
        }
        catch {
            message = response.ReasonPhrase ?? "Request failed";
        }

        throw new RemoteObdException(response.StatusCode, message);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
