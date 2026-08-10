using System.Numerics;
using Venue.Chain;
using Venue.Domain;

namespace Venue.Broadcasting;

/// <summary>
/// Outbound channel fan-out produced by the venue core. The API layer implements this
/// with the WebSocket hub (channels book:&lt;mkt&gt;, trades:&lt;mkt&gt;, rfm:&lt;reqId&gt;,
/// user:&lt;addr&gt;, each with a per-channel (generation, seq) protocol). Deliberately
/// signal-only: snapshots are pulled by the hub from the core so they can never drift.
/// </summary>
public interface IEventSink
{
    void BookChanged(string marketId);
    void Fills(string marketId, IReadOnlyList<SettlementTrade> trades);
    void OrderUpdated(string user, string orderId, string status);
    void BalanceChanged(string user);
    /// <summary>txHash is CONFIRMED-ONLY provenance (the settleBatch tx). Reverted/unwind
    /// emissions pass null; consumers must branch on status, never infer failure from a
    /// missing hash.</summary>
    void SettlementOutcome(string marketId, string batchId, TxStatus status, string? error, IReadOnlyList<string> tradeIds, string? txHash);
    void RfmChanged(BigInteger requestId);
    void MarketBorn(string marketId);
    void GenerationBump();
}
