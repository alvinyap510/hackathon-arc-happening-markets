using System.Numerics;
using Venue.Broadcasting;
using Venue.Chain;
using Venue.Domain;
using Venue.Engine;
using Venue.Indexing;
using Venue.Infrastructure;
using Venue.Ledger;
using Venue.Rfm;
using Venue.Settlement;
using LedgerImpl = Venue.Ledger.Ledger;
using TradingEngine = Venue.Engine.Engine;

namespace Venue;

/// <summary>
/// The venue core: one process, five modules (STATE · ENGINE · SETTLEMENT · RFM ·
/// API), all serialized through a single AsyncGate. The ledger is a best-effort cache
/// for admission + display; the on-chain contract is the solvency guard. Chain I/O
/// (log fetch, settlement submit, finalize) happens OUTSIDE the gate; outcomes are
/// re-applied under the gate. Restart = replay events from the deploy block, discard
/// volatile orders, bump book generations.
/// </summary>
public sealed class VenueCore : ISettlementCoordinator, IAsyncDisposable
{
    private readonly ChainConfig _cfg;
    private readonly IChainGateway _gateway;
    private IEventSink _sink;
    private readonly AsyncGate _gate = new();
    private readonly Dictionary<string, Market> _markets = new();
    private readonly LedgerImpl _ledger;
    private readonly TradingEngine _engine;
    private readonly RfmCoordinator _rfm;
    private readonly SettlementBatcher _batcher;
    private readonly EventIndexer _indexer;
    private CancellationTokenSource? _cts;
    private Task? _loops;

    public VenueCore(ChainConfig cfg, IChainGateway gateway, IEventSink sink, int indexerPollIntervalMs = 2000)
    {
        _cfg = cfg;
        _gateway = gateway;
        _sink = sink;
        _ledger = new LedgerImpl(cfg.NormalizedVault, ResolvedOutcome);
        _engine = new TradingEngine(_ledger, _markets);
        _rfm = new RfmCoordinator(gateway);
        _batcher = new SettlementBatcher(gateway, this, cfg.OperatorAddress);
        _indexer = new EventIndexer(gateway, ApplyEventsAsync, ReplayResetAsync, cfg.StartBlock, indexerPollIntervalMs);
    }

    public ChainConfig Config => _cfg;
    public IChainGateway Gateway => _gateway;
    public int PendingSettlements => _batcher.PendingCount;

    /// <summary>Attach the broadcast sink (the WS hub) after construction — the hub needs the core.</summary>
    public void SetSink(IEventSink sink) => _sink = sink;

    private Outcome? ResolvedOutcome(string marketId)
        => _markets.TryGetValue(Infrastructure.Hash.NormalizeBytes32(marketId), out var m) ? m.WinningOutcome : null;

