using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Venue.Engine;
using Venue.Infrastructure;
using Venue.Settlement;
using Xunit;

namespace Venue.Core.Tests;

public class SettlementTests
{
    private const string MarketId = "0x2222222222222222222222222222222222222222222222222222222222222222";

    // ------------------------------------------------------------ classification

    [Theory]
    [InlineData(OrderSide.Buy, Outcome.Yes, OrderSide.Buy, Outcome.No, TradeClass.Mint)]
    [InlineData(OrderSide.Buy, Outcome.No, OrderSide.Buy, Outcome.Yes, TradeClass.Mint)]
    [InlineData(OrderSide.Sell, Outcome.Yes, OrderSide.Sell, Outcome.No, TradeClass.Merge)]
    [InlineData(OrderSide.Sell, Outcome.No, OrderSide.Sell, Outcome.Yes, TradeClass.Merge)]
    [InlineData(OrderSide.Buy, Outcome.Yes, OrderSide.Sell, Outcome.Yes, TradeClass.Transfer)]
    [InlineData(OrderSide.Sell, Outcome.Yes, OrderSide.Buy, Outcome.Yes, TradeClass.Transfer)]
    [InlineData(OrderSide.Buy, Outcome.No, OrderSide.Sell, Outcome.No, TradeClass.Transfer)]
    [InlineData(OrderSide.Sell, Outcome.No, OrderSide.Buy, Outcome.No, TradeClass.Transfer)]
    public void Classify_AllFourDirectionPairs(OrderSide makerSide, Outcome makerOutcome, OrderSide takerSide, Outcome takerOutcome, TradeClass expected)
    {
        var maker = MakeOrder(TestData.Alice, makerSide, makerOutcome);
        var taker = MakeOrder(TestData.Bob, takerSide, takerOutcome);
        Assert.Equal(expected, TradeBuilder.Classify(maker, taker));
    }

    [Fact]
    public void Build_MintEncodesYesAndNoParties()
    {
        var maker = MakeOrder(TestData.Alice, OrderSide.Buy, Outcome.Yes);
        var taker = MakeOrder(TestData.Bob, OrderSide.Buy, Outcome.No);
        var trade = TradeBuilder.Build(maker, taker, 100, 400, TradeId(1));
        Assert.Equal(TradeClass.Mint, trade.Class);
        Assert.Equal(TestData.Alice, trade.PartyA); // yes party = the BUY YES
        Assert.Equal(TestData.Bob, trade.PartyB);   // no party = the BUY NO
        Assert.Equal(400, trade.OutcomeTick);
        Assert.Null(trade.Outcome);
    }

    [Fact]
    public void Build_TransferNo_UsesComplementForOutcomeTick()
    {
        var maker = MakeOrder(TestData.Alice, OrderSide.Sell, Outcome.No);   // stored BUY @ 1000-600
        var taker = MakeOrder(TestData.Bob, OrderSide.Buy, Outcome.No);      // stored SELL @ 1000-600
        var trade = TradeBuilder.Build(maker, taker, 50, 400, TradeId(2));
        Assert.Equal(TradeClass.Transfer, trade.Class);
        Assert.Equal(Outcome.No, trade.Outcome);
        Assert.Equal(600, trade.OutcomeTick);            // NO price = 1000 - yesBasisTick
        Assert.Equal(TestData.Alice, trade.PartyA);      // seller
        Assert.Equal(TestData.Bob, trade.PartyB);        // buyer
    }

    [Fact]
    public void Build_MergeEncodesYesAndNoParties()
    {
        var maker = MakeOrder(TestData.Alice, OrderSide.Sell, Outcome.No);
        var taker = MakeOrder(TestData.Bob, OrderSide.Sell, Outcome.Yes);
        var trade = TradeBuilder.Build(maker, taker, 30, 650, TradeId(3));
        Assert.Equal(TradeClass.Merge, trade.Class);
        Assert.Equal(TestData.Bob, trade.PartyA);        // SELL YES party
        Assert.Equal(TestData.Alice, trade.PartyB);      // SELL NO party
        Assert.Equal(650, trade.OutcomeTick);
    }

    // ------------------------------------------------------------ revert parsing

    [Fact]
    public void RevertParser_ReadsFailingIndexAndTradeId()
    {
        var tradeId = "0x" + new string('a', 64);
        var hex = SelectorOf("SettleBatchFailed(uint256,bytes32)") + Uint(2) + tradeId[2..];
        var info = RevertParser.Parse("0x" + hex);
        Assert.Equal(2, info.FailIndex);
        Assert.Equal(tradeId, info.TradeId);
        Assert.Equal("SettleBatchFailed", info.ErrorName);
    }

    [Fact]
    public void RevertParser_RecognizesBatchReused()
    {
        var batchId = "0x" + new string('b', 64);
        var hex = SelectorOf("BatchReused(bytes32)") + batchId[2..];
        var info = RevertParser.Parse("0x" + hex);
        Assert.Equal("BatchReused", info.ErrorName);
        Assert.Equal(batchId, info.TradeId);
        Assert.Null(info.FailIndex);
    }

    [Fact]
    public void RevertParser_UnknownRevert_IsUnclear()
    {
        var info = RevertParser.Parse("0xdeadbeef00112233445566778899aabb");
        Assert.Equal("Unknown", info.ErrorName);
        Assert.Null(info.FailIndex);
        Assert.Null(info.TradeId);
    }

    // ------------------------------------------------------------ batcher repair

