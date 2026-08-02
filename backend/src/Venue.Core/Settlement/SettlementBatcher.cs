using System.Collections.Concurrent;
using Venue.Chain;
using Venue.Domain;
using Venue.Engine;
using Venue.Infrastructure;

namespace Venue.Settlement;

/// <summary>
/// Settlement batcher (PLAN_BACKEND §3): flush at ~500 ms or 8 trades (whichever first),
/// one whole batch per chain tx, whole-batch atomic, revert = repair. tradeIds are
/// deterministic (hash of market + order ids + fill seq); a NEW batchId is minted per
/// attempt so a repaired resubmission can never collide with a used batch id. One batch
/// in flight at a time keeps the operator nonce serial.
///
/// Revert repair: the contract's SettleBatchFailed(index, tradeId) names the failing
/// trade; we cancel its two orders (under the gate) and resubmit the rest under a new
/// batchId. Attribution unclear or attempts exhausted → cancel all, re-cross. Reverts are
/// rare (the cache is usually right) and cheap.
/// </summary>
public sealed class SettlementBatcher
{
    public const int MaxBatch = 8;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private const int MaxRepairAttempts = 3;
    private readonly TimeSpan _reconcileInterval = TimeSpan.FromSeconds(1);

    private readonly IChainGateway _gateway;
    private readonly ISettlementCoordinator _core;
    private readonly string _operatorAddress;
    private readonly ConcurrentQueue<MatchedTrade> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly object _deferredLock = new();
    private readonly List<(string BatchId, List<MatchedTrade> Matches)> _deferred = new();
    private long _batchAttempt;
    private int _busy; // 1 while a batch is submitted/awaiting; read by AwaitIdleAsync

    public SettlementBatcher(IChainGateway gateway, ISettlementCoordinator core, string operatorAddress, int reconcileIntervalMs = 1000)
    {
        _gateway = gateway;
        _core = core;
        _operatorAddress = operatorAddress;
        _reconcileInterval = TimeSpan.FromMilliseconds(reconcileIntervalMs);
    }

    public int PendingCount => _queue.Count;

    /// <summary>Batches whose submission lookup is inconclusive — reservations HELD, never unwound.</summary>
    public int DeferredCount
    {
        get { lock (_deferredLock) return _deferred.Count; }
    }

    public void Enqueue(MatchedTrade match)
    {
        _queue.Enqueue(match);
        _signal.Release();
    }

    /// <summary>Remove every queued (not-yet-submitted) fill for a market, preserving the
    /// relative order of the rest. Used on market resolution so no matched fill outlives
    /// the market's birth → resolve transition.</summary>
    public IReadOnlyList<MatchedTrade> DrainForMarket(string marketId)
    {
        var kept = new List<MatchedTrade>();
        var removed = new List<MatchedTrade>();
        while (_queue.TryDequeue(out var m))
        {
            if (m.Trade.MarketId == marketId) removed.Add(m);
            else kept.Add(m);
        }
        foreach (var m in kept) _queue.Enqueue(m);
        return removed;
    }

    /// <summary>Discard every queued fill (restart/replay: orders are gone, so a stale fill
    /// must never be submitted).</summary>
    public void ClearQueue()
    {
        while (_queue.TryDequeue(out _)) { }
    }

