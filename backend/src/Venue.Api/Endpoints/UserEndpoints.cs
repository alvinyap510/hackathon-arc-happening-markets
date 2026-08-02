using Venue.Circle;
using static Venue.Api.Endpoints.EndpointHelpers;

namespace Venue.Api.Endpoints;

/// <summary>Session / wallet / bridge / funds endpoints (PLAN_BACKEND §5).</summary>
public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app, SessionStore sessions, AppConfig cfg, ICircleServices circle, VenueCore core, Venue.Chain.IChainGateway gateway)
    {
        var g = app.MapGroup("/v1").WithTags("user");

        g.MapPost("/session", async (BindSessionReq req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Ref)) return Results.BadRequest(new { error = "ref required" });
            var session = await circle.BindSessionAsync(req.Ref, CancellationToken.None);
            var token = sessions.Create(session.Address);
            return Results.Ok(new { token, address = session.Address, gasless = circle.GaslessSupported });
        });

        g.MapGet("/wallet", (HttpRequest req) =>
        {
            var user = UserOf(req, sessions, cfg);
            return user == null ? Results.Unauthorized() : Results.Ok(new { address = user });
        });

        g.MapPost("/bridge/cctp", async (BridgeReq req) =>
        {
            var id = await circle.InitiateBridgeAsync(req.Amount ?? "0", CancellationToken.None);
            return Results.Ok(new { bridgeId = id });
        });

        g.MapGet("/bridge/{id}/status", async (string id) =>
        {
            var status = await circle.BridgeStatusAsync(id, CancellationToken.None);
            return Results.Ok(status);
        });

        g.MapGet("/balances", async (HttpRequest req) =>
        {
            var user = UserOf(req, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            return Results.Ok(await core.GetBalancesAsync(user));
        });

        g.MapPost("/vault/deposit", async (AmountReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            return await Submit(() => gateway.SubmitDepositAsync(user, Amount(req.Amount), CancellationToken.None));
        });

        g.MapPost("/vault/withdraw", async (AmountReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            return await Submit(() => gateway.SubmitWithdrawAsync(user, Amount(req.Amount), CancellationToken.None));
        });

        g.MapPost("/vault/tokens/deposit", async (TokenAmountReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            return await Submit(() => gateway.SubmitDepositTokensAsync(user, req.TokenId ?? "", Amount(req.Amount), CancellationToken.None));
        });

        g.MapPost("/vault/tokens/withdraw", async (TokenAmountReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            return await Submit(() => gateway.SubmitWithdrawTokensAsync(user, req.TokenId ?? "", Amount(req.Amount), CancellationToken.None));
        });

        g.MapGet("/tx/{hash}/status", async (string hash) =>
            Results.Ok(new { txHash = hash, status = (await gateway.TxStatusAsync(hash, CancellationToken.None)).ToString().ToLowerInvariant() }));

        return g;
    }

    public sealed record BindSessionReq(string? Ref);
    public sealed record BridgeReq(string? Amount);
    public sealed record AmountReq(string? Amount);
    public sealed record TokenAmountReq(string? TokenId, string? Amount);
}
