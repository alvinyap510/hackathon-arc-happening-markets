using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Venue.Chain;

// ------------------------------------------------------------------ Vault

[Event("Deposited")]
public sealed class DepositedEventDto : IEventDTO
{
    [Parameter("address", "user", 1, true)] public string User { get; set; } = "";
    [Parameter("uint256", "amt", 2, false)] public BigInteger Amt { get; set; }
}

[Event("Withdrawn")]
public sealed class WithdrawnEventDto : IEventDTO
{
    [Parameter("address", "user", 1, true)] public string User { get; set; } = "";
    [Parameter("uint256", "amt", 2, false)] public BigInteger Amt { get; set; }
}

[Event("TokensDeposited")]
public sealed class TokensDepositedEventDto : IEventDTO
{
    [Parameter("address", "user", 1, true)] public string User { get; set; } = "";
    [Parameter("uint256", "id", 2, true)] public BigInteger Id { get; set; }
    [Parameter("uint256", "amt", 3, false)] public BigInteger Amt { get; set; }
}

[Event("TokensWithdrawn")]
public sealed class TokensWithdrawnEventDto : IEventDTO
{
    [Parameter("address", "user", 1, true)] public string User { get; set; } = "";
    [Parameter("uint256", "id", 2, true)] public BigInteger Id { get; set; }
    [Parameter("uint256", "amt", 3, false)] public BigInteger Amt { get; set; }
}

[Event("USDCMoved")]
public sealed class UsdcMovedEventDto : IEventDTO
{
    [Parameter("address", "from", 1, true)] public string From { get; set; } = "";
    [Parameter("address", "to", 2, true)] public string To { get; set; } = "";
    [Parameter("uint256", "amt", 3, false)] public BigInteger Amt { get; set; }
    [Parameter("bytes32", "tradeId", 4, true)] public byte[] TradeId { get; set; } = Array.Empty<byte>();
}

[Event("TokensMoved")]
public sealed class TokensMovedEventDto : IEventDTO
{
    [Parameter("address", "from", 1, true)] public string From { get; set; } = "";
    [Parameter("address", "to", 2, true)] public string To { get; set; } = "";
    [Parameter("uint256", "id", 3, false)] public BigInteger Id { get; set; }
    [Parameter("uint256", "amt", 4, false)] public BigInteger Amt { get; set; }
    [Parameter("bytes32", "tradeId", 5, true)] public byte[] TradeId { get; set; } = Array.Empty<byte>();
}

[Event("Locked")]
public sealed class LockedEventDto : IEventDTO
{
    [Parameter("bytes32", "ref", 1, true)] public byte[] Ref { get; set; } = Array.Empty<byte>();
    [Parameter("address", "user", 2, true)] public string User { get; set; } = "";
    [Parameter("uint256", "amt", 3, false)] public BigInteger Amt { get; set; }
}

[Event("LockReleased")]
public sealed class LockReleasedEventDto : IEventDTO
{
    [Parameter("bytes32", "ref", 1, true)] public byte[] Ref { get; set; } = Array.Empty<byte>();
    [Parameter("address", "user", 2, true)] public string User { get; set; } = "";
    [Parameter("uint256", "amt", 3, false)] public BigInteger Amt { get; set; }
}

[Event("LockConsumed")]
public sealed class LockConsumedEventDto : IEventDTO
{
    [Parameter("bytes32", "ref", 1, true)] public byte[] Ref { get; set; } = Array.Empty<byte>();
    [Parameter("address", "user", 2, true)] public string User { get; set; } = "";
    [Parameter("uint256", "amt", 3, false)] public BigInteger Amt { get; set; }
    [Parameter("address", "to", 4, true)] public string To { get; set; } = "";
}

[Struct("Allocation")]
public sealed class AllocationDto
{
    [Parameter("address", "account", 1)] public string Account { get; set; } = "";
    [Parameter("uint256", "amount", 2)] public BigInteger Amount { get; set; }
}