    /// <summary>Wait until no batch is currently submitted/awaiting on chain (resolution gate).</summary>
    public async Task AwaitIdleAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _busy) != 0)
            await Task.Delay(100, ct);
    }

    /// <summary>The settlement loop; run as a background task from the host. The busy flag is
    /// set BEFORE dequeue and cleared after the batch is terminal, so a resolution gate awaiting
    /// idle covers the whole dequeue+submit window (no drained-but-not-busy gap).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _busy, 1);
            List<MatchedTrade> batch;
            try
            {
                await ReconcileDeferredAsync(ct); // resolve unknown-lookup batches before new work
                batch = await DrainAsync(ct);
                if (batch.Count > 0) await SubmitWithRepairAsync(batch, ct);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
            if (batch.Count == 0)
                await Task.Delay(FlushInterval, ct);
        }
    }

    private async Task<List<MatchedTrade>> DrainAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(FlushInterval);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_queue.Count >= MaxBatch) break;
            var remainingMs = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (remainingMs <= 0) break;
            try
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(remainingMs, 500)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var batch = new List<MatchedTrade>(MaxBatch);
        while (batch.Count < MaxBatch && _queue.TryDequeue(out var match))
            batch.Add(match);
        return batch;
    }

    private async Task SubmitWithRepairAsync(List<MatchedTrade> batch, CancellationToken ct)
        => await SubmitWithRepairCoreAsync(batch, ct);

    private async Task SubmitWithRepairCoreAsync(List<MatchedTrade> batch, CancellationToken ct)
    {
        var remaining = batch;
        for (var attempt = 0; attempt <= MaxRepairAttempts && remaining.Count > 0; attempt++)
        {
            // Settlement race seal: a market that became Closing/Resolved since these fills were
            // matched must never settle them — unwind them and continue with the open remainder.
            remaining = (await _core.UnwindClosedAsync(remaining)).ToList();
            if (remaining.Count == 0) return;

            var batchId = Hash.BatchId(_operatorAddress, ++_batchAttempt);
            string? txHash = null;
            SettlementReceipt outcome;
            try
            {
                txHash = await _gateway.SubmitSettlementAsync(batchId, remaining.Select(m => m.Trade).ToList(), ct);
                outcome = await _gateway.AwaitSettlementAsync(txHash, ct);
            }
            catch (Exception ex)
            {
                // The RPC may have ACCEPTED the tx and lost only the response. Locate it — but a
                // lookup can be INCONCLUSIVE (RPC error / partial scan): only a DEFINITIVE
                // NotSubmitted lets us unwind. Unknown keeps the batch deferred and its
                // reservations HELD until the lookup resolves — never release on a guess.
                var lookup = await _gateway.FindPendingSettlementAsync(batchId, ct);
                switch (lookup.State)
                {
                    case SettlementTxState.Submitted:
                        txHash = lookup.TxHash;
                        outcome = await ReconcileAsync(lookup.TxHash!, ct);
                        break;
                    case SettlementTxState.NotSubmitted:
                        // definitively not submitted -> safe to unwind
                        txHash = null;
                        outcome = new SettlementReceipt(TxStatus.Unknown, null);
                        break;
                    default:
                        // Unknown: could not determine -> keep the tx tracked, reservations held,
                        // retry the lookup on later loop iterations. NEVER unwind.
                        Console.WriteLine($"settle: submit raised {ex.Message}; lookup inconclusive -> deferred");
                        lock (_deferredLock) _deferred.Add((batchId, remaining.ToList()));
                        return;
                }
            }

            if (outcome.Status == TxStatus.Confirmed)
            {
                await _core.ConfirmBatchAsync(batchId, remaining);
                return;
            }

            // A submit TIMEOUT is NOT a cancellation: reconcile by receipt + mempool liveness
            // before unwinding, so a tx that later mines is confirmed (never double-executed).
            if (outcome.Status == TxStatus.Unknown && txHash != null)
                outcome = await ReconcileAsync(txHash, ct);

            if (outcome.Status == TxStatus.Confirmed)
            {
                await _core.ConfirmBatchAsync(batchId, remaining);
                return;
            }

            if (outcome.Status == TxStatus.Reverted && outcome.Revert is { FailIndex: int idx } && idx < remaining.Count && idx >= 0)
            {
                // Pass a snapshot: the coordinator runs under the gate and must not see the
                // list mutate when the failing trade is removed below.
                await _core.RepairBatchAsync(batchId, remaining.ToList(), outcome.Revert);
                remaining.RemoveAt(idx);
                continue;
            }

            // Non-SettleBatchFailed revert, attribution unclear, or a tx that was provably
            // never mined → cancel all, re-cross.
            await _core.CancelAllOrdersAsync(remaining, outcome.Revert?.ErrorName ?? (txHash == null ? "submit_failed" : "unknown_revert"));
            return;
        }

        // Repair attempts exhausted: nothing left to resubmit safely → unwind the remainder.
        if (remaining.Count > 0)
            await _core.CancelAllOrdersAsync(remaining, "repair_attempts_exhausted");
    }

    /// <summary>
    /// Reconcile a timed-out submission. NEVER unwinds a tx that could still mine: keep waiting
    /// until it is confirmed, reverted, or PROVABLY not-going-to-mine (its nonce was consumed by a
    /// different tx, or the node dropped/replaced it). A still-pending-unknown tx stays reserved —
    /// it is never declared expired on a wall-clock timer, so a late mine cannot double-execute
    /// against a replacement order.
    /// </summary>
    private async Task<SettlementReceipt> ReconcileAsync(string txHash, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(_reconcileInterval, ct); // throws OperationCanceledException on shutdown
            var status = await _gateway.TxStatusAsync(txHash, ct);
            if (status == TxStatus.Confirmed) return new SettlementReceipt(TxStatus.Confirmed, null);
            if (status == TxStatus.Reverted)
            {
                var revert = await _gateway.TryGetRevertAsync(txHash, ct);
                return new SettlementReceipt(TxStatus.Reverted, revert);
            }
            if (!await _gateway.IsTransactionPendingAsync(txHash, ct))
            {
                Console.WriteLine($"settle: tx {txHash} provably not mining (replaced/expired) -> unwind");
                return new SettlementReceipt(TxStatus.Unknown, null);
            }
            // still pending: it could still mine -> keep waiting, keep the reservations held
        }
    }

    /// <summary>
    /// Retry the lookup for batches whose submission outcome is Unknown (inconclusive). The tx is
    /// resolved to a DEFINITIVE terminal state FIRST — an Unknown (possibly-mining) tx is NEVER
    /// released, not even when its market closes/resolves. Only a terminally-not-mined tx
    /// (reverted / provably dropped / NotSubmitted) may be unwound; a mined one is confirmed.
    /// Closing a market cancels RESTING orders, but an already-submitted Unknown settlement tx is
    /// resolved to terminal before its reservations can ever be released.
    /// </summary>
    private async Task ReconcileDeferredAsync(CancellationToken ct)
    {
        List<(string BatchId, List<MatchedTrade> Matches)> snapshot;
        lock (_deferredLock) snapshot = _deferred.ToList();

        foreach (var (batchId, matches) in snapshot)
        {
            var lookup = await _gateway.FindPendingSettlementAsync(batchId, ct);
            switch (lookup.State)
            {
                case SettlementTxState.Submitted:
                {
                    var outcome = await ReconcileAsync(lookup.TxHash!, ct);
                    if (outcome.Status == TxStatus.Confirmed)
                        await _core.ConfirmBatchAsync(batchId, matches); // mined -> settle it
                    else if (outcome.Status == TxStatus.Reverted)
                    {
                        // definitively not-mined (reverted, nothing consumed): re-queue for a fresh
                        // attempt; the next submit's seal unwinds any closed-market matches.
                        foreach (var m in matches) { _queue.Enqueue(m); _signal.Release(); }
                    }
                    else
                    {
                        await _core.CancelAllOrdersAsync(matches, "dropped_after_deferral"); // provably dropped
                    }
                    lock (_deferredLock) _deferred.RemoveAll(d => d.BatchId == batchId);
                    break;
                }
                case SettlementTxState.NotSubmitted:
                    await _core.CancelAllOrdersAsync(matches, "not_submitted_after_deferral"); // definitively not submitted
                    lock (_deferredLock) _deferred.RemoveAll(d => d.BatchId == batchId);
                    break;
                default:
                    break; // Unknown: the tx may still mine -> keep deferred, reservations HELD, never unwound on closure
            }
        }
    }
}
