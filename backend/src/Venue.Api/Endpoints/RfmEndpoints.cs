using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;
using Venue.Rfm;
using static Venue.Api.Endpoints.EndpointHelpers;

namespace Venue.Api.Endpoints;

/// <summary>RFM endpoints (PLAN_BACKEND §5 + INTEGRATION_CONTRACT G1/G6): request lifecycle,
/// sealed-quote commit/reveal, restart-durable market metadata keyed by marketHash.</summary>
public static class RfmEndpoints
{
    public static RouteGroupBuilder MapRfmEndpoints(this IEndpointRouteBuilder app, SessionStore sessions, AppConfig cfg, VenueCore core, Venue.Chain.IChainGateway gateway, SaltService salts, MarketMetadataStore metadata)
    {
        var g = app.MapGroup("/v1/rfm").WithTags("rfm");

        g.MapGet("/requests", async () => Results.Ok((await core.GetRfmRequestsAsync()).Select(r => RfmView(r, metadata)).ToList()));

        g.MapGet("/requests/{id}", async (BigInteger id) =>
        {
            var r = await core.GetRfmRequestAsync(id);
            return r == null ? Results.NotFound() : Results.Ok(RfmView(r, metadata));
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

            // Optional requester-selected auction duration preset. Omitted/null keeps the
            // configured default windows (back-compat); a preset is split 2/3 commit, 1/3
            // reveal (reveal floored at 20s); any other value is rejected - never accept
            // raw client deadlines (that is what keeps the contract's 7-day cap unreachable).
            if (!TryResolveWindow(req.Duration, cfg.CommitSeconds, cfg.RevealSeconds, out var commitSec, out var revealSec))
                return Results.BadRequest(new { error = $"duration must be one of: 1m, 15m, 1h, 4h, 24h (got \"{req.Duration}\")" });

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var commitDeadline = now + commitSec;
            var revealDeadline = commitDeadline + revealSec;
            var marketHash = Hash.KeccakHex(req.Market ?? "rfm-market");

            // G1: the marketHash is the deterministic preimage the contract commits (the
            // bytes32 `market` on RequestPosted). Persist the off-chain text BEFORE the
            // on-chain tx so it survives even if the tx lands late. finalize carries no
            // metadata; the born market's text is served from this store by marketHash.
            metadata.Save(marketHash, req.QuestionText, req.ResolutionSource, req.CloseTime);

            // G6: return {requestId, txHash} - the requestId is read from the contract once
            // the post tx is mined (single authoritative source, not a client-supplied guess).
            return await SubmitPost(gateway, user, marketHash, side, quantity, maxTick, minMatch, commitDeadline, revealDeadline);
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
            // Deterministic server salt when the client does not supply one: the reveal endpoint
            // recomputes the SAME salt, so a restart between commit and reveal never strands the bond.
            var salt = ParseSalt(req.Salt) ?? salts.Derive(req.RequestId, user);
            var commitHash = Hash.QuoteHash(cfg.Chain.ChainId, cfg.Chain.NormalizedRfm, req.RequestId, user, tick, size, salt);
            return await Submit(() => gateway.SubmitCommitQuoteAsync(user, req.RequestId, commitHash, CancellationToken.None));
        });

        g.MapPost("/reveal", async (RevealReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var tick = req.PriceTick ?? 0;
            var size = Amount(req.Size);
            var salt = ParseSalt(req.Salt) ?? salts.Derive(req.RequestId, user);
            return await Submit(() => gateway.SubmitRevealQuoteAsync(user, req.RequestId, tick, size, salt, CancellationToken.None));
        });

        return g;
    }

