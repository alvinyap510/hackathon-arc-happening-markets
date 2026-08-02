using System.Numerics;
using Venue.Domain;
using Venue.Engine;
using Xunit;
using TradingEngine = Venue.Engine.Engine;

namespace Venue.Core.Tests;

public class EngineTests
{
    private const string MarketId = "0x1111111111111111111111111111111111111111111111111111111111111111";

    private static (TradingEngine TradingEngine, Domain.Market Market) Setup(BigInteger usdcForEach)
    {
        var ledger = TestData.NewLedger();
        TestData.SeedUsdc(ledger, TestData.Alice, usdcForEach);
        TestData.SeedUsdc(ledger, TestData.Bob, usdcForEach);
        TestData.SeedUsdc(ledger, TestData.Carol, usdcForEach);
        // sellers need token positions to rest SELL orders
        TestData.SeedTokens(ledger, TestData.Alice, MarketId, Outcome.Yes, usdcForEach);
        TestData.SeedTokens(ledger, TestData.Alice, MarketId, Outcome.No, usdcForEach);
        TestData.SeedTokens(ledger, TestData.Bob, MarketId, Outcome.Yes, usdcForEach);
        TestData.SeedTokens(ledger, TestData.Bob, MarketId, Outcome.No, usdcForEach);
        TestData.SeedTokens(ledger, TestData.Carol, MarketId, Outcome.Yes, usdcForEach);
        TestData.SeedTokens(ledger, TestData.Carol, MarketId, Outcome.No, usdcForEach);
        var market = TestData.MarketFor(MarketId);
        var markets = new Dictionary<string, Market> { [MarketId] = market };
        return (new TradingEngine(ledger, markets), market);
    }

    private static OrderRequest Req(string user, Outcome outcome, OrderSide side, long price, BigInteger size, OrderType type = OrderType.Limit)
        => new(user, MarketId, outcome, side, size, price, type, "", null);

    // ------------------------------------------------------------- transforms

    [Fact]
    public void IntakeTransform_MapsFourDirectionsToYesBasis()
    {
        Assert.Equal(BookSide.Bid, Order.StoredSideFor(OrderSide.Buy, Outcome.Yes));
        Assert.Equal(BookSide.Ask, Order.StoredSideFor(OrderSide.Sell, Outcome.Yes));
        Assert.Equal(BookSide.Ask, Order.StoredSideFor(OrderSide.Buy, Outcome.No));
        Assert.Equal(BookSide.Bid, Order.StoredSideFor(OrderSide.Sell, Outcome.No));

        Assert.Equal(600, Order.StoredPriceFor(Outcome.Yes, 600));
        Assert.Equal(400, Order.StoredPriceFor(Outcome.No, 600)); // BUY NO @600 == SELL YES @400
        Assert.Equal(400, Order.StoredPriceFor(Outcome.No, 600)); // SELL NO @600 == BUY YES @400
        Assert.Equal(650, Order.StoredPriceFor(Outcome.No, 350));
    }

    // ------------------------------------------------------------ transfer

