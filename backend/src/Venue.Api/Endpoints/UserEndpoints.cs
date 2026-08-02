using Venue.Circle;
using Venue.Chain;
using static Venue.Api.Endpoints.EndpointHelpers;

namespace Venue.Api.Endpoints;

/// <summary>Session / wallet / bridge / funds endpoints (PLAN_BACKEND §5).</summary>
public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app, SessionStore sessions, AppConfig cfg, ICircleServices circle, VenueCore core, Venue.Chain.IChainGateway gateway, Venue.Chain.ISessionProvisioner sessionProvisioner)
    {
        var g = app.MapGroup("/v1").WithTags("user");

        g.MapPost("/session", async (BindSessionReq req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Ref)) return Results.BadRequest(new { error = "ref required" });
            // Session: provision a signable account for the email (DevAccountStore for
            // nethereum/simulated; a Circle SCA for circle mode) so user-signed txs work.
            var address = await sessionProvisioner.ProvisionAsync(req.Ref, CancellationToken.None);
            var token = sessions.Create(address);
            return Results.Ok(new { token, address, gasless = circle.GaslessSupported });
        });

        // Real-chain demo sessions: bind a session directly to an address the host holds a
        // dev-controlled key for (Venue:DemoUsers). The Circle mock derives non-signable
        // addresses from refs, which cannot drive real user ops; this path is the explicit
        // bridge for throwaway EOA keys on a local chain / dev-run.
        g.MapPost("/session/bind", async (BindAddressReq req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Address)) return Results.BadRequest(new { error = "address required" });
            var addr = Venue.Domain.Addresses.Normalize(req.Address);
            if (!cfg.DemoUserKeys.ContainsKey(addr)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var token = sessions.Create(addr);
            return Results.Ok(new { token, address = addr, gasless = circle.GaslessSupported });
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
            var b = await core.GetBalancesAsync(user);
            // G4: `wallet` = on-chain MockUSDC collateral balance (outside the Vault).
            var wallet = await gateway.GetUsdcWalletBalanceAsync(user, CancellationToken.None);
            return Results.Ok(new
            {
                user = b.User,
                wallet = wallet.ToString(),
                chainFree = b.ChainFree.ToString(),
                reserved = b.Reserved.ToString(),
                available = b.Available.ToString(),
                positions = b.Positions.Select(p => new { tokenId = p.TokenId, marketId = p.MarketId, outcome = p.Outcome.ToString().ToLowerInvariant(), amount = p.Amount.ToString() }).ToList(),
            });
        });

        // G4 faucet: mint the self-deployed collateral MockUSDC to the caller's wallet.
        // Demo/dev convenience - the venue collateral is our own mock, freely mintable.
        g.MapPost("/faucet", async (AmountReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var amt = Amount(req.Amount);
            if (amt <= 0) return Results.BadRequest(new { error = "amount required" });
            return await Submit(() => gateway.SubmitMintUsdcAsync(user, amt, CancellationToken.None));
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
    public sealed record BindAddressReq(string? Address);
    public sealed record BridgeReq(string? Amount);
    public sealed record AmountReq(string? Amount);
    public sealed record TokenAmountReq(string? TokenId, string? Amount);
}