    /// <summary>G6: submit the post, wait for it to mine, then read the authoritative
    /// requestCount from the contract. Returns {requestId, txHash}.</summary>
    private static async Task<IResult> SubmitPost(IChainGateway gateway, string user, string marketHash, RfmSide side,
        BigInteger quantity, BigInteger maxTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline)
    {
        try
        {
            var txHash = await gateway.SubmitPostRequestAsync(user, marketHash, side, quantity, maxTick, minMatch, commitDeadline, revealDeadline, CancellationToken.None);
            var status = await AwaitStatusAsync(gateway, txHash);
            if (status != TxStatus.Confirmed)
                return Results.BadRequest(new { error = $"postRequest tx {status} ({txHash})" });
            var requestId = await gateway.GetRequestCountAsync(CancellationToken.None);
            return Results.Ok(new { requestId = requestId.ToString(), txHash });
        }
        catch (SimulationRevertException ex)
        {
            return Results.BadRequest(new { error = ex.Reason });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<TxStatus> AwaitStatusAsync(IChainGateway gateway, string txHash)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await gateway.TxStatusAsync(txHash, CancellationToken.None);
            if (status is TxStatus.Confirmed or TxStatus.Reverted or TxStatus.Dropped) return status;
            await Task.Delay(500);
        }
        return TxStatus.Pending;
    }

    private static object RfmView(RfmRequestMirror r, MarketMetadataStore metadata)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() < 0 ? BigInteger.Zero : new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var meta = metadata.Get(r.Market);
        return new
        {
            requestId = r.RequestId.ToString(),
            market = r.Market,
            questionText = meta?.QuestionText,
            resolutionSource = meta?.ResolutionSource,
            closeTime = meta?.CloseTime?.ToString(),
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
            postedTxHash = r.PostedTxHash,
            born = r.MarketId == null ? null : new { marketId = r.MarketId, marginalYesTick = r.BornMarginalYesTick, vwapYesTick = r.BornVwapYesTick, filled = r.BornFilledQuantity?.ToString(), txHash = r.BornTxHash },
            reveals = r.Reveals.Select(v => new { mm = v.Mm, tick = v.Tick.ToString(), size = v.Size.ToString(), inRange = v.InRange }).ToList(),
            fills = r.Fills.Select(f => new { mm = f.Mm, tick = f.Tick.ToString(), size = f.Size.ToString() }).ToList(),
        };
    }

    private static BigInteger? ParseSalt(string? salt)
        => string.IsNullOrWhiteSpace(salt) ? null : BigInteger.TryParse(salt, out var v) ? v : null;

    /// <summary>
    /// Resolve the auction window. A PRESET duration (the total commit+reveal span) maps to
    /// commit = total - reveal with reveal = max(total/3, 20); null/empty falls back to the
    /// configured defaults (today's behaviour). Returns false only for an unknown preset,
    /// which the endpoint turns into HTTP 400. Only these presets are accepted.
    /// </summary>
    public static bool TryResolveWindow(string? duration, int defaultCommitSeconds, int defaultRevealSeconds, out int commitSeconds, out int revealSeconds)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            commitSeconds = defaultCommitSeconds;
            revealSeconds = defaultRevealSeconds;
            return true;
        }
        if (!DurationTotals.TryGetValue(duration, out var totalSeconds))
        {
            commitSeconds = 0;
            revealSeconds = 0;
            return false;
        }
        revealSeconds = Math.Max(totalSeconds / 3, 20);
        commitSeconds = totalSeconds - revealSeconds;
        return true;
    }

    private static readonly Dictionary<string, int> DurationTotals = new()
    {
        ["1m"] = 60,
        ["15m"] = 900,
        ["1h"] = 3600,
        ["4h"] = 14400,
        ["24h"] = 86400,
    };

    public sealed record PostRequestReq(string? Market, string? Side, string? Quantity, long? MaxPriceTick, string? MinMatch, string? QuestionText, string? ResolutionSource, long? CloseTime, string? Duration);
    public sealed record CommitReq(BigInteger RequestId, long? PriceTick, string? Size, string? Salt);
    public sealed record RevealReq(BigInteger RequestId, long? PriceTick, string? Size, string? Salt);
}