[Struct("Funding")]
public sealed class FundingDto
{
    [Parameter("uint8", "kind", 1)] public byte Kind { get; set; }
    [Parameter("bytes32", "ref", 2)] public byte[] Ref { get; set; } = Array.Empty<byte>();
    [Parameter("address", "account", 3)] public string Account { get; set; } = "";
    [Parameter("uint256", "amount", 4)] public BigInteger Amount { get; set; }
}

[Event("PairMinted")]
public sealed class PairMintedEventDto : IEventDTO
{
    [Parameter("bytes32", "marketId", 1, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("tuple[]", "yesAlloc", 2, false)] public List<AllocationDto> YesAlloc { get; set; } = new();
    [Parameter("tuple[]", "noAlloc", 3, false)] public List<AllocationDto> NoAlloc { get; set; } = new();
    [Parameter("tuple[]", "funding", 4, false)] public List<FundingDto> Funding { get; set; } = new();
    [Parameter("uint256", "size", 5, false)] public BigInteger Size { get; set; }
}

[Event("PairBurned")]
public sealed class PairBurnedEventDto : IEventDTO
{
    [Parameter("bytes32", "marketId", 1, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("address", "yesFrom", 2, true)] public string YesFrom { get; set; } = "";
    [Parameter("address", "noFrom", 3, true)] public string NoFrom { get; set; } = "";
    [Parameter("uint256", "size", 4, false)] public BigInteger Size { get; set; }
    [Parameter("uint256", "yesCredit", 5, false)] public BigInteger YesCredit { get; set; }
}

[Event("Redeemed")]
public sealed class RedeemedEventDto : IEventDTO
{
    [Parameter("address", "user", 1, true)] public string User { get; set; } = "";
    [Parameter("bytes32", "marketId", 2, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint256", "amt", 3, false)] public BigInteger Amt { get; set; }
}

// ------------------------------------------------------------ OutcomeTokens

[Event("MarketReserved")]
public sealed class OtMarketReservedEventDto : IEventDTO
{
    [Parameter("bytes32", "marketId", 1, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
}

[Event("MarketCreated")]
public sealed class MarketCreatedEventDto : IEventDTO
{
    [Parameter("bytes32", "marketId", 1, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("bytes", "meta", 2, false)] public byte[] Meta { get; set; } = Array.Empty<byte>();
}

[Event("MarketResolved")]
public sealed class MarketResolvedEventDto : IEventDTO
{
    [Parameter("bytes32", "marketId", 1, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "outcome", 2, false)] public byte Outcome { get; set; }
}

// ---------------------------------------------------------- CTFExchangeLite

[Event("BatchSettled")]
public sealed class BatchSettledEventDto : IEventDTO
{
    [Parameter("bytes32", "batchId", 1, true)] public byte[] BatchId { get; set; } = Array.Empty<byte>();
    [Parameter("bytes32[]", "tradeIds", 2, false)] public List<byte[]> TradeIds { get; set; } = new();
}

// ------------------------------------------------ settleBatch function DTO

[Struct("Trade")]
public class TradeStructDto
{
    [Parameter("bytes32", "tradeId", 1)] public byte[] TradeId { get; set; } = Array.Empty<byte>();
    [Parameter("bytes32", "marketId", 2)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "class", 3)] public byte Class { get; set; }
    [Parameter("uint8", "outcome", 4)] public byte Outcome { get; set; }
    [Parameter("address", "partyA", 5)] public string PartyA { get; set; } = "";
    [Parameter("address", "partyB", 6)] public string PartyB { get; set; } = "";
    [Parameter("uint256", "outcomeTick", 7)] public BigInteger OutcomeTick { get; set; }
    [Parameter("uint256", "size", 8)] public BigInteger Size { get; set; }
}

[Function("settleBatch")]
public sealed class SettleBatchFunction : FunctionMessage
{
    [Parameter("bytes32", "batchId", 1)] public byte[] BatchId { get; set; } = Array.Empty<byte>();
    [Parameter("tuple[]", "trades", 2)] public List<TradeStructDto> Trades { get; set; } = new();
}
