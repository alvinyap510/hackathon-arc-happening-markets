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

    private readonly IChainGateway _gateway;
    private readonly ISettlementCoordinator _core;
    private readonly string _operatorAddress;
    private readonly ConcurrentQueue<MatchedTrade> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private long _batchAttempt;
    private int _busy; // 1 while a batch is submitted/awaiting; read by AwaitIdleAsync

    public SettlementBatcher(IChainGateway gateway, ISettlementCoordinator core, string operatorAddress)
    {
        _gateway = gateway;
        _core = core;
        _operatorAddress = operatorAddress;
    }

    public int PendingCount => _queue.Count;

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

    /// <summary>The settlement loop; run as a background task from the host.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var batch = await DrainAsync(ct);
            if (batch.Count == 0)
            {
                await Task.Delay(FlushInterval, ct);
                continue;
            }
            await SubmitWithRepairAsync(batch, ct);
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
    {
        Interlocked.Exchange(ref _busy, 1);
        try
        {
            await SubmitWithRepairCoreAsync(batch, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task SubmitWithRepairCoreAsync(List<MatchedTrade> batch, CancellationToken ct)
    {
        var remaining = batch;
        for (var attempt = 0; attempt <= MaxRepairAttempts && remaining.Count > 0; attempt++)
        {
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
                outcome = new SettlementReceipt(TxStatus.Unknown, null);
                System.Console.WriteLine($"settle: submit/await failed {ex.Message}");
            }

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
}