    [Fact]
    public void Matching_BuyYesCrossesSellYes_ClassifiedTransferYes()
    {
        var (engine, market) = Setup(100_000);
        var sell = engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Sell, 600, 100), market);
        Assert.Equal(OrderStatus.Resting, sell.TerminalStatus);

        var buy = engine.Place(Req(TestData.Bob, Outcome.Yes, OrderSide.Buy, 600, 100), market);
        Assert.Equal(OrderStatus.Filled, buy.TerminalStatus);
        var fill = Assert.Single(buy.Fills);
        Assert.Equal(TradeClass.Transfer, fill.Trade.Class);
        Assert.Equal(Outcome.Yes, fill.Trade.Outcome);
        Assert.Equal(TestData.Alice, fill.Trade.PartyA); // seller
        Assert.Equal(TestData.Bob, fill.Trade.PartyB);   // buyer
        Assert.Equal(600, fill.Trade.OutcomeTick);
        Assert.Equal(new BigInteger(100), fill.Size);
    }

    [Fact]
    public void Matching_BuyNoCrossesSellNo_ClassifiedTransferNo()
    {
        var (engine, _) = Setup(100_000);
        engine.Place(Req(TestData.Alice, Outcome.No, OrderSide.Sell, 600, 100), new Market { MarketId = MarketId });
        var buy = engine.Place(Req(TestData.Bob, Outcome.No, OrderSide.Buy, 600, 100), new Market { MarketId = MarketId });
        Assert.Equal(OrderStatus.Filled, buy.TerminalStatus);
        var fill = Assert.Single(buy.Fills);
        Assert.Equal(TradeClass.Transfer, fill.Trade.Class);
        Assert.Equal(Outcome.No, fill.Trade.Outcome);
        Assert.Equal(600, fill.Trade.OutcomeTick); // NO-price tick
        Assert.Equal(TestData.Alice, fill.Trade.PartyA);
        Assert.Equal(TestData.Bob, fill.Trade.PartyB);
    }

    // ----------------------------------------------------------------- mint

    [Fact]
    public void Matching_BuyYesCrossesBuyNo_ClassifiedMint()
    {
        var (engine, _) = Setup(100_000);
        engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Buy, 400, 100), new Market { MarketId = MarketId });
        var buyNo = engine.Place(Req(TestData.Bob, Outcome.No, OrderSide.Buy, 600, 100), new Market { MarketId = MarketId });
        Assert.Equal(OrderStatus.Filled, buyNo.TerminalStatus);
        var fill = Assert.Single(buyNo.Fills);
        Assert.Equal(TradeClass.Mint, fill.Trade.Class);
        Assert.Equal(TestData.Alice, fill.Trade.PartyA); // yes party
        Assert.Equal(TestData.Bob, fill.Trade.PartyB);   // no party
        Assert.Equal(400, fill.Trade.OutcomeTick);       // yes tick
        Assert.Null(fill.Trade.Outcome);
    }

    // ----------------------------------------------------------------- merge

    [Fact]
    public void Matching_SellYesCrossesSellNo_ClassifiedMerge()
    {
        var (engine, _) = Setup(100_000);
        engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Sell, 400, 100), new Market { MarketId = MarketId });
        var sellNo = engine.Place(Req(TestData.Bob, Outcome.No, OrderSide.Sell, 600, 100), new Market { MarketId = MarketId });
        Assert.Equal(OrderStatus.Filled, sellNo.TerminalStatus);
        var fill = Assert.Single(sellNo.Fills);
        Assert.Equal(TradeClass.Merge, fill.Trade.Class);
        Assert.Equal(TestData.Alice, fill.Trade.PartyA); // yes party
        Assert.Equal(TestData.Bob, fill.Trade.PartyB);   // no party
        Assert.Equal(400, fill.Trade.OutcomeTick);
    }

    // ------------------------------------------------------------ partial fills

    [Fact]
    public void Matching_PartialFill_RestsRemainderAndKeepsMaker()
    {
        var (engine, market) = Setup(100_000);
        var sell = engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Sell, 500, 40), market);
        Assert.Equal(OrderStatus.Resting, sell.TerminalStatus);

        var buy = engine.Place(Req(TestData.Bob, Outcome.Yes, OrderSide.Buy, 500, 100), market);
        Assert.Equal(OrderStatus.Resting, buy.TerminalStatus); // 40 filled, 60 rests as bid
        Assert.Equal(OrderStatus.Filled, sell.Order.Status);   // alice fully filled
        Assert.Equal(BigInteger.Zero, sell.Order.Remaining);
        Assert.Equal(new BigInteger(60), buy.Order.Remaining);
        var fill = Assert.Single(buy.Fills);
        Assert.Equal(new BigInteger(40), fill.Size);
    }

    [Fact]
    public void Matching_MultiLevelFillsAtMakerPrices()
    {
        var (engine, market) = Setup(100_000);
        engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Sell, 400, 50), market);
        engine.Place(Req(TestData.Carol, Outcome.Yes, OrderSide.Sell, 500, 50), market);

        var buy = engine.Place(Req(TestData.Bob, Outcome.Yes, OrderSide.Buy, 600, 120), market);
        Assert.Equal(OrderStatus.Resting, buy.TerminalStatus); // 100 filled, 20 rests
        Assert.Equal(2, buy.Fills.Count);
        Assert.Equal(new BigInteger(50), buy.Fills[0].Size);
        Assert.Equal(400, buy.Fills[0].Trade.OutcomeTick);
        Assert.Equal(new BigInteger(50), buy.Fills[1].Size);
        Assert.Equal(500, buy.Fills[1].Trade.OutcomeTick);
        Assert.Equal(new BigInteger(20), buy.Order.Remaining);
    }

    [Fact]
    public void Matching_MarketOrderSweepsBook_KillsRemainder()
    {
        var (engine, market) = Setup(100_000);
        engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Sell, 400, 50), market);
        engine.Place(Req(TestData.Carol, Outcome.Yes, OrderSide.Sell, 500, 50), market);

        var buy = engine.Place(Req(TestData.Bob, Outcome.Yes, OrderSide.Buy, 0, 200, OrderType.Market), market);
        Assert.Equal(OrderStatus.Partial, buy.TerminalStatus); // 100 filled, 100 killed
        Assert.Equal(2, buy.Fills.Count);
        Assert.Equal(new BigInteger(100), buy.Order.Remaining);
    }

    // ------------------------------------------------- NO-side market orders

    [Fact]
    public void Matching_BuyNoMarketOrder_SweepsRestingSellNo()
    {
        var (engine, market) = Setup(100_000);
        var sellNo = engine.Place(Req(TestData.Alice, Outcome.No, OrderSide.Sell, 600, 100), market);
        Assert.Equal(OrderStatus.Resting, sellNo.TerminalStatus); // stored BUY @ 400

        var buyNo = engine.Place(Req(TestData.Bob, Outcome.No, OrderSide.Buy, 0, 100, OrderType.Market), market);
        Assert.Equal(OrderStatus.Filled, buyNo.TerminalStatus); // market sweeps the resting sell
        var fill = Assert.Single(buyNo.Fills);
        Assert.Equal(TradeClass.Transfer, fill.Trade.Class);
        Assert.Equal(Outcome.No, fill.Trade.Outcome);
        Assert.Equal(600, fill.Trade.OutcomeTick); // NO-price tick
    }

    [Fact]
    public void Matching_SellNoMarketOrder_SweepsRestingBuyNo()
    {
        var (engine, market) = Setup(100_000);
        var buyNo = engine.Place(Req(TestData.Alice, Outcome.No, OrderSide.Buy, 600, 100), market);
        Assert.Equal(OrderStatus.Resting, buyNo.TerminalStatus); // stored SELL @ 400

        var sellNo = engine.Place(Req(TestData.Bob, Outcome.No, OrderSide.Sell, 0, 100, OrderType.Market), market);
        Assert.Equal(OrderStatus.Filled, sellNo.TerminalStatus);
        var fill = Assert.Single(sellNo.Fills);
        Assert.Equal(TradeClass.Transfer, fill.Trade.Class);
        Assert.Equal(600, fill.Trade.OutcomeTick);
    }

    [Fact]
    public void Reservations_MarketBuy_ReservesFullNotional()
    {
        // A market BUY reserves its WORST-CASE spend (the full notional), never zero: a buyer
        // who cannot cover the full notional is rejected even before the book is consulted.
        var ledger = TestData.NewLedger();
        TestData.SeedUsdc(ledger, TestData.Bob, 99); // one micro short of 100 shares at $1.00
        var engine = TestData.NewEngine(ledger, MarketId);
        var market = TestData.MarketFor(MarketId);

        var rejected = engine.Place(Req(TestData.Bob, Outcome.Yes, OrderSide.Buy, 0, 100, OrderType.Market), market);
        Assert.Equal(OrderStatus.Rejected, rejected.TerminalStatus);
        Assert.Equal("insufficient_available", rejected.RejectReason);

        TestData.SeedUsdc(ledger, TestData.Bob, 1); // now exactly 100
        var accepted = engine.Place(Req(TestData.Bob, Outcome.Yes, OrderSide.Buy, 0, 100, OrderType.Market), market);
        Assert.Equal(OrderStatus.Partial, accepted.TerminalStatus); // no liquidity: killed, reservation released
        Assert.Equal(new BigInteger(100), ledger.Available(TestData.Bob, Assets.Usdc));
    }

    // ----------------------------------------------------------- reservations

    [Fact]
    public void Reservations_AvailableReflectsOpenOrderCommitment()
    {
        var ledger = TestData.NewLedger();
        TestData.SeedUsdc(ledger, TestData.Alice, 10_000);
        var engine = TestData.NewEngine(ledger, MarketId);

        Assert.Equal(new BigInteger(10_000), ledger.Available(TestData.Alice, Assets.Usdc));
        var order = engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Buy, 500, 100), TestData.MarketFor(MarketId));
        // cost = 100 * 500 / 1000 = 50
        Assert.Equal(new BigInteger(50), order.Order.ReservedAmount);
        Assert.Equal(new BigInteger(9_950), ledger.Available(TestData.Alice, Assets.Usdc));

        engine.Cancel(order.Order.OrderId);
        Assert.Equal(new BigInteger(10_000), ledger.Available(TestData.Alice, Assets.Usdc));
    }

    [Fact]
    public void InsolvencySweep_CancelsUnderwaterRestingOrders()
    {
        var ledger = TestData.NewLedger();
        TestData.SeedUsdc(ledger, TestData.Alice, 10_000);
        var engine = TestData.NewEngine(ledger, MarketId);
        var order = engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Buy, 500, 100), TestData.MarketFor(MarketId));
        Assert.Equal(OrderStatus.Resting, order.TerminalStatus);

        // Adverse event: alice withdraws everything -> free balance collapses.
        ledger.Apply(new Withdrawn(TestData.Vault, 2, 0, "0xtx", TestData.Alice, 10_000));
        var doomed = engine.InsolvencySweep(TestData.Alice);

        Assert.Contains(doomed, o => o.OrderId == order.Order.OrderId);
        Assert.Equal(OrderStatus.Cancelled, order.Order.Status);
        Assert.Equal(new BigInteger(0), ledger.Available(TestData.Alice, Assets.Usdc));
    }

    [Fact]
    public void BookSnapshot_ProjectsYesAndNoFromOneBook()
    {
        var (engine, market) = Setup(100_000);
        engine.Place(Req(TestData.Alice, Outcome.Yes, OrderSide.Sell, 600, 100), market); // YES ask @600
        engine.Place(Req(TestData.Alice, Outcome.No, OrderSide.Buy, 600, 100), market);   // NO bid @600 -> YES ask @400
        var snap = engine.BookSnapshot(MarketId);

        var yesAsk = Assert.Single(snap.YesAsks);
        Assert.Equal(600, yesAsk.Price);
        var noBid = Assert.Single(snap.NoBids);
        Assert.Equal(600, noBid.Price); // complement back to NO basis
        Assert.Empty(snap.YesBids);
        Assert.Empty(snap.NoAsks);
    }
}
