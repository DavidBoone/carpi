using CarPi.Daemon;

var builder = WebApplication.CreateBuilder(args);

// Under systemd this switches to Type=notify readiness and journal-formatted logging;
// run from a terminal it's a no-op.
builder.Host.UseSystemd();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => {
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// Bind all interfaces by default — the whole point is remote access. Override with
// CARPI_URLS or --urls if it should be narrower.
var urls = builder.Configuration["Urls"] ?? "http://0.0.0.0:5252";
builder.WebHost.UseUrls(urls);

var idleTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("IdleTimeoutSeconds", 300));

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, DaemonJsonContext.Default));

builder.Services.AddSingleton(sp => new ObdSessionManager(
    sp.GetRequiredService<ILogger<ObdSessionManager>>(),
    sp.GetRequiredService<ILoggerFactory>(),
    idleTimeout));
builder.Services.AddHostedService<IdleSessionReaper>();

var app = builder.Build();

// Serve wwwroot/ if it exists, so a browser page can use these endpoints same-origin
// and no CORS hole has to be opened in something that talks to a car. Deliberately the
// only web concern here: the daemon ships no page of its own, decodes nothing for one,
// and doesn't care what's in the directory. See projects/carpi-web.
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot"))) {
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// No auth by design — the trust boundary is the tailnet/LAN. Do not expose this to the
// open internet: it will send arbitrary commands to a vehicle.
app.MapGet("/status", (ObdSessionManager sessions) => Results.Ok(sessions.GetStatus()));

// Discovery has to run where the radio is. Read-only and session-less by nature — this is
// what a caller hits before it has an address to connect to.
app.MapGet("/scan", async (ObdSessionManager sessions, CancellationToken ct, int seconds = 10) =>
    await Run(() => sessions.ScanAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60)), ct)));

app.MapGet("/ports", () => Results.Ok(ObdSessionManager.GetPorts()));

app.MapPost("/connect", async (ConnectRequest request, ObdSessionManager sessions, CancellationToken ct) =>
    await Run(() => sessions.ConnectAsync(request, ct)));

app.MapPost("/command", async (CommandRequest request, HttpContext http, ObdSessionManager sessions, CancellationToken ct) =>
    await Run(() => sessions.SendCommandAsync(SessionId(http), request, ct)));

app.MapPost("/disconnect", async (HttpContext http, ObdSessionManager sessions, CancellationToken ct) =>
    await Run(async () => {
        await sessions.DisconnectAsync(SessionId(http), ct);
        return new OkResponse();
    }));

app.Run();

static string SessionId(HttpContext http)
    => http.Request.Headers[ObdProtocol.SessionHeader].ToString();

static async Task<IResult> Run<T>(Func<Task<T>> action)
{
    try {
        return Results.Ok(await action());
    }
    catch (ObdSessionException ex) {
        return Results.Json(
            new ErrorResponse { Error = ex.Message, Detail = ex.InnerException?.Message },
            DaemonJsonContext.Default.ErrorResponse,
            statusCode: ex.StatusCode);
    }
    catch (OperationCanceledException) {
        return Results.StatusCode(499);
    }
}

namespace CarPi.Daemon
{
    public class IdleSessionReaper(ObdSessionManager sessions) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(ct))
                await sessions.ReapIfIdleAsync();
        }
    }
}
