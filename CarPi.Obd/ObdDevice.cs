using System.Text;
using Microsoft.Extensions.Logging;

public class ObdDevice : IObdSession
{
    private readonly IObdTransport _transport;
    private readonly ILogger _logger;

    public ObdDevice(IObdTransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _transport.OpenAsync(ct);
        _transport.DiscardBuffers();

        // Reset adapter
        var atzResp = await SendCommandAsync("ATZ", ct);
        _logger.LogTrace("ATZ: {Response}", atzResp);
        await Task.Delay(1000, ct);

        // Disable echo, linefeeds, spaces — reduces BLE traffic significantly.
        // ATS0 alone cuts ~33% of bytes for large responses.
        var echo = await SendCommandAsync("ATE0", ct);
        _logger.LogTrace("ATE0: {Response}", echo);
        if (!echo.Contains("OK"))
            _logger.LogWarning("AT echo disable failed, continuing...");

        _logger.LogTrace("ATL0: {Response}", await SendCommandAsync("ATL0", ct));
        _logger.LogTrace("ATS0: {Response}", await SendCommandAsync("ATS0", ct));

        // TODO: Make protocol configurable (e.g., via --protocol or appsettings).
        // ATSP6 = ISO 15765-4 CAN 500kbps / 11-bit — correct for Chevy Bolt and most
        // modern GM vehicles. Hardcoding skips the slow auto-detect search (ATSP0 + 0100)
        // which can take 10-15+ seconds over BLE and is unreliable with tight timeouts.
        // Other protocols: 7 = CAN 500k/29-bit, 8 = CAN 250k/11-bit.
        _logger.LogTrace("ATSP6: {Response}", await SendCommandAsync("ATSP6", ct));

        // ATSTFF: Max timeout (255 * 5ms = 1275ms). Cheap clones can timeout
        // mid-response with the default 75ms when multi-frame ISO-TP takes longer.
        _logger.LogTrace("ATSTFF: {Response}", await SendCommandAsync("ATSTFF", ct));

        // NOTE: ATAL (Allow Long) was tried but causes this clone to output raw
        // ISO-TP frames with sequence numbers instead of reassembled data, making
        // frame drops more likely. Omitting it lets the adapter reassemble internally.

        // Enable ISO-TP flow control. ATCFC1 lets the adapter manage FC frames.
        _logger.LogTrace("ATCFC1: {Response}", await SendCommandAsync("ATCFC1", ct));

        _logger.LogInformation("ELM327 initialized ({Adapter})", atzResp.Trim());
    }

    public async Task ConnectAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await ConnectAsync(cts.Token);
    }

    public async Task DisconnectAsync()
    {
        if (_transport.IsOpen)
        {
            try
            {
                await SendCommandAsync("ATPC", CancellationToken.None);
            }
            catch { }

            await _transport.CloseAsync();
        }
    }

    public Task<string> SendCommandAsync(string command, CancellationToken ct)
        => SendCommandAsync(command, 0, 5000, ct);

    public async Task<string> SendCommandAsync(string command, int writeDelayMs, int readTimeoutMs, CancellationToken ct)
    {
        _transport.DiscardBuffers();
        await _transport.WriteAsync(command, ct);
        _logger.LogTrace("Sent: {Command}", command);

        if (writeDelayMs > 0)
            await Task.Delay(writeDelayMs, ct);

        var responses = new List<string>();
        var buffer = new StringBuilder();
        bool receivedAnyData = false;

        using var timeout = new CancellationTokenSource(readTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            var ch = await _transport.ReadCharAsync(linked.Token);
            if (ch == null)
                break;

            if (ch == '>')
            {
                if (!receivedAnyData)
                {
                    // Stale '>' from previous command arrived late over BLE.
                    // Skip it and keep reading for the real response.
                    _logger.LogTrace("Skipped stale '>' for {Command}", command);
                    continue;
                }

                var remaining = buffer.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining) && !remaining.StartsWith(command))
                    responses.Add(remaining);

                // Cheap ELM327 clones can emit a premature '>' mid-response
                // when their internal buffer overflows. Peek briefly for more data.
                using var peekTimeout = new CancellationTokenSource(250);
                using var peekLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, peekTimeout.Token);
                var next = await _transport.ReadCharAsync(peekLinked.Token);
                if (next == null || next == '>')
                    break; // Real end-of-response

                // More data after '>': premature prompt, keep reading.
                _logger.LogTrace("Premature '>' detected for {Command}, continuing read", command);
                buffer.Append(next);
                receivedAnyData = true;
                continue;
            }

            if (ch == '\r' || ch == '\n')
            {
                var line = buffer.ToString().Trim();
                buffer.Clear();

                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith(command)) continue;

                responses.Add(line);
                receivedAnyData = true;
            }
            else
            {
                buffer.Append(ch);
                receivedAnyData = true;
            }
        }

        var result = string.Join("\n", responses);
        _logger.LogTrace("Response for {Command}: {Length} chars, {Lines} lines",
            command, result.Length, responses.Count);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
    }
}
