using System.Numerics;
using Venue.Broadcasting;
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

    [Fact]
    public async Task Batcher_ExhaustedRepairAttempts_UnwindsRemainder()
    {
        var gateway = new ScriptedGateway();
        // Every attributed repair attempt reverts at index 0; after MaxRepairAttempts the loop
        // must unwind what is left rather than silently stranding fills.
        for (var i = 0; i < 10; i++)
            gateway.Outcomes.Enqueue(new SettlementReceipt(TxStatus.Reverted, new BatchRevertInfo(0, "0x" + new string('c', 64), "SettleBatchFailed", "")));

        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));

        for (var i = 0; i < 8; i++) batcher.Enqueue(Match(i));

        await WaitUntilAsync(() => coordinator.CancelledAll.Count == 1, TimeSpan.FromSeconds(8));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.Equal(4, coordinator.Repaired.Count);               // 4 attributed repairs
        Assert.Equal(4, coordinator.CancelledAll[0].Matches.Count); // the remaining 4 unwound
        Assert.Equal("repair_attempts_exhausted", coordinator.CancelledAll[0].Reason);
        Assert.Empty(coordinator.Confirmed);
    }

    // ------------------------------------------------------------ timeout reconciliation

    [Fact]
    public async Task Batcher_SubmitThrowsAndLookupUnknown_HoldsReservations_ThenReconciles()
    {
        var gateway = new ScriptedGateway();
        gateway.SubmitThrows = true;
        gateway.FindState = SettlementTxState.Unknown; // RPC error / inconclusive scan
        gateway.PendingTx = true;
        for (var i = 0; i < 3; i++) gateway.StatusSequence.Enqueue(TxStatus.Pending);
        gateway.StatusSequence.Enqueue(TxStatus.Confirmed);

        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator, reconcileIntervalMs: 10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));
        for (var i = 0; i < 2; i++) batcher.Enqueue(Match(i));

        // Inconclusive lookup: the batch is deferred, reservations HELD — nothing unwound, nothing confirmed.
        await WaitUntilAsync(() => batcher.DeferredCount == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(0, coordinator.CancelledAll.Count);
        Assert.Equal(0, coordinator.Confirmed.Count);

        // The lookup later resolves to Submitted and the tx mines: the deferred batch reconciles
        // to a confirmation — still never unwound.
        gateway.FindState = SettlementTxState.Submitted;
        await WaitUntilAsync(() => coordinator.Confirmed.Count == 1, TimeSpan.FromSeconds(8));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.Single(coordinator.Confirmed);
        Assert.Empty(coordinator.CancelledAll); // reservations were never released
        Assert.Equal(0, batcher.DeferredCount);
    }

    [Fact]
    public async Task Batcher_DeferredUnknownTx_MarketCloses_ReservationsHeldUntilTerminal()
    {
        var gateway = new ScriptedGateway();
        gateway.SubmitThrows = true;
        gateway.FindState = SettlementTxState.Unknown; // inconclusive lookup, tx possibly-mining
        gateway.PendingTx = true;
        for (var i = 0; i < 3; i++) gateway.StatusSequence.Enqueue(TxStatus.Pending);
        gateway.StatusSequence.Enqueue(TxStatus.Confirmed);

        var coordinator = new RecordingCoordinator(); // market OPEN during submit
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator, reconcileIntervalMs: 10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));
        for (var i = 0; i < 2; i++) batcher.Enqueue(Match(i));

        // Submit throws, lookup Unknown -> the batch is deferred with reservations HELD.
        await WaitUntilAsync(() => batcher.DeferredCount == 1, TimeSpan.FromSeconds(5));
        var unwoundBefore = coordinator.Unwound.Count; // the normal submit-path seal (market open)

        // The market CLOSES while the tx is still Unknown. Closure must NOT release the
        // deferred tx's reservations: no closure-unwind on the deferred batch, no unwind/confirm,
        // and the deferred record stays until the tx is reconciled to a terminal state.
        coordinator.CloseAllOnUnwind = true;
        await Task.Delay(300);
        Assert.Equal(1, batcher.DeferredCount);
        Assert.Equal(unwoundBefore, coordinator.Unwound.Count); // no closure-unwind of the deferred batch
        Assert.Equal(0, coordinator.CancelledAll.Count);
        Assert.Equal(0, coordinator.Confirmed.Count);

        // Lookup resolves to Submitted and the tx mines: settled then, never released early.
        gateway.FindState = SettlementTxState.Submitted;
        await WaitUntilAsync(() => coordinator.Confirmed.Count == 1, TimeSpan.FromSeconds(8));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.Single(coordinator.Confirmed);
        Assert.Empty(coordinator.CancelledAll);
        Assert.Equal(0, batcher.DeferredCount);
    }

    [Fact]
    public async Task Batcher_SubmitThrowsButTxLaterMines_NoDoubleExecute()
    {
        var gateway = new ScriptedGateway();
        gateway.SubmitThrows = true; // RPC accepted the tx, only the response was lost
        for (var i = 0; i < 3; i++) gateway.StatusSequence.Enqueue(TxStatus.Pending);
        gateway.StatusSequence.Enqueue(TxStatus.Confirmed);
        gateway.PendingTx = true;

        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator, reconcileIntervalMs: 10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));

        for (var i = 0; i < 2; i++) batcher.Enqueue(Match(i));

        await WaitUntilAsync(() => coordinator.Confirmed.Count == 1, TimeSpan.FromSeconds(8));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        // The submission exception did NOT unwind: the accepted tx was located by batchId and
        // confirmed when it mined — reservation never released prematurely, no double execution.
        Assert.Single(coordinator.Confirmed);
        Assert.Empty(coordinator.CancelledAll);
        Assert.Empty(coordinator.Repaired);
        Assert.Equal(1, gateway.SettlementSubmits);
    }

    [Fact]
    public async Task Batcher_StillPendingPastOldExpiry_LaterMines_NoDoubleExecute()
    {
        var gateway = new ScriptedGateway();
        gateway.Outcomes.Enqueue(new SettlementReceipt(TxStatus.Unknown, null)); // initial submit timeout
        // Far more pending re-checks than the OLD 30-check expiry cap, then the tx finally mines.
        for (var i = 0; i < 40; i++) gateway.StatusSequence.Enqueue(TxStatus.Pending);
        gateway.StatusSequence.Enqueue(TxStatus.Confirmed);
        gateway.PendingTx = true; // the tx stays in the mempool the whole time

        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator, reconcileIntervalMs: 10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));

        for (var i = 0; i < 2; i++) batcher.Enqueue(Match(i));

        await WaitUntilAsync(() => coordinator.Confirmed.Count == 1, TimeSpan.FromSeconds(8));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        // 40 pending checks (> the old 30-check expiry) with NO unwind: the late-mined tx is
        // confirmed, its reservation was never prematurely released (no CancelAll/Repair).
        Assert.Single(coordinator.Confirmed);
        Assert.Empty(coordinator.CancelledAll);
        Assert.Empty(coordinator.Repaired);
    }

    [Fact]
    public async Task Batcher_BatchInFlightWhenResolutionInitiates_DoesNotSettle()
    {
        var gateway = new ScriptedGateway();
        var core = new VenueCore(TestData.Cfg, gateway, new NullEventSink());
        var market = Hash.NormalizeBytes32("0xEEEE");
        await core.ApplyEventsAsync(new VenueEvent[]
        {
            new MarketCreated(TestData.Ot, 1, 0, "0x", market, Array.Empty<byte>()),
            new Deposited(TestData.Vault, 2, 0, "0x", TestData.Alice, 10_000_000),
            new Deposited(TestData.Vault, 3, 0, "0x", TestData.Bob, 10_000_000),
            new TokensDeposited(TestData.Vault, 4, 0, "0x", TestData.Alice, Assets.TokenId(market, Outcome.Yes), 10_000_000),
        });

        await core.PlaceOrderAsync(new OrderRequest(TestData.Alice, market, Outcome.Yes, OrderSide.Sell, 100_000, 600, OrderType.Limit, "s1", null));
        var buy = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, market, Outcome.Yes, OrderSide.Buy, 100_000, 600, OrderType.Limit, "b1", null));
        Assert.Equal(1, core.PendingSettlements);

        // The batcher has already dequeued this batch (out of the queue, about to submit) when
        // resolution initiates. Market is still open, so the seal lets it through on this check.
        var dequeued = await core.UnwindClosedAsync(buy.Fills.ToList());
        Assert.Single(dequeued);

        // Resolution initiates: market becomes Closing, the queue is drained.
        await core.ResolveMarketAsync(market, Outcome.Yes);
        Assert.Equal(0, core.PendingSettlements);

        // The settle-time seal aborts the already-dequeued batch — nothing settles against Closing.
        var afterClose = await core.UnwindClosedAsync(dequeued);
        Assert.Empty(afterClose);
        Assert.Null(await core.GetOrderAsync(buy.Order.OrderId));
        var bobBal = await core.GetBalancesAsync(TestData.Bob);
        Assert.Equal(new BigInteger(10_000_000), bobBal.Available);

        // Intake rejects while Closing (before the MarketResolved event is even indexed).
        var rejected = await core.PlaceOrderAsync(new OrderRequest(TestData.Bob, market, Outcome.No, OrderSide.Buy, 1000, 400, OrderType.Limit, "s2", null));
        Assert.Equal(OrderStatus.Rejected, rejected.TerminalStatus);
        Assert.Equal("market_closing", rejected.RejectReason);
    }

    [Fact]
    public void Engine_RejectsNonExistentMarket()
    {
        var ledger = TestData.NewLedger();
        TestData.SeedUsdc(ledger, TestData.Bob, 10_000);
        var engine = TestData.NewEngine(ledger, "0x" + new string('f', 64)); // a DIFFERENT registered market
        var ghostMarket = TestData.MarketFor("0x" + new string('9', 64));    // not in the engine's map

        var result = engine.Place(new OrderRequest(TestData.Bob, ghostMarket.MarketId, Outcome.Yes, OrderSide.Buy, 100, 500, OrderType.Limit, "g1", null), ghostMarket);
        Assert.Equal(OrderStatus.Rejected, result.TerminalStatus);
        Assert.Equal("market_not_found", result.RejectReason);
    }

    // ------------------------------------------------------------ timeout reconciliation

    [Fact]
    public async Task Batcher_TimeoutThatLaterMines_ConfirmsInsteadOfCancelling()
    {
        var gateway = new ScriptedGateway();
        gateway.Outcomes.Enqueue(new SettlementReceipt(TxStatus.Unknown, null)); // AwaitSettlement times out
        gateway.TxStatusOverride = TxStatus.Confirmed;                           // ...then it mines

        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));

        for (var i = 0; i < 2; i++) batcher.Enqueue(Match(i));

        await WaitUntilAsync(() => coordinator.Confirmed.Count == 1, TimeSpan.FromSeconds(8));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.Single(coordinator.Confirmed);
        Assert.Empty(coordinator.CancelledAll); // never treated as cancelled
        Assert.Empty(coordinator.Repaired);
    }

    [Fact]
    public async Task Batcher_TimeoutThatIsProvablyDropped_Unwinds()
    {
        var gateway = new ScriptedGateway();
        gateway.Outcomes.Enqueue(new SettlementReceipt(TxStatus.Unknown, null)); // AwaitSettlement times out
        gateway.TxStatusOverride = TxStatus.Pending;                              // still "pending" on re-check
        gateway.PendingTx = false;                                                // ...but not in the mempool -> replaced/expired

        var coordinator = new RecordingCoordinator();
        var batcher = new SettlementBatcher(gateway, coordinator, TestData.Operator);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => batcher.RunAsync(cts.Token));

        for (var i = 0; i < 2; i++) batcher.Enqueue(Match(i));

        await WaitUntilAsync(() => coordinator.CancelledAll.Count == 1, TimeSpan.FromSeconds(8));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.Single(coordinator.CancelledAll);
        Assert.Empty(coordinator.Confirmed);
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
            var hash = "0x" + SettlementSubmits.ToString("x");
            BatchTx[Hash.NormalizeBytes32(batchId)] = hash;
            if (SubmitThrows)
                throw new InvalidOperationException("rpc response lost"); // tx WAS recorded, response not returned
            return Task.FromResult(hash);
        }

        public Task<SettlementTxLookup> FindPendingSettlementAsync(string batchId, CancellationToken ct)
        {
            if (FindState == SettlementTxState.Unknown) return Task.FromResult(new SettlementTxLookup(SettlementTxState.Unknown, null));
            if (FindState == SettlementTxState.NotSubmitted) return Task.FromResult(new SettlementTxLookup(SettlementTxState.NotSubmitted, null));
            return Task.FromResult(BatchTx.TryGetValue(Hash.NormalizeBytes32(batchId), out var h)
                ? new SettlementTxLookup(SettlementTxState.Submitted, h)
                : new SettlementTxLookup(SettlementTxState.NotSubmitted, null));
        }

        public bool SubmitThrows { get; set; }
        public SettlementTxState FindState { get; set; } = SettlementTxState.Submitted;
        public Dictionary<string, string> BatchTx { get; } = new();

        public Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct)
            => Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : new SettlementReceipt(TxStatus.Confirmed, null));

        public Task<bool> IsTransactionPendingAsync(string txHash, CancellationToken ct)
            => Task.FromResult(PendingTx);

        public Task<BatchRevertInfo?> TryGetRevertAsync(string txHash, CancellationToken ct)
            => Task.FromResult(LatestRevert);

        public bool PendingTx { get; set; }
        public BatchRevertInfo? LatestRevert { get; set; }

        public Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct) => Task.FromResult("0xfin");
        public Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct) => Task.FromResult("0xresolve");
        public Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();

        public Task<BigInteger> GetRequestCountAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitMintUsdcAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<BigInteger> GetUsdcWalletBalanceAsync(string user, CancellationToken ct) => throw new NotImplementedException();

        public Task FundGasAsync(string address, CancellationToken ct) => Task.CompletedTask;
        public Task SubmitCreateMarketAsync(string marketId, byte[] meta, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct)
            => Task.FromResult(StatusSequence.Count > 0 ? StatusSequence.Dequeue() : TxStatusOverride);

        public TxStatus TxStatusOverride { get; set; } = TxStatus.Pending;
        public Queue<TxStatus> StatusSequence { get; } = new();
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

        public Task<IReadOnlyList<MatchedTrade>> UnwindClosedAsync(IReadOnlyList<MatchedTrade> matches)
        {
            Unwound.Add(matches);
            return Task.FromResult<IReadOnlyList<MatchedTrade>>(CloseAllOnUnwind ? Array.Empty<MatchedTrade>() : matches);
        }

        public bool CloseAllOnUnwind { get; set; }
        public List<IReadOnlyList<MatchedTrade>> Unwound { get; } = new();
    }
}
