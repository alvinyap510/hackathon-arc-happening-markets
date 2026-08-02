using System.Numerics;

namespace Venue.Domain;

/// <summary>
/// Delta-complete venue events (PLAN_CONTRACTS §6). The indexer decodes raw logs
/// into these; the ledger rebuilds balances from the GRANULAR subset only (see
/// Ledger.RebuildRule) and the market/RFM mirrors consume the lifecycle subset.
/// `Contract` is the normalized emitting address — required to disambiguate the two
/// Redeemed/MarketReserved events that share a signature across contracts.
/// Ordering across events is (BlockNumber, LogIndex); that order is the single
/// source of truth for the rebuild.
/// </summary>
public abstract record VenueEvent(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash);

public sealed record Allocation(string Account, BigInteger Amount);

public sealed record Funding(FundingKind Kind, string Ref, string Account, BigInteger Amount);

// ---------------------------------------------------------------- Vault

public sealed record Deposited(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string User, BigInteger Amt)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record Withdrawn(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string User, BigInteger Amt)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record TokensDeposited(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string User, string TokenId, BigInteger Amt)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record TokensWithdrawn(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string User, string TokenId, BigInteger Amt)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record USDCMoved(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string From, string To, BigInteger Amt, string TradeId)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record TokensMoved(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string From, string To, string TokenId, BigInteger Amt, string TradeId)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record Locked(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string Ref, string User, BigInteger Amt)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record LockReleased(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string Ref, string User, BigInteger Amt)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

/// <summary>Bond slash / pay: locked -&gt; internal credit of `to`. The funder's free
/// balance is neutral (already released at Locked); `to` gains Amt.</summary>
public sealed record LockConsumed(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string Ref, string User, BigInteger Amt, string To)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

/// <summary>Mint size YES + size NO into the pool; FREE funding debits the account,
/// LOCK funding consumes an existing lock (neutral to free). Allocations credit tokens.</summary>
public sealed record PairMinted(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string MarketId, Allocation[] YesAlloc, Allocation[] NoAlloc, Funding[] Funding, BigInteger Size)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record PairBurned(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string MarketId, string YesFrom, string NoFrom, BigInteger Size, BigInteger YesCredit)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

/// <summary>Emitted by BOTH Vault (internal credit, user balances change) and
/// OutcomeTokens (pool pays wallet-held tokens). Disambiguate by Contract.</summary>
public sealed record Redeemed(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string User, string MarketId, BigInteger Amt)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

// ------------------------------------------------------------ OutcomeTokens

public sealed record MarketReserved(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string MarketId)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record MarketCreated(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string MarketId, byte[] Meta)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record MarketResolved(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string MarketId, Outcome Outcome)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

// ---------------------------------------------------------- CTFExchangeLite

public sealed record BatchSettled(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, string BatchId, string[] TradeIds)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

// --------------------------------------------------------------------- RFM

public sealed record RequestPosted(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId, string Market, RfmSide Side, BigInteger Quantity, BigInteger MaxPriceTick, BigInteger MinMatch, BigInteger CommitDeadline, BigInteger RevealDeadline, BigInteger EscrowAmount, BigInteger MinQuoteSize)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record QuoteCommitted(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId, string Mm, BigInteger CommitIndex)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record QuoteRevealed(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId, string Mm, BigInteger Tick, BigInteger Size, bool InRange)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record RfmFill(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId, string Mm, BigInteger Tick, BigInteger Size)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record RequestFinalized(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record RequestFailed(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record RequestCancelled(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record BondSlashed(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId, string Mm, string To)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);

public sealed record MarketBorn(string Contract, ulong BlockNumber, ulong LogIndex, string TxHash, BigInteger RequestId, string MarketId, BigInteger MarginalYesTick, BigInteger VwapYesTick, BigInteger FilledQuantity, RfmSide Side)
    : VenueEvent(Contract, BlockNumber, LogIndex, TxHash);
