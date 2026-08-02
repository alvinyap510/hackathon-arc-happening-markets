using Venue;
using Venue.Api;
using Venue.Api.Endpoints;
using Venue.Api.Ws;
using Venue.Broadcasting;
using Venue.Chain;
using Venue.Circle;
using Venue.Domain;
using Venue.Rfm;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var appCfg = AppConfig.Load(builder.Configuration);

// Dev-mode signable accounts (SEAM 1): email login provisions a backend-held keypair.
// The gateway's user-key resolver checks the configured DemoUsers (the /session/bind
// bridge) first, then dev-provisioned accounts.
var devAccounts = new DevAccountStore(appCfg.SaltSecret);
Func<string, string?> resolveKey = address =>
    appCfg.DemoUserKeys.TryGetValue(Addresses.Normalize(address), out var key)
        ? key
        : devAccounts.KeyForAddress(Addresses.Normalize(address));

// --- chain seam: three swappable wallet providers (Venue:WalletProvider) ---
//   circle    : user actions via Circle dev-controlled SCA contract executions (Gas Station)
//   nethereum : user actions via backend-held demo keys (Arc RPC)
//   simulated : in-memory demo (no RPC)
IChainGateway gateway;
ISessionProvisioner sessionProvisioner;
ICircleServices circle;

if (appCfg.WalletProvider == "circle")
{
    if (string.IsNullOrWhiteSpace(appCfg.CircleApiKey) || string.IsNullOrWhiteSpace(appCfg.CircleEntitySecret) || string.IsNullOrWhiteSpace(appCfg.CircleWalletSetId))
        throw new InvalidOperationException("WalletProvider=circle requires Venue:Circle:ApiKey, Venue:Circle:EntitySecret and Venue:Circle:WalletSetId");
    var circleClient = new CircleW3sClient(appCfg.CircleApiKey!, appCfg.CircleEntitySecret!, appCfg.CircleWalletSetId!, appCfg.CircleBaseUrl);
    var walletStore = new CircleWalletStore(appCfg.CircleWalletStorePath);
    var circleGateway = new CircleChainGateway(appCfg.Chain, appCfg.OperatorPrivateKey, circleClient, walletStore);
    gateway = circleGateway;
    sessionProvisioner = circleGateway;
    circle = new CircleServicesMock(); // bridge seam untouched in circle mode
}
else if (appCfg.WalletProvider == "nethereum")
{
    var nethereumGateway = new NethereumChainGateway(appCfg.Chain, appCfg.OperatorPrivateKey, resolveKey);
    gateway = nethereumGateway;
    sessionProvisioner = devAccounts;
    circle = string.IsNullOrWhiteSpace(appCfg.CircleApiKey)
        ? new CircleServicesMock()
        : new CircleServicesHttp(appCfg.CircleApiKey, appCfg.CircleEntitySecretCiphertext ?? "", appCfg.CircleBaseUrl);
}
else
{
    // "simulated"
    gateway = new SimulatedChainGateway(appCfg.Chain);
    sessionProvisioner = devAccounts;
    circle = new CircleServicesMock();
}
devAccounts.AttachGateway(gateway);

// --- venue core + WS hub (circular: hub needs core, core needs the sink) ---
var core = new VenueCore(appCfg.Chain, gateway, new NullEventSink());
var hub = new WsHub(core);
core.SetSink(hub);

var sessions = new SessionStore();
var salts = new SaltService(appCfg.SaltSecret);
// Restart-durable RFM market metadata (INTEGRATION_CONTRACT G1), keyed by marketHash.
var marketMetadata = new MarketMetadataStore(appCfg.MetadataStorePath);

builder.Services.AddSingleton(appCfg);
builder.Services.AddSingleton(gateway);
builder.Services.AddSingleton(circle);
builder.Services.AddSingleton(core);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton(sessions);
builder.Services.AddSingleton(marketMetadata);
builder.Services.AddSingleton(devAccounts);
builder.Services.AddHostedService(_ => new CoreHostedService(core));
builder.Services.AddHostedService(_ => new MarketSeederHostedService(core, gateway, marketMetadata, appCfg));

// Money amounts travel as decimal strings everywhere in the API.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new BigIntegerJsonConverter()));

var app = builder.Build();
app.UseWebSockets();

app.MapUserEndpoints(sessions, appCfg, circle, core, gateway, sessionProvisioner);
app.MapTradingEndpoints(sessions, appCfg, core, gateway, marketMetadata);
app.MapRfmEndpoints(sessions, appCfg, core, gateway, salts, marketMetadata);
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
