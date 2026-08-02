using Venue;
using Venue.Api;
using Venue.Api.Endpoints;
using Venue.Api.Ws;
using Venue.Broadcasting;
using Venue.Chain;
using Venue.Circle;
using Venue.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var appCfg = AppConfig.Load(builder.Configuration);

// --- chain seam: simulated (local demo) or Nethereum (Arc RPC) ---
IChainGateway gateway = appCfg.Simulate
    ? new SimulatedChainGateway(appCfg.Chain)
    : new NethereumChainGateway(appCfg.Chain, appCfg.OperatorPrivateKey,
        address => appCfg.DemoUserKeys.TryGetValue(Addresses.Normalize(address), out var key) ? key : null);

// --- Circle seam: mock unless credentials are configured ---
ICircleServices circle = string.IsNullOrWhiteSpace(appCfg.CircleApiKey)
    ? new CircleServicesMock()
    : new CircleServicesHttp(appCfg.CircleApiKey, appCfg.CircleEntitySecretCiphertext ?? "", appCfg.CircleBaseUrl);

// --- venue core + WS hub (circular: hub needs core, core needs the sink) ---
var core = new VenueCore(appCfg.Chain, gateway, new NullEventSink());
var hub = new WsHub(core);
core.SetSink(hub);

var sessions = new SessionStore();

builder.Services.AddSingleton(appCfg);
builder.Services.AddSingleton(gateway);
builder.Services.AddSingleton(circle);
builder.Services.AddSingleton(core);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton(sessions);
builder.Services.AddHostedService(_ => new CoreHostedService(core));

// Money amounts travel as decimal strings everywhere in the API.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new BigIntegerJsonConverter()));

var app = builder.Build();
app.UseWebSockets();

app.MapUserEndpoints(sessions, appCfg, circle, core, gateway);
app.MapTradingEndpoints(sessions, appCfg, core, gateway);
app.MapRfmEndpoints(sessions, appCfg, core, gateway);
app.MapOpsEndpoints(appCfg, core, gateway);

app.MapGet("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.AcceptAsync(ws, ctx.RequestAborted);
});

app.Run();

/// <summary>Starts/stops the venue core's background loops (indexer, batcher, RFM crank).</summary>
internal sealed class CoreHostedService(VenueCore core) : IHostedService
{
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return core.StartAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        await core.StopAsync();
    }
}
