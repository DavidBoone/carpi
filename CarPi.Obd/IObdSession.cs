/// <summary>
/// A connected ELM327 session at command granularity: send a command string, get the
/// adapter's response back. Deliberately coarser than <see cref="IObdTransport"/>, which
/// is char-at-a-time and only sane over a local link — this is the boundary that can be
/// put on a network, one round trip per command rather than per character.
/// Implemented locally by <see cref="ObdDevice"/> and remotely by CarPi.Client.
/// </summary>
public interface IObdSession : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    Task<string> SendCommandAsync(string command, CancellationToken ct);
    Task<string> SendCommandAsync(string command, int writeDelayMs, int readTimeoutMs, CancellationToken ct);
}
