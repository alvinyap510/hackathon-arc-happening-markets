using System.Numerics;
using Venue.Domain;
using static Venue.Api.Endpoints.EndpointHelpers;

namespace Venue.Api.Endpoints;

/// <summary>Trading + market lifecycle endpoints (PLAN_BACKEND §5).</summary>
public static class TradingEndpoints
{
    public static RouteGroupBuilder MapTradingEndpoints(this IEndpointRouteBuilder app, SessionStore sessions, AppConfig cfg, VenueCore core, Venue.Chain.IChainGateway gateway)
    {
        var g = app.MapGroup("/v1").WithTags("trading");

        g.MapPost("/orders", async (OrderReq req, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var outcome = ParseEnum<Outcome>(req.Outcome);
            var side = ParseEnum<OrderSide>(req.Side);
            if (outcome == null || side == null) return Results.BadRequest(new { error = "outcome/side must be yes|no, buy|sell" });
            var type = string.Equals(req.Type, "market", StringComparison.OrdinalIgnoreCase) ? OrderType.Market : OrderType.Limit;
            var price = req.Price ?? 0;
            if (type == OrderType.Limit && (price < Prices.MinTick || price > Prices.MaxTick))
                return Results.BadRequest(new { error = "limit price must be 1..999 ticks" });
            var size = Amount(req.Size);
            if (size <= 0) return Results.BadRequest(new { error = "size required" });

            try
            {
                var result = await core.PlaceOrderAsync(new OrderRequest(user, req.MarketId ?? "", outcome.Value, side.Value, size, price, type, req.ClientOrderId ?? "", null));
                return Results.Ok(new
                {
                    orderId = result.Order.OrderId,
                    status = result.TerminalStatus.ToString().ToLowerInvariant(),
                    size = result.Order.Size.ToString(),
                    remaining = result.Order.Remaining.ToString(),
                    fills = result.Fills.Select(f => new { tradeId = f.Trade.TradeId, tradeClass = f.Trade.Class.ToString().ToLowerInvariant(), size = f.Size.ToString(), priceTick = f.Trade.OutcomeTick }).ToList(),
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapDelete("/orders/{id}", async (string id, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var result = await core.CancelOrderAsync(id);
            return result.Cancelled ? Results.Ok(new { orderId = id, status = "cancelled" }) : Results.NotFound(new { error = "order not cancellable" });
        });

        g.MapGet("/orders", async (HttpRequest http, string? status) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var orders = await core.GetOrdersAsync(user, ParseEnum<OrderStatus>(status));
            return Results.Ok(orders.Select(o => OrderView(o)).ToList());
        });

        g.MapGet("/orders/{id}", async (string id, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var order = await core.GetOrderAsync(id);
            return order == null || order.User != user ? Results.NotFound() : Results.Ok(OrderView(order));
        });

        g.MapGet("/book/{marketId}", async (string marketId) =>
        {
            try
            {
                var snap = await core.GetBookAsync(marketId);
                return Results.Ok(new
                {
                    marketId = snap.MarketId,
                    generation = snap.Generation,
                    yes = new { bids = Levels(snap.YesBids), asks = Levels(snap.YesAsks) },
                    no = new { bids = Levels(snap.NoBids), asks = Levels(snap.NoAsks) },
                });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        g.MapGet("/markets", async () =>
        {
            var markets = await core.GetMarketsAsync();
            return Results.Ok(markets.Select(MarketView).ToList());
        });

        g.MapGet("/markets/{id}", async (string id) =>
        {
            try
            {
                var m = await core.GetMarketAsync(id);
                return Results.Ok(new
                {
                    market = MarketView(m),
                    trades = m.Trades.Select(t => new { tradeId = t.TradeId, tradeClass = t.Class.ToString().ToLowerInvariant(), size = t.Size.ToString(), yesBasisTick = t.YesBasisTick, batchId = t.BatchId, at = t.UnixSec }).ToList(),
                });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        g.MapGet("/positions", async (HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            var b = await core.GetBalancesAsync(user);
            return Results.Ok(b.Positions);
        });

        g.MapPost("/markets/{id}/redeem", async (string id, AmountBody body, HttpRequest http) =>
        {
            var user = UserOf(http, sessions, cfg);
            if (user == null) return Results.Unauthorized();
            return await Submit(() => gateway.SubmitRedeemAsync(user, id, Amount(body.Amount), CancellationToken.None));
        });

        g.MapPost("/markets/{id}/resolve", async (string id, ResolveReq req, HttpRequest http) =>
        {
            // Operator-only demo hook: resolve a market to its winning outcome. Goes through
            // the resolution gate (book + pending fills drained) before the on-chain tx.
            var user = UserOf(http, sessions, cfg);
            if (user == null || !string.Equals(user, cfg.Chain.OperatorAddress, StringComparison.OrdinalIgnoreCase))
                return Results.Forbid();
            var outcome = ParseEnum<Outcome>(req.Outcome);
            if (outcome == null) return Results.BadRequest(new { error = "outcome must be yes|no" });
            try
            {
                await core.ResolveMarketAsync(id, outcome.Value);
                return Results.Ok(new { marketId = id, resolved = true, outcome = outcome.Value.ToString().ToLowerInvariant() });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        g.MapGet("/resolution/{marketId}", async (string marketId) =>
        {
            try
            {
                var m = await core.GetMarketAsync(marketId);
                return Results.Ok(new { marketId, resolved = m.Resolved, winningOutcome = m.WinningOutcome?.ToString().ToLowerInvariant() });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        return g;
    }

    private static object MarketView(Market m) => new
    {
        marketId = m.MarketId,
        exists = m.Exists,
        resolved = m.Resolved,
        winningOutcome = m.WinningOutcome?.ToString().ToLowerInvariant(),
        born = m.BornRequestId == null ? null : new { requestId = m.BornRequestId.ToString(), marginalYesTick = m.BornMarginalYesTick, vwapYesTick = m.BornVwapYesTick, filled = m.BornFilledQuantity?.ToString() },
    };

    private static object OrderView(Order o) => new
    {
        orderId = o.OrderId,
        marketId = o.MarketId,
        outcome = o.Outcome.ToString().ToLowerInvariant(),
        side = o.Side.ToString().ToLowerInvariant(),
        size = o.Size.ToString(),
        remaining = o.Remaining.ToString(),
        price = o.Price,
        type = o.Type.ToString().ToLowerInvariant(),
        status = o.Status.ToString().ToLowerInvariant(),
        createdAt = o.CreatedAtUnixSec,
    };

    private static object[] Levels(IReadOnlyList<BookLevel> levels)
        => levels.Select(l => new { price = l.Price, size = l.Size.ToString() }).ToArray();

    private static T? ParseEnum<T>(string? s) where T : struct, Enum
        => Enum.TryParse<T>(s, true, out var v) ? v : null;

    public sealed record OrderReq(string? MarketId, string? Outcome, string? Side, string? Size, long? Price, string? Type, string? ClientOrderId);
    public sealed record AmountBody(string? Amount);
    public sealed record ResolveReq(string? Outcome);
}
