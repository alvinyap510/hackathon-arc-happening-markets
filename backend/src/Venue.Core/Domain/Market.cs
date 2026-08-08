using System.Numerics;

namespace Venue.Domain;

/// <summary>A single settled trade retained for market history display.</summary>
public sealed record TradeRecord(string TradeId, string MakerOrderId, string TakerOrderId, TradeClass Class, BigInteger Size, long YesBasisTick, long UnixSec, string BatchId);

/// <summary>Lifecycle state for one venue market (mirrors OutcomeTokens markets[]).</summary>
public sealed class Market
{
    public required string MarketId { get; init; }
    public bool Reserved { get; set; }
    public bool Exists { get; set; }
    public bool Resolved { get; set; }
    /// <summary>Set the moment resolution is INITIATED (before the resolve tx mines or its event
    /// is indexed). A Closing market admits NO new orders and settles NO fills — it is the
    /// local gate that closes the window between "resolution started" and "resolution indexed".</summary>
    public bool Closing { get; set; }
    public Outcome? WinningOutcome { get; set; }

    /// <summary>RFM-born birth marks (canonical YES basis), from MarketBorn.</summary>
    public long? BornMarginalYesTick { get; set; }
    public long? BornVwapYesTick { get; set; }
    public BigInteger? BornFilledQuantity { get; set; }
    public RfmSide? BornSide { get; set; }
    public BigInteger? BornRequestId { get; set; }

    /// <summary>Bumped on restart and at birth so WS clients resnapshot the book.</summary>
    public long BookGeneration { get; set; } = 1;

    /// <summary>Deterministic per-fill sequence used in tradeId derivation (per market).</summary>
    public long FillSeq { get; set; }

    public List<TradeRecord> Trades { get; } = new();

    public long NextFillSeq() => ++FillSeq;
}

public sealed record BookLevel(long Price, BigInteger Size);

/// <summary>YES and NO projections of one market's book (both derived from the single
/// YES-basis book via the complement transform at projection time).</summary>
public sealed record BookSnapshot(
    string MarketId,
    long Generation,
    IReadOnlyList<BookLevel> YesBids,
    IReadOnlyList<BookLevel> YesAsks,
    IReadOnlyList<BookLevel> NoBids,
    IReadOnlyList<BookLevel> NoAsks);

/// <summary>A position on one market/outcome. Carries the marketId + outcome alongside the
/// keccak tokenId (which the frontend cannot reverse) so clients map directly (SEAM 3).
/// `Reserved` is the amount committed to the user's own open SELL orders on this token —
/// the off-chain venue reservation (asset-scoped; distinct from the USDC `Reserved`).</summary>
public sealed record PositionBalance(string TokenId, string MarketId, Outcome Outcome, BigInteger Amount, BigInteger Reserved);

public sealed record UserBalances(
    string User,
    BigInteger ChainFree,
    BigInteger Reserved,
    BigInteger Available,
    IReadOnlyList<PositionBalance> Positions);
