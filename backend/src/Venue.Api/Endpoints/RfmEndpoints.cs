using System.Collections.Concurrent;
using System.Numerics;
using Venue.Domain;
using Venue.Infrastructure;
using static Venue.Api.Endpoints.EndpointHelpers;

namespace Venue.Api.Endpoints;

/// <summary>RFM endpoints (PLAN_BACKEND §5): request lifecycle, sealed-quote commit/reveal.</summary>
public static class RfmEndpoints
{
    private static readonly ConcurrentDictionary<(BigInteger RequestId, string User), BigInteger> SaltStore = new();

    public static RouteGroupBuilder MapRfmEndpoints(this IEndpointRouteBuilder app, SessionStore sessions, AppConfig cfg, VenueCore core, Venue.Chain.IChainGateway gateway)
    {
        var g = app.MapGroup("/v1/rfm").WithTags("rfm");

        g.MapGet("/requests", async () => Results.Ok((await core.GetRfmRequestsAsync()).Select(RfmView)));

        g.MapGet("/requests/{id}", async (BigInteger id) =>
        {
            var r = await core.GetRfmRequestAsync(id);
            return r == null ? Results.NotFound() : Results.Ok(RfmView(r));
        });

        g.MapPost("/requests", async (PostRequestReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var side = Enum.TryParse<RfmSide>(req.Side, true, out var s) ? s : RfmSide.Yes;
            var quantity = Amount(req.Quantity);
            var maxTick = req.MaxPriceTick ?? 700;
            var minMatch = Amount(req.MinMatch);
            if (quantity <= 0 || minMatch <= 0 || minMatch > quantity) return Results.BadRequest(new { error = "quantity/minMatch invalid" });

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var commitDeadline = now + cfg.CommitSeconds;
            var revealDeadline = commitDeadline + cfg.RevealSeconds;
            var marketId = Hash.KeccakHex(req.Market ?? "rfm-market");

            return await Submit(() => gateway.SubmitPostRequestAsync(user, marketId, side, quantity, maxTick, minMatch, commitDeadline, revealDeadline, CancellationToken.None));
        });

        g.MapPost("/requests/{id}/cancel", async (BigInteger id, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            return await Submit(() => gateway.SubmitCancelRequestAsync(user, id, CancellationToken.None));
        });

        g.MapPost("/commit", async (CommitReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var tick = req.PriceTick ?? 0;
            var size = Amount(req.Size);
            if (tick <= 0 || tick >= 1000 || size <= 0) return Results.BadRequest(new { error = "tick/size invalid" });
            var salt = ParseSalt(req.Salt) ?? RandomSalt();
            SaltStore[(req.RequestId, user)] = salt;
            var commitHash = Hash.QuoteHash(cfg.Chain.ChainId, cfg.Chain.NormalizedRfm, req.RequestId, user, tick, size, salt);
            return await Submit(() => gateway.SubmitCommitQuoteAsync(user, req.RequestId, commitHash, CancellationToken.None));
        });

        g.MapPost("/reveal", async (RevealReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var tick = req.PriceTick ?? 0;
            var size = Amount(req.Size);
            var salt = ParseSalt(req.Salt)
                ?? (SaltStore.TryGetValue((req.RequestId, user), out var stored) ? stored : BigInteger.Zero);
            return await Submit(() => gateway.SubmitRevealQuoteAsync(user, req.RequestId, tick, size, salt, CancellationToken.None));
        });

        return g;
    }

    private static object RfmView(RfmRequestMirror r)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() < 0 ? BigInteger.Zero : new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return new
        {
            requestId = r.RequestId.ToString(),
            market = r.Market,
            side = r.Side.ToString().ToLowerInvariant(),
            quantity = r.Quantity.ToString(),
            maxPriceTick = r.MaxPriceTick.ToString(),
            minMatch = r.MinMatch.ToString(),
            commitDeadline = r.CommitDeadline.ToString(),
            revealDeadline = r.RevealDeadline.ToString(),
            escrowAmount = r.EscrowAmount.ToString(),
            minQuoteSize = r.MinQuoteSize.ToString(),
            commitCount = r.CommitCount.ToString(),
            phase = r.PhaseAt(now).ToString().ToLowerInvariant(),
            born = r.MarketId == null ? null : new { marketId = r.MarketId, marginalYesTick = r.BornMarginalYesTick, vwapYesTick = r.BornVwapYesTick, filled = r.BornFilledQuantity?.ToString() },
            reveals = r.Reveals.Select(v => new { mm = v.Mm, tick = v.Tick.ToString(), size = v.Size.ToString(), inRange = v.InRange }).ToList(),
            fills = r.Fills.Select(f => new { mm = f.Mm, tick = f.Tick.ToString(), size = f.Size.ToString() }).ToList(),
        };
    }

    private static BigInteger? ParseSalt(string? salt)
        => string.IsNullOrWhiteSpace(salt) ? null : BigInteger.TryParse(salt, out var v) ? v : null;

    private static BigInteger RandomSalt()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    public sealed record PostRequestReq(string? Market, string? Side, string? Quantity, long? MaxPriceTick, string? MinMatch);
    public sealed record CommitReq(BigInteger RequestId, long? PriceTick, string? Size, string? Salt);
    public sealed record RevealReq(BigInteger RequestId, long? PriceTick, string? Size, string? Salt);
}