    [Fact]
    public async Task Batcher_DropsFailingTrade_RepairsAndResubmitsRest()
    {
        var gateway = new ScriptedGateway();
        gateway.Outcomes.Enqueue(new SettlementReceipt(TxStatus.Reverted, new BatchRevertInfo(1, "0x" + new string('c', 64), "SettleBatchFailed", "")));
        gateway.Outcomes.Enqueue(new SettlementReceipt(TxStatus.Confirmed, null));

        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));

        for (var i = 0; i < 3; i++) batcher.Enqueue(Match(i));

        await WaitUntilAsync(() => coordinator.Confirmed.Count == 1, TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.Single(coordinator.Repaired);
        Assert.Equal(1, coordinator.Repaired[0].Revert.FailIndex);
        Assert.Equal(3, coordinator.Repaired[0].Matches.Count);
        Assert.Single(coordinator.Confirmed);
        Assert.Equal(2, coordinator.Confirmed[0].Matches.Count); // failing trade dropped
        Assert.DoesNotContain(coordinator.Confirmed[0].Matches, m => m.Trade.TradeId == coordinator.Repaired[0].Matches[1].Trade.TradeId);
        Assert.Empty(coordinator.CancelledAll);
        Assert.Equal(2, gateway.SettlementSubmits); // initial + resubmission
    }

    [Fact]
    public async Task Batcher_UnclearRevert_CancelsAllAndReCrosses()
    {
        var gateway = new ScriptedGateway();
        gateway.Outcomes.Enqueue(new SettlementReceipt(TxStatus.Reverted, new BatchRevertInfo(null, null, "InsufficientFree", "")));
        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));

        for (var i = 0; i < 2; i++) batcher.Enqueue(Match(i));

        await WaitUntilAsync(() => coordinator.CancelledAll.Count == 1, TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.Single(coordinator.CancelledAll);
        Assert.Equal(2, coordinator.CancelledAll[0].Matches.Count);
        Assert.Empty(coordinator.Confirmed);
        Assert.Equal(1, gateway.SettlementSubmits);
    }

    // ------------------------------------------------------------- fixtures

    private static Order MakeOrder(string user, OrderSide side, Outcome outcome) => new()
    {
        OrderId = "o_" + Guid.NewGuid().ToString("N"),
        User = user,
        MarketId = MarketId,
        Outcome = outcome,
        Side = side,
        Size = 100,
        Remaining = 100,
        Price = outcome == Outcome.Yes ? 400 : 600,
        Type = OrderType.Limit,
        Status = OrderStatus.Resting,
        BookSide = Order.StoredSideFor(side, outcome),
        BookPrice = Order.StoredPriceFor(outcome, outcome == Outcome.Yes ? 400 : 600),
        ReservedAsset = Assets.Usdc,
        ReservedAmount = 100,
        CreatedAtUnixSec = 0,
    };

    private static MatchedTrade Match(int i)
    {
        var maker = MakeOrder(TestData.Alice, OrderSide.Sell, Outcome.Yes);
        var taker = MakeOrder(TestData.Bob, OrderSide.Buy, Outcome.Yes);
        var trade = TradeBuilder.Build(maker, taker, 10, 500, TradeId(i + 100));
        return new MatchedTrade(trade, maker.OrderId, taker.OrderId, maker, taker, 10);
    }

    private static string TradeId(int n) => Hash.NormalizeBytes32("0x" + n.ToString("x"));

    private static string SelectorOf(string signature) => Hash.KeccakHex(signature)[2..10];
    private static string Uint(int v) => v.ToString("x64");

    private static async Task WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    private sealed class ScriptedGateway : IChainGateway
    {
        public Queue<SettlementReceipt> Outcomes { get; } = new();
        public int SettlementSubmits { get; private set; }
        public bool Simulated => true;

        public Task<ulong> LatestBlockAsync(CancellationToken ct) => Task.FromResult(0UL);
        public Task<string> GetBlockHashAsync(ulong blockNumber, CancellationToken ct) => Task.FromResult("0x0");
        public Task<IReadOnlyList<VenueEvent>> FetchLogsAsync(ulong fromBlock, ulong toBlock, CancellationToken ct) => Task.FromResult<IReadOnlyList<VenueEvent>>(Array.Empty<VenueEvent>());
        public Task<IReadOnlyList<VenueEvent>> DecodeReceiptEventsAsync(string txHash, CancellationToken ct) => Task.FromResult<IReadOnlyList<VenueEvent>>(Array.Empty<VenueEvent>());

        public Task<string> SubmitSettlementAsync(string batchId, IReadOnlyList<SettlementTrade> trades, CancellationToken ct)
        {
            SettlementSubmits++;
            return Task.FromResult("0x" + SettlementSubmits.ToString("x"));
        }

        public Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct)
            => Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : new SettlementReceipt(TxStatus.Confirmed, null));

        public Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct) => Task.FromResult(TxStatus.Pending);
    }

    private sealed class RecordingCoordinator : ISettlementCoordinator
    {
        public List<(string BatchId, IReadOnlyList<MatchedTrade> Matches)> Confirmed { get; } = new();
        public List<(string BatchId, IReadOnlyList<MatchedTrade> Matches, BatchRevertInfo Revert)> Repaired { get; } = new();
        public List<(IReadOnlyList<MatchedTrade> Matches, string Reason)> CancelledAll { get; } = new();

        public Task ConfirmBatchAsync(string batchId, IReadOnlyList<MatchedTrade> matches)
        {
            Confirmed.Add((batchId, matches));
            return Task.CompletedTask;
        }

        public Task RepairBatchAsync(string batchId, IReadOnlyList<MatchedTrade> matches, BatchRevertInfo revert)
        {
            Repaired.Add((batchId, matches, revert));
            return Task.CompletedTask;
        }

        public Task CancelAllOrdersAsync(IReadOnlyList<MatchedTrade> matches, string reason)
        {
            CancelledAll.Add((matches, reason));
            return Task.CompletedTask;
        }
    }
}
