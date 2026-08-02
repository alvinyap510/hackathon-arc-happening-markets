using System.Numerics;

namespace Venue.Domain;

/// <summary>A revealed quote on an RFM request (raw requested-outcome ticks).</summary>
public sealed record RevealView(string Mm, BigInteger Tick, BigInteger Size, bool InRange);

/// <summary>
/// Off-chain mirror of one RFM Request. Phase is DERIVED from the mirrored state
/// exactly as the contract derives it (PLAN_CONTRACTS §4.0): terminal flags win,
/// then deadline ordering decides OPEN/COMMIT/REVEAL. The coordinator cranks
/// finalize() once `finalizeReady` and broadcasts timelines to the API.
/// </summary>
public sealed class RfmRequestMirror
{
    public required BigInteger RequestId { get; init; }
    public required string Market { get; init; }
    public required RfmSide Side { get; init; }
    public required BigInteger Quantity { get; init; }
    public required BigInteger MaxPriceTick { get; init; }
    public required BigInteger MinMatch { get; init; }
    public required BigInteger CommitDeadline { get; init; }
    public required BigInteger RevealDeadline { get; init; }
    public required BigInteger EscrowAmount { get; init; }
    public required BigInteger MinQuoteSize { get; init; }

    public BigInteger CommitCount { get; set; }
    public bool Finalized { get; set; }
    public bool Failed { get; set; }
    public bool Cancelled { get; set; }

    public string? MarketId { get; set; }                 // from MarketBorn
    public long? BornMarginalYesTick { get; set; }
    public long? BornVwapYesTick { get; set; }
    public BigInteger? BornFilledQuantity { get; set; }

    public List<RevealView> Reveals { get; } = new();
    public List<(string Mm, BigInteger Tick, BigInteger Size)> Fills { get; } = new();

    public RfmPhase PhaseAt(BigInteger nowUnixSec)
    {
        if (Cancelled) return RfmPhase.Cancelled;
        if (Finalized) return RfmPhase.Finalized;
        if (Failed) return RfmPhase.Failed;
        if (nowUnixSec <= CommitDeadline) return CommitCount == 0 ? RfmPhase.Open : RfmPhase.Commit;
        return RfmPhase.Reveal;
    }

    public bool FinalizeReadyAt(BigInteger nowUnixSec)
        => nowUnixSec > RevealDeadline && !Finalized && !Failed && !Cancelled;
}
