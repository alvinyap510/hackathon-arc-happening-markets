using System.Numerics;

namespace Venue.Domain;

public enum OrderType
{
    Limit,
    Market,
}

public enum OrderStatus
{
    New,        // accepted, reserved, being matched
    Resting,    // resting on the book (limit, unfilled remainder)
    Filled,     // fully matched off-chain
    Partial,    // partially matched, remainder killed (market / fill-and-kill)
    Cancelled,  // cancelled by user or by the adverse-event sweep
    Rejected,   // rejected at admission (insufficient available, invalid)
    Settled,    // fill(s) confirmed on chain; order fully done
}

/// <summary>Trader input to place an order. Prices are ticks of the REQUESTED outcome.</summary>
public sealed record OrderRequest(
    string User,
    string MarketId,
    Outcome Outcome,
    OrderSide Side,
    BigInteger Size,
    long Price,
    OrderType Type,
    string ClientOrderId,
    long? ExpirationUnixSec);

/// <summary>
/// A venue order in the book. Intake transforms to canonical YES basis ONCE
/// (BookSide/BookPrice); the original (Outcome, Side, Price) is retained so
/// settlement can classify TRANSFER/MINT/MERGE and re-derive outcome-ticks.
/// </summary>
public sealed class Order
{
    public required string OrderId { get; init; }
    public required string User { get; init; }
    public required string MarketId { get; init; }
    public required Outcome Outcome { get; init; }
    public required OrderSide Side { get; init; }
    public required BigInteger Size { get; init; }
    public required BigInteger Remaining { get; set; }
    public required long Price { get; init; }            // original outcome ticks (limit), or 0 for market
    public required OrderType Type { get; init; }
    public required OrderStatus Status { get; set; }
    public required BookSide BookSide { get; init; }     // canonical YES-basis side
    public required long BookPrice { get; init; }        // canonical YES-basis ticks
    public required string ReservedAsset { get; init; }  // Assets.Usdc or a tokenId
    public required BigInteger ReservedAmount { get; init; }
    public BigInteger ReleasedReservation { get; private set; }
    public required long CreatedAtUnixSec { get; init; }
    public long? ExpirationUnixSec { get; init; }

    /// <summary>Release a slice of the order's reservation (fill confirm or cancel/kill).</summary>
    public void ReleaseReservation(BigInteger amount)
    {
        if (amount <= 0) return;
        ReleasedReservation = ReleasedReservation + amount > ReservedAmount ? ReservedAmount : ReleasedReservation + amount;
    }

    /// <summary>Biggest stored price a market order takes (crosses every resting level).</summary>
    public const long MarketBuyBookPrice = Prices.Denominator;
    /// <summary>Smallest stored price a market order takes.</summary>
    public const long MarketSellBookPrice = 0;

    public bool IsMarket => Type == OrderType.Market;

    /// <summary>
    /// YES-basis stored price for the intake transform. A NO-side order is the
    /// complement of a YES-side order: BUY NO @ p == SELL YES @ (1000-p) and
    /// SELL NO @ p == BUY YES @ (1000-p), so the stored price depends only on the
    /// outcome; the stored SIDE (StoredSideFor) carries the BUY/SELL direction.
    /// </summary>
    public static long StoredPriceFor(Outcome outcome, long price)
        => outcome == Outcome.Yes ? price : Prices.Complement(price);

    /// <summary>Canonical book side: bids = BUY YES + SELL NO (stored BUY); asks = SELL YES + BUY NO (stored SELL).</summary>
    public static BookSide StoredSideFor(OrderSide side, Outcome outcome)
    {
        // BUY YES -> bid; SELL NO -> bid; SELL YES -> ask; BUY NO -> ask.
        bool isBuyInYesBasis = (side == OrderSide.Buy) == (outcome == Outcome.Yes);
        return isBuyInYesBasis ? BookSide.Bid : BookSide.Ask;
    }

    /// <summary>Reservation: BUY reserves USDC quote; SELL reserves the outcome token.</summary>
    public static (string Asset, BigInteger Amount) ReserveFor(string marketId, Outcome outcome, OrderSide side, BigInteger size, long price)
    {
        if (side == OrderSide.Buy) return (Assets.Usdc, Prices.LegCost(size, price));
        return (Assets.TokenId(marketId, outcome), size);
    }
}

/// <summary>A single maker-taker crossing produced by the matcher, already classified.</summary>
public sealed record Fill(
    string TradeId,
    string MakerOrderId,
    string TakerOrderId,
    string MarketId,
    TradeClass Class,
    BigInteger Size,
    long YesBasisTick);
