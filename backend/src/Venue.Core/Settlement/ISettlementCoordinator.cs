using Venue.Engine;

namespace Venue.Settlement;

/// <summary>
/// Callback interface between the batcher and the venue core. All confirm/repair/cancel
/// mutations run under the core gate; the batcher itself performs the chain I/O outside it.
/// </summary>
public interface ISettlementCoordinator
{
    /// <summary>A whole batch settled on chain (txHash = the confirmed settleBatch tx):
    /// release fill reservations, record trade history, broadcast.</summary>
    Task ConfirmBatchAsync(string batchId, string txHash, IReadOnlyList<MatchedTrade> matches);

    /// <summary>A batch reverted at <paramref name="revert"/>: cancel the failing trade's orders, resubmit the rest.</summary>
    Task RepairBatchAsync(string batchId, IReadOnlyList<MatchedTrade> matches, BatchRevertInfo revert);

    /// <summary>Attribution unclear or repair attempts exhausted: cancel every order in the batch, let the book re-cross.</summary>
    Task CancelAllOrdersAsync(IReadOnlyList<MatchedTrade> matches, string reason);

    /// <summary>
    /// Settlement race seal: drop (and unwind the orders of) every match whose market is
    /// unknown, Closing or Resolved — a market that is resolving must never settle a stale
    /// fill, even one already dequeued into a batch. Returns the matches that may still settle.
    /// </summary>
    Task<IReadOnlyList<MatchedTrade>> UnwindClosedAsync(IReadOnlyList<MatchedTrade> matches);
}
