public interface IObdTransport : IAsyncDisposable, IDisposable
{
    Task OpenAsync(CancellationToken ct);
    Task CloseAsync();
    Task WriteAsync(string command, CancellationToken ct);
    Task<char?> ReadCharAsync(CancellationToken ct);
    void DiscardBuffers();
    bool IsOpen { get; }
}
