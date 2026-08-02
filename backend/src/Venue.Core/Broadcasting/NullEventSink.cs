using System.Numerics;
using Venue.Chain;
using Venue.Domain;

namespace Venue.Broadcasting;

/// <summary>No-op sink used before the WS hub is attached.</summary>
public sealed class NullEventSink : IEventSink
{
    public void BookChanged(string marketId) { }
    public void Fills(string marketId, IReadOnlyList<SettlementTrade> trades) { }
    public void OrderUpdated(string user, string orderId, string status) { }
    public void BalanceChanged(string user) { }
    public void SettlementOutcome(string marketId, string batchId, TxStatus status, string? error, IReadOnlyList<string> tradeIds) { }
    public void RfmChanged(BigInteger requestId) { }
    public void MarketBorn(string marketId) { }
    public void GenerationBump() { }
}