    private static BigInteger Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds() < 0 ? BigInteger.Zero : new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Replay from the deploy block, then start the indexer, batcher and RFM crank.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        // The replay is resilient (backoff inside EventIndexer.ReplayAsync) and normally
        // completes before we return. If it STILL fails for an unexpected reason, do NOT let
        // it take the whole service down: the poll loop retries and catches up, and the API
        // serves on the (partial) ledger meanwhile. The ledger is a cache - the Vault is the
        // solvency guard, so a stale indexer is safe, not fatal.
        try
        {
            await _indexer.ReplayAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"indexer: initial replay failed: {ex.Message}; continuing - poll loop will catch up");
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loops = Task.Run(() => Task.WhenAll(
            _indexer.RunAsync(_cts.Token),
            _batcher.RunAsync(_cts.Token),
            RfmCrankLoopAsync(_cts.Token)), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loops != null)
        {
            try { await _loops; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    /// <summary>Restart semantics: replay from the deploy block, discard volatile state, bump generations.</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        await _indexer.ReplayAsync(ct); // ReplayResetAsync clears ledger/markets/rfm/engine/queue first
        _engine.BumpAllGenerations();
        _sink.GenerationBump();
    }

    /// <summary>Cleared by the indexer before every replay: a replay REBUILDS from events, so no
    /// derived state may survive into it (double-apply is the bug being prevented).</summary>
    private Task ReplayResetAsync() => _gate.RunAsync(() =>
    {
        _markets.Clear();
        _rfm.Clear();
        _engine.ResetForRestart();
        _ledger.ResetForReplay();
        _batcher.ClearQueue();
        return Task.CompletedTask;
    });

    private async Task RfmCrankLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<BigInteger> ready = Array.Empty<BigInteger>();
                await _gate.RunAsync(() =>
                {
                    ready = _rfm.ReadyToFinalize(Now);
                    return Task.CompletedTask;
                });
                await _rfm.CrankAsync(ready, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"rfm: crank failed: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    // ------------------------------------------------------------------ events

    /// <summary>Apply a batch of events in order under the gate (indexer + restart).</summary>
    public async Task ApplyEventsAsync(IReadOnlyList<VenueEvent> events)
    {
        await _gate.RunAsync(() =>
        {
            foreach (var e in events)
            {
                UpdateMarket(e);
                _rfm.Apply(e);
                _ledger.Apply(e);
                SweepFor(e);
                Broadcast(e);
            }
            return Task.CompletedTask;
        });
    }

    private void UpdateMarket(VenueEvent e)
    {
        switch (e)
        {
            case MarketReserved r:
                GetOrCreate(r.MarketId).Reserved = true;
                break;
            case MarketCreated c:
                GetOrCreate(c.MarketId).Exists = true;
                break;
            case MarketResolved r:
                var m = GetOrCreate(r.MarketId);
                m.Resolved = true;
                m.Closing = true;
                m.WinningOutcome = r.Outcome;
                CloseMarketForResolution(r.MarketId);
                break;
            case MarketBorn b:
                var market = GetOrCreate(b.MarketId);
                market.Exists = true;
                market.BornMarginalYesTick = (long)b.MarginalYesTick;
                market.BornVwapYesTick = (long)b.VwapYesTick;
                market.BornFilledQuantity = b.FilledQuantity;
                market.BornSide = b.Side;
                market.BornRequestId = b.RequestId;
                market.BookGeneration++;
                break;
        }
    }

    private Market GetOrCreate(string marketId)
    {
        var key = Infrastructure.Hash.NormalizeBytes32(marketId);
        if (!_markets.TryGetValue(key, out var m))
        {
            m = new Market { MarketId = key };
            _markets[key] = m;
        }
        return m;
    }

    private Market RequireMarket(string marketId)
    {
        var key = Infrastructure.Hash.NormalizeBytes32(marketId);
        return _markets.TryGetValue(key, out var m) ? m : throw new KeyNotFoundException($"market {marketId} does not exist");
    }

    private void SweepFor(VenueEvent e)
    {
        string? user = e switch
        {
            Withdrawn w => w.User,
            Locked l => l.User,
            _ => null,
        };
        if (user == null) return;
        foreach (var order in _engine.InsolvencySweep(user))
            BroadcastCancelled(order);
    }

    /// <summary>
    /// Close a market for trading (resolution gate): a resolved market must not settle stale
    /// fills — drain its queued settlement fills and release their orders' reservations, then
    /// cancel the whole book. Idempotent; called on the MarketResolved event AND before the
    /// operator's resolve tx so a stale fill can never cross the resolve boundary.
    /// </summary>
    private void CloseMarketForResolution(string marketId)
    {
        foreach (var pending in _batcher.DrainForMarket(marketId))
        {
            _engine.UnwindOrder(pending.MakerOrderId);
            _engine.UnwindOrder(pending.TakerOrderId);
            _sink.SettlementOutcome(marketId, "", TxStatus.Reverted, "market_resolved", new[] { pending.Trade.TradeId });
        }
        foreach (var order in _engine.CancelMarket(marketId))
            BroadcastCancelled(order);
        _sink.BookChanged(marketId);
    }

    private void BroadcastCancelled(Order order)
    {
        _sink.OrderUpdated(order.User, order.OrderId, OrderStatus.Cancelled.ToString().ToLowerInvariant());
        _sink.BookChanged(order.MarketId);
        _sink.BalanceChanged(order.User);
    }

    private void Broadcast(VenueEvent e)
    {
        switch (e)
        {
            case Deposited d: _sink.BalanceChanged(d.User); break;
            case Withdrawn w: _sink.BalanceChanged(w.User); break;
            case TokensDeposited d: _sink.BalanceChanged(d.User); break;
            case TokensWithdrawn w: _sink.BalanceChanged(w.User); break;
            case USDCMoved m: _sink.BalanceChanged(m.From); _sink.BalanceChanged(m.To); break;
            case TokensMoved m: _sink.BalanceChanged(m.From); _sink.BalanceChanged(m.To); break;
            case Locked l: _sink.BalanceChanged(l.User); break;
            case LockReleased r: _sink.BalanceChanged(r.User); break;
            case LockConsumed c: _sink.BalanceChanged(c.User); _sink.BalanceChanged(c.To); break;
            case PairMinted pm:
                foreach (var f in pm.Funding) if (f.Kind == FundingKind.Free) _sink.BalanceChanged(f.Account);
                foreach (var a in pm.YesAlloc) _sink.BalanceChanged(a.Account);
                foreach (var a in pm.NoAlloc) _sink.BalanceChanged(a.Account);
                break;
            case PairBurned pb: _sink.BalanceChanged(pb.YesFrom); _sink.BalanceChanged(pb.NoFrom); break;
            case Redeemed rd when rd.Contract == _cfg.NormalizedVault: _sink.BalanceChanged(rd.User); break;
            case MarketBorn b: _sink.MarketBorn(b.MarketId); _sink.BookChanged(b.MarketId); break;
            case MarketResolved r: _sink.BookChanged(r.MarketId); break;
            case RequestPosted p: _sink.RfmChanged(p.RequestId); break;
            case QuoteCommitted q: _sink.RfmChanged(q.RequestId); break;
            case QuoteRevealed q: _sink.RfmChanged(q.RequestId); break;
            case RfmFill f: _sink.RfmChanged(f.RequestId); break;
            case RequestFinalized f: _sink.RfmChanged(f.RequestId); break;
            case RequestFailed f: _sink.RfmChanged(f.RequestId); break;
            case RequestCancelled c: _sink.RfmChanged(c.RequestId); break;
            case BondSlashed s: _sink.RfmChanged(s.RequestId); break;
            case RfmMarketReserved r: _sink.RfmChanged(r.RequestId); break;
        }
    }

    // ---------------------------------------------------------------- trading

    public async Task<PlaceResult> PlaceOrderAsync(OrderRequest req)
    {
        return await _gate.RunAsync(() =>
        {
            var market = RequireMarket(req.MarketId);
            var result = _engine.Place(req, market);
            var status = result.TerminalStatus.ToString().ToLowerInvariant();

            if (result.TerminalStatus == OrderStatus.Rejected)
            {
                _sink.OrderUpdated(req.User, result.Order.OrderId, status);
                return Task.FromResult(result);
            }

            if (result.Fills.Count > 0)
            {
                foreach (var fill in result.Fills) _batcher.Enqueue(fill);
                _sink.Fills(market.MarketId, result.Fills.Select(f => f.Trade).ToList());
                _sink.BookChanged(market.MarketId);
            }
            _sink.OrderUpdated(req.User, result.Order.OrderId, status);
            _sink.BalanceChanged(req.User);
            return Task.FromResult(result);
        });
    }

    public async Task<CancelResult> CancelOrderAsync(string orderId)
    {
        return await _gate.RunAsync(() =>
        {
            var order = _engine.GetOrder(orderId);
            var result = _engine.Cancel(orderId);
            if (result.Cancelled && order != null)
            {
                _sink.OrderUpdated(order.User, order.OrderId, OrderStatus.Cancelled.ToString().ToLowerInvariant());
                _sink.BookChanged(order.MarketId);
                _sink.BalanceChanged(order.User);
            }
            return Task.FromResult(result);
        });
    }

    // -------------------------------------------------------------- reads (gated)

    public Task<BookSnapshot> GetBookAsync(string marketId)
        => _gate.RunAsync(() => Task.FromResult(_engine.BookSnapshot(RequireMarket(marketId).MarketId)));

    public Task<UserBalances> GetBalancesAsync(string user)
        => _gate.RunAsync(() =>
        {
            var u = Domain.Addresses.Normalize(user);
            var positions = _markets.Keys.SelectMany(m =>
            {
                var outList = new List<PositionBalance>();
                var yes = Assets.TokenId(m, Outcome.Yes);
                var no = Assets.TokenId(m, Outcome.No);
                var y = _ledger.Position(u, yes);
                var n = _ledger.Position(u, no);
                if (y > 0) outList.Add(new PositionBalance(yes, m, Outcome.Yes, y, _ledger.Reserved(u, yes)));
                if (n > 0) outList.Add(new PositionBalance(no, m, Outcome.No, n, _ledger.Reserved(u, no)));
                return outList;
            }).ToList();
            var free = _ledger.ChainFree(u);
            var reserved = _ledger.Reserved(u, Assets.Usdc);
            return Task.FromResult(new UserBalances(u, free, reserved, _ledger.Available(u, Assets.Usdc), positions));
        });

    public Task<IReadOnlyList<Market>> GetMarketsAsync()
        => _gate.RunAsync(() => Task.FromResult<IReadOnlyList<Market>>(_markets.Values.OrderBy(m => m.MarketId).ToList()));

    public Task<Market> GetMarketAsync(string marketId)
        => _gate.RunAsync(() => Task.FromResult(RequireMarket(marketId)));

    public Task<IReadOnlyList<Order>> GetOrdersAsync(string user, OrderStatus? status = null)
        => _gate.RunAsync(() => Task.FromResult<IReadOnlyList<Order>>(_engine.OrdersFor(user, status)));

    public Task<Order?> GetOrderAsync(string orderId)
        => _gate.RunAsync(() => Task.FromResult(_engine.GetOrder(orderId)));

    public Task<IReadOnlyList<RfmRequestMirror>> GetRfmRequestsAsync()
        => _gate.RunAsync(() => Task.FromResult<IReadOnlyList<RfmRequestMirror>>(_rfm.Requests));

    public Task<RfmRequestMirror?> GetRfmRequestAsync(BigInteger requestId)
        => _gate.RunAsync(() => Task.FromResult(_rfm.Get(requestId)));

    public Task<BigInteger?> RfmRequestForMarketAsync(string marketId)
        => _gate.RunAsync(() => Task.FromResult(_rfm.RequestForMarket(marketId)));

    // ------------------------------------------------------- settlement hooks

    /// <summary>
    /// Operator resolution with the resolution gate: mark the market CLOSING (so nothing can be
    /// admitted or settled against it), close the book + drain pending fills, let any in-flight
    /// batch abort via the settle-time seal, THEN submit the resolve tx. The MarketResolved event
    /// re-closes the market idempotently when it lands.
    /// </summary>
    public async Task ResolveMarketAsync(string marketId, Outcome outcome)
    {
        await _gate.RunAsync(() =>
        {
            var market = RequireMarket(marketId);
            if (market.Resolved) throw new InvalidOperationException("market already resolved");
            market.Closing = true; // closes the intake + settle window BEFORE the on-chain resolve
            CloseMarketForResolution(marketId);
            return Task.CompletedTask;
        });
        await _batcher.AwaitIdleAsync(CancellationToken.None);
        await _gateway.SubmitResolveAsync(marketId, outcome, CancellationToken.None);
    }

    public async Task<IReadOnlyList<MatchedTrade>> UnwindClosedAsync(IReadOnlyList<MatchedTrade> matches)
    {
        return await _gate.RunAsync(() =>
        {
            var open = new List<MatchedTrade>();
            foreach (var m in matches)
            {
                var key = Infrastructure.Hash.NormalizeBytes32(m.Trade.MarketId);
                var closed = !_markets.TryGetValue(key, out var mk) || mk.Closing || mk.Resolved;
                if (closed)
                {
                    _engine.UnwindOrder(m.MakerOrderId);
                    _engine.UnwindOrder(m.TakerOrderId);
                    _sink.SettlementOutcome(m.Trade.MarketId, "", TxStatus.Reverted, "market_closed", new[] { m.Trade.TradeId });
                    _sink.BookChanged(m.Trade.MarketId);
                }
                else
                {
                    open.Add(m);
                }
            }
            return Task.FromResult<IReadOnlyList<MatchedTrade>>(open);
        });
    }

    public async Task ConfirmBatchAsync(string batchId, IReadOnlyList<MatchedTrade> matches)
    {
        await _gate.RunAsync(() =>
        {
            _engine.OnBatchConfirmed(matches);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var m in matches)
            {
                var market = GetOrCreate(m.Trade.MarketId);
                market.Trades.Add(new TradeRecord(m.Trade.TradeId, m.MakerOrderId, m.TakerOrderId, m.Trade.Class, m.Size, m.Trade.OutcomeTick, now, batchId));
            }
            var ids = matches.Select(m => m.Trade.TradeId).ToList();
            foreach (var g in matches.GroupBy(m => m.Trade.MarketId))
            {
                _sink.SettlementOutcome(g.Key, batchId, TxStatus.Confirmed, null, ids);
                _sink.BookChanged(g.Key);
            }
            foreach (var u in matches.SelectMany(m => new[] { m.Maker.User, m.Taker.User }).Distinct())
            {
                _sink.BalanceChanged(u);
                _sink.OrderUpdated(u, "", "settled");
            }
            return Task.CompletedTask;
        });
    }

    public async Task RepairBatchAsync(string batchId, IReadOnlyList<MatchedTrade> matches, BatchRevertInfo revert)
    {
        await _gate.RunAsync(() =>
        {
            var failing = matches[revert.FailIndex!.Value];
            // UnwindOrder (not Cancel): a fully-matched order is Filled, not Resting, and its
            // reservation must still be released when its fill fails on chain.
            _engine.UnwindOrder(failing.MakerOrderId);
            _engine.UnwindOrder(failing.TakerOrderId);
            _sink.SettlementOutcome(failing.Trade.MarketId, batchId, TxStatus.Reverted, revert.ErrorName, new[] { failing.Trade.TradeId });
            _sink.BookChanged(failing.Trade.MarketId);
            _sink.OrderUpdated(failing.Maker.User, failing.MakerOrderId, "rejected");
            _sink.OrderUpdated(failing.Taker.User, failing.TakerOrderId, "rejected");
            return Task.CompletedTask;
        });
    }

    public async Task CancelAllOrdersAsync(IReadOnlyList<MatchedTrade> matches, string reason)
    {
        await _gate.RunAsync(() =>
        {
            foreach (var m in matches)
            {
                _engine.UnwindOrder(m.MakerOrderId);
                _engine.UnwindOrder(m.TakerOrderId);
            }
            var ids = matches.Select(m => m.Trade.TradeId).ToList();
            foreach (var g in matches.GroupBy(m => m.Trade.MarketId))
            {
                _sink.SettlementOutcome(g.Key, "", TxStatus.Reverted, reason, ids);
                _sink.BookChanged(g.Key);
            }
            return Task.CompletedTask;
        });
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
