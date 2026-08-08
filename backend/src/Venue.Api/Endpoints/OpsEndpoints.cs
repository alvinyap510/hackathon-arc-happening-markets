namespace Venue.Api.Endpoints;

/// <summary>Ops endpoints: health + user resnapshot (replay + discard volatile state).</summary>
public static class OpsEndpoints
{
    public static RouteGroupBuilder MapOpsEndpoints(this IEndpointRouteBuilder app, AppConfig cfg, VenueCore core, Venue.Chain.IChainGateway gateway)
    {
        var g = app.MapGroup("/v1").WithTags("ops");

        g.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            simulate = gateway.Simulated,
            chainId = cfg.Chain.ChainId.ToString(),
            pendingSettlements = core.PendingSettlements,
            at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }));

        // The running backend build (git SHA), injected at deploy via Venue__Version. The Arc proof
        // driver requires this to be set so the evidence can cite a VERIFIED backend build.
        g.MapGet("/version", () => Results.Ok(new { commit = cfg.Version }));

        g.MapPost("/user/resnapshot", async () =>
        {
            await core.RestartAsync(CancellationToken.None);
            return Results.Ok(new { ok = true, note = "replayed from deploy block; volatile orders discarded" });
        });

        return g;
    }
}
