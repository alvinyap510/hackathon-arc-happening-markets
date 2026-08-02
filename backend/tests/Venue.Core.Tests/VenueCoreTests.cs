using System.Numerics;
using Venue.Broadcasting;
using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;
using Venue.Settlement;
using Xunit;

namespace Venue.Core.Tests;

/// <summary>VenueCore integration tests (resolution gate, market lifecycle).</summary>
public class VenueCoreTests
{
    private static readonly string Market = Hash.NormalizeBytes32("0xCCCC");

    private static VenueCore NewCore()
    {
        var cfg = TestData.Cfg;
        return new VenueCore(cfg, new SimulatedChainGateway(cfg), new NullEventSink());
    }

    private static async Task<VenueCore> SeedMarketAsync(VenueCore core)
    {
        var yesId = Assets.TokenId(Market, Outcome.Yes);
        await core.ApplyEventsAsync(new VenueEvent[]
        {
            new MarketCreated(TestData.Ot, 1, 0, "0x", Market, Array.Empty<byte>()),
            new Deposited(TestData.Vault, 2, 0, "0x", TestData.Alice, 10_000_000),
            new Deposited(TestData.Vault, 3, 0, "0x", TestData.Bob, 10_000_000),
            new TokensDeposited(TestData.Vault, 4, 0, "0x", TestData.Alice, yesId, 10_000_000),
        });
        return core;
    }

    [Fact]
    public async Task ResolutionGate_RejectsOrdersAndClosesBookAndPendingFills()
    {
        var core = await SeedMarketAsync(NewCore());

        // alice rests a SELL YES; bob's BUY YES crosses it -> a fill is queued for settlement.
        var sell = await core.PlaceOrderAsync(new OrderRequest(TestData.Alice, Market, Outcome.Yes, OrderSide.Sell, 100_000, 600, OrderType.Limit, "s1", null));
        Assert.Equal(OrderStatus.Resting, sell.TerminalStatus);
        var buy = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, Market, Outcome.Yes, OrderSide.Buy, 100_000, 600, OrderType.Limit, "b1", null));
        Assert.Equal(OrderStatus.Filled, buy.TerminalStatus);
        Assert.Equal(1, core.PendingSettlements);

        // resolution closes the market: pending fill drained, both orders unwound.
        await core.ApplyEventsAsync(new[] { new MarketResolved(TestData.Ot, 5, 0, "0x", Market, Outcome.Yes) });
        Assert.Equal(0, core.PendingSettlements);
        Assert.Null(await core.GetOrderAsync(sell.Order.OrderId));
        Assert.Null(await core.GetOrderAsync(buy.Order.OrderId));

        // new orders on a resolved market are rejected at intake with the resolution reason.
        var after = await core.PlaceOrderAsync(new OrderRequest(TestData.Alice, Market, Outcome.Yes, OrderSide.Buy, 100, 600, OrderType.Limit, "s2", null));
        Assert.Equal(OrderStatus.Rejected, after.TerminalStatus);
        Assert.Equal("market_resolved", after.RejectReason);

        // reservations were fully released on unwind.
        var balances = await core.GetBalancesAsync(TestData.Bob);
        Assert.Equal(new BigInteger(10_000_000), balances.Available);
    }

    [Fact]
    public async Task ResolutionGate_UnwindsFilledOrdersEvenWhenNotResting()
    {
        var core = await SeedMarketAsync(NewCore());

        // alice SELL YES (fully matched by bob) -> alice's order is Filled, not resting.
        await core.PlaceOrderAsync(new OrderRequest(TestData.Alice, Market, Outcome.Yes, OrderSide.Sell, 100_000, 600, OrderType.Limit, "s1", null));
        var buy = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, Market, Outcome.Yes, OrderSide.Buy, 100_000, 600, OrderType.Limit, "b1", null));
        Assert.Equal(OrderStatus.Filled, buy.TerminalStatus);
        Assert.Equal(1, core.PendingSettlements);

        await core.ApplyEventsAsync(new[] { new MarketResolved(TestData.Ot, 5, 0, "0x", Market, Outcome.Yes) });

        // the Filled (non-resting) orders were unwound too — no stranded reservations.
        var aliceBal = await core.GetBalancesAsync(TestData.Alice);
        Assert.Equal(new BigInteger(10_000_000), aliceBal.Available);
        var bobBal = await core.GetBalancesAsync(TestData.Bob);
        Assert.Equal(new BigInteger(10_000_000), bobBal.Available);
    }

    [Fact]
    public async Task FailedFullFill_ReleasesReservationOfFilledOrder()
    {
        var core = await SeedMarketAsync(NewCore());

        await core.PlaceOrderAsync(new OrderRequest(TestData.Alice, Market, Outcome.Yes, OrderSide.Sell, 100_000, 600, OrderType.Limit, "s1", null));
        var buy = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, Market, Outcome.Yes, OrderSide.Buy, 100_000, 600, OrderType.Limit, "b1", null));
        Assert.Equal(OrderStatus.Filled, buy.TerminalStatus); // bob fully matched -> Filled, not Resting

        // A withdrawal-raced full fill reverts on chain: the repair path must unwind bob's
        // Filled order (Cancel cannot, it only handles Resting/New).
        await core.RepairBatchAsync("batch_0", buy.Fills.ToList(), new BatchRevertInfo(0, buy.Fills[0].Trade.TradeId, "SettleBatchFailed", ""));

        var bobBal = await core.GetBalancesAsync(TestData.Bob);
        Assert.Equal(new BigInteger(10_000_000), bobBal.Available); // reservation fully released
        Assert.Null(await core.GetOrderAsync(buy.Order.OrderId));
    }
}
