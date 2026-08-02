using System.Numerics;

namespace Venue.Domain;

/// <summary>Binary market outcome. Mirrors IOutcomeTokens.Outcome (YES=0, NO=1).</summary>
public enum Outcome
{
    Yes = 0,
    No = 1,
}

/// <summary>Trader-facing direction for a given outcome. Mirrors CTFExchangeLite order Side.</summary>
public enum OrderSide
{
    Buy = 0,
    Sell = 1,
}

/// <summary>Book side in canonical YES basis (the stored representation).</summary>
public enum BookSide
{
    Bid = 0,
    Ask = 1,
}

/// <summary>Settlement class of a matched pair. Mirrors CTFExchangeLite.TradeClass.</summary>
public enum TradeClass
{
    Transfer = 0,
    Mint = 1,
    Merge = 2,
}

/// <summary>RFM auction phase. Mirrors IRFM.Phase.</summary>
public enum RfmPhase
{
    Open = 0,
    Commit = 1,
    Reveal = 2,
    Finalized = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>RFM requested side (the outcome the institution buys). Mirrors IRFM.Side.</summary>
public enum RfmSide
{
    Yes = 0,
    No = 1,
}

/// <summary>Which class of funds funded a mintPair Funding[] entry. Mirrors IVault.FundingKind.</summary>
public enum FundingKind
{
    Lock = 0,
    Free = 1,
}

public static class Prices
{
    /// <summary>Tick denominator: prices are integer ticks in [0, 1000] (1 tick = 0.001).</summary>
    public const long Denominator = 1000;

    /// <summary>Minimum resting tick.</summary>
    public const long MinTick = 1;

    /// <summary>Maximum resting tick.</summary>
    public const long MaxTick = Denominator - 1;

    /// <summary>Complement transform: convert a tick of one outcome to the other (YES basis).
    /// Canonical YES tick = 1000 - NO tick.</summary>
    public static long Complement(long tick) => Denominator - tick;

    /// <summary>floor(size * tick / 1000) — the one rounding rule (priced leg).</summary>
    public static BigInteger LegCost(BigInteger size, long tick) => size * tick / Denominator;

    /// <summary>Counter-leg of a pair is always size - legA (never independently rounded).</summary>
    public static BigInteger CounterLeg(BigInteger size, BigInteger legA) => size - legA;
}

/// <summary>Fixed well-known addresses/assets in the ledger.</summary>
public static class Assets
{
    /// <summary>Reservation asset key for USDC free balance.</summary>
    public const string Usdc = "USDC";

    /// <summary>Builds the ERC-1155 token-id string for a market/outcome pair.</summary>
    public static string TokenId(string marketId, Outcome outcome) => Infrastructure.Hash.TokenId(marketId, outcome);
}

public static class Addresses
{
    /// <summary>Normalize an Ethereum address to lowercase 0x-prefixed form.</summary>
    public static string Normalize(string address)
    {
        if (string.IsNullOrEmpty(address)) return "0x0000000000000000000000000000000000000000";
        var a = address.Trim();
        if (!a.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) a = "0x" + a;
        return a.ToLowerInvariant();
    }

    /// <summary>Zero address (unused-party / NOBODY sentinel).</summary>
    public static string Zero => Normalize("0x0000000000000000000000000000000000000000");
}
