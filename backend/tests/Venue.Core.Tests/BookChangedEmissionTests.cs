using System.Numerics;
using Venue.Broadcasting;
using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;
using Venue.Settlement;
using Xunit;

namespace Venue.Core.Tests;

/// <summary>PLAN_CONSOLIDATED_BOOK_UI: a pure no-fill rest must emit exactly ONE
/// BookChanged (the live ladder refreshes without a reload), and a partial-fill rest
/// must NOT emit a duplicate (the fills branch already emits once).</summary>
public class BookChangedEmissionTests
{
    private static readonly string Market = Hash.NormalizeBytes32("0xBBBB");

    private sealed class CountingSink : IEventSink
    {
        public int BookChangedCount;
        public void BookChanged(string marketId) => BookChangedCount++;
        public void Fills(string marketId, IReadOnlyList<SettlementTrade> trades) { }
        public void OrderUpdated(string user, string orderId, string status) { }
        public void BalanceChanged(string user) { }
        public void SettlementOutcome(string marketId, string batchId, TxStatus status, string? error, IReadOnlyList<string> tradeIds) { }
        public void RfmChanged(BigInteger requestId) { }
        public void MarketBorn(string marketId) { }
        public void GenerationBump() { }
    }

    private static async Task<(VenueCore core, CountingSink sink)> SeededCoreAsync()
    {
        var cfg = TestData.Cfg;
        var sink = new CountingSink();
        var core = new VenueCore(cfg, new SimulatedChainGateway(cfg), sink);
        var yesId = Assets.TokenId(Market, Outcome.Yes);
        await core.ApplyEventsAsync(new VenueEvent[]
        {
            new MarketCreated(TestData.Ot, 1, 0, "0x", Market, Array.Empty<byte>()),
            new Deposited(TestData.Vault, 2, 0, "0x", TestData.Alice, 10_000_000),
            new Deposited(TestData.Vault, 3, 0, "0x", TestData.Bob, 10_000_000),
            new TokensDeposited(TestData.Vault, 4, 0, "0x", TestData.Alice, yesId, 10_000_000),
        });
        return (core, sink);
    }

    [Fact]
    public async Task PureRest_EmitsExactlyOneBookChanged()
    {
        var (core, sink) = await SeededCoreAsync();
        var before = sink.BookChangedCount;
        var rest = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, Market, Outcome.Yes, OrderSide.Buy, 100_000, 400, OrderType.Limit, "pure-rest", null));
        Assert.Equal(OrderStatus.Resting, rest.TerminalStatus);
        Assert.Empty(rest.Fills);
        Assert.Equal(before + 1, sink.BookChangedCount);
    }

    [Fact]
    public async Task PartialFillRest_EmitsExactlyOnce_NoDuplicate()
    {
        var (core, sink) = await SeededCoreAsync();
        // alice rests SELL YES 100k @600; bob BUYs 250k @600 -> fills 100k, remainder rests.
        var sell = await core.PlaceOrderAsync(new OrderRequest(TestData.Alice, Market, Outcome.Yes, OrderSide.Sell, 100_000, 600, OrderType.Limit, "maker", null));
        Assert.Equal(OrderStatus.Resting, sell.TerminalStatus);
        var before = sink.BookChangedCount;
        var buy = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, Market, Outcome.Yes, OrderSide.Buy, 250_000, 600, OrderType.Limit, "taker", null));
        Assert.Equal(OrderStatus.Resting, buy.TerminalStatus); // partial-fill remainder rests
        Assert.NotEmpty(buy.Fills);
        Assert.Equal(before + 1, sink.BookChangedCount); // fills branch only — no duplicate
    }

    [Fact]
    public async Task Rejected_EmitsNoBookChanged()
    {
        var (core, sink) = await SeededCoreAsync();
        var before = sink.BookChangedCount;
        // bob holds no YES tokens -> SELL rejected at admission
        var rejected = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, Market, Outcome.Yes, OrderSide.Sell, 100_000, 600, OrderType.Limit, "rej", null));
        Assert.Equal(OrderStatus.Rejected, rejected.TerminalStatus);
        Assert.Equal(before, sink.BookChangedCount);
    }
}
