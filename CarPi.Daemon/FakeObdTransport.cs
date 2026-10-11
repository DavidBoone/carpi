using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace CarPi.Daemon;

/// <summary>
/// A canned ELM327 that answers AT commands and returns NO DATA for everything else.
/// Selected with <c>"transport": "fake"</c> so the daemon, the client, and anything built
/// on top can be exercised with no adapter and no car — the vehicle is asleep most of the
/// time, and a pty is not an option here (System.IO.Ports needs a real serial device).
///
/// Responses can be scripted by pointing <c>address</c> at a JSON file of
/// <c>{"COMMAND": "response"}</c>, matched case-insensitively.
/// </summary>
public class FakeObdTransport : IObdTransport
{
    private readonly Dictionary<string, string> _canned;
    private readonly ILogger _logger;
    private readonly Channel<char> _incoming = Channel.CreateUnbounded<char>();
    private bool _open;

    public FakeObdTransport(Dictionary<string, string>? canned, ILogger logger)
    {
        _canned = new Dictionary<string, string>(canned ?? new(), StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public bool IsOpen => _open;

    public Task OpenAsync(CancellationToken ct)
    {
        _open = true;
        _logger.LogInformation("Fake adapter open ({Count} canned responses)", _canned.Count);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        _open = false;
        return Task.CompletedTask;
    }

    public Task WriteAsync(string command, CancellationToken ct)
    {
        var cmd = command.Trim();
        var response = _canned.TryGetValue(cmd, out var canned) ? canned
            : cmd.Equals("ATZ", StringComparison.OrdinalIgnoreCase) ? "ELM327 v1.5 (fake)"
            : cmd.StartsWith("AT", StringComparison.OrdinalIgnoreCase) ? "OK"
            : "NO DATA";

        foreach (var ch in response + "\r\r>")
            _incoming.Writer.TryWrite(ch);

        return Task.CompletedTask;
    }

    public async Task<char?> ReadCharAsync(CancellationToken ct)
    {
        try {
            return await _incoming.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException) {
            return null;
        }
    }

    public void DiscardBuffers()
    {
        while (_incoming.Reader.TryRead(out _)) { }
    }

    public void Dispose() => _open = false;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
