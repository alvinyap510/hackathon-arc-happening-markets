using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Venue.Chain;

[Event("RequestPosted")]
public sealed class RequestPostedEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("bytes32", "market", 2, true)] public byte[] Market { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "side", 3, false)] public byte Side { get; set; }
    [Parameter("uint256", "quantity", 4, false)] public BigInteger Quantity { get; set; }
    [Parameter("uint256", "maxPriceTick", 5, false)] public BigInteger MaxPriceTick { get; set; }
    [Parameter("uint256", "minMatch", 6, false)] public BigInteger MinMatch { get; set; }
    [Parameter("uint256", "commitDeadline", 7, false)] public BigInteger CommitDeadline { get; set; }
    [Parameter("uint256", "revealDeadline", 8, false)] public BigInteger RevealDeadline { get; set; }
    [Parameter("uint256", "escrowAmount", 9, false)] public BigInteger EscrowAmount { get; set; }
    [Parameter("uint256", "minQuoteSize", 10, false)] public BigInteger MinQuoteSize { get; set; }
}

[Event("QuoteCommitted")]
public sealed class QuoteCommittedEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("address", "mm", 2, true)] public string Mm { get; set; } = "";
    [Parameter("uint256", "commitIndex", 3, false)] public BigInteger CommitIndex { get; set; }
}

[Event("QuoteRevealed")]
public sealed class QuoteRevealedEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("address", "mm", 2, true)] public string Mm { get; set; } = "";
    [Parameter("uint256", "tick", 3, false)] public BigInteger Tick { get; set; }
    [Parameter("uint256", "size", 4, false)] public BigInteger Size { get; set; }
    [Parameter("bool", "inRange", 5, false)] public bool InRange { get; set; }
}

[Event("RfmFill")]
public sealed class RfmFillEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("address", "mm", 2, true)] public string Mm { get; set; } = "";
    [Parameter("uint256", "tick", 3, false)] public BigInteger Tick { get; set; }
    [Parameter("uint256", "size", 4, false)] public BigInteger Size { get; set; }
}

[Event("RequestFinalized")]
public sealed class RequestFinalizedEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
}

[Event("RequestFailed")]
public sealed class RequestFailedEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
}

[Event("RequestCancelled")]
public sealed class RequestCancelledEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
}

[Event("BondSlashed")]
public sealed class BondSlashedEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("address", "mm", 2, true)] public string Mm { get; set; } = "";
    [Parameter("address", "to", 3, true)] public string To { get; set; } = "";
}

[Event("MarketReserved")]
public sealed class RfmMarketReservedEventDto : IEventDTO
{
    [Parameter("bytes32", "marketId", 1, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint256", "requestId", 2, true)] public BigInteger RequestId { get; set; }
}

[Event("MarketBorn")]
public sealed class MarketBornEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("bytes32", "marketId", 2, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint256", "marginalYesTick", 3, false)] public BigInteger MarginalYesTick { get; set; }
    [Parameter("uint256", "vwapYesTick", 4, false)] public BigInteger VwapYesTick { get; set; }
    [Parameter("uint256", "filledQuantity", 5, false)] public BigInteger FilledQuantity { get; set; }
    [Parameter("uint8", "side", 6, false)] public byte Side { get; set; }
}
