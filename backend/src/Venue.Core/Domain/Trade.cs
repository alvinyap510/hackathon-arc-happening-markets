using System.Numerics;

namespace Venue.Domain;

/// <summary>
/// Settlement trade encoding — the exact shape CTFExchangeLite.Trade expects:
/// a tagged union over TRANSFER / MINT / MERGE (PLAN_CONTRACTS §2).
/// </summary>
public sealed record SettlementTrade(
    string TradeId,
    string MarketId,
    TradeClass Class,
    Outcome? Outcome,        // TRANSFER only
    string PartyA,           // TRANSFER: seller; MINT/MERGE: yes party
    string PartyB,           // TRANSFER: buyer; MINT/MERGE: no party
    long OutcomeTick,        // TRANSFER: outcome price; MINT/MERGE: yes tick
    BigInteger Size);
