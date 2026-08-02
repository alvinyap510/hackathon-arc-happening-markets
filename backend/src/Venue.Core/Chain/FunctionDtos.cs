using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Venue.Chain;

// -------------------------------------------------------------- Vault user surface

[Function("deposit")]
public sealed class DepositFunction : FunctionMessage
{
    [Parameter("uint256", "amt", 1)] public BigInteger Amt { get; set; }
}

[Function("withdraw")]
public sealed class WithdrawFunction : FunctionMessage
{
    [Parameter("uint256", "amt", 1)] public BigInteger Amt { get; set; }
}

[Function("redeem")]
public sealed class VaultRedeemFunction : FunctionMessage
{
    [Parameter("bytes32", "marketId", 1)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint256", "amt", 2)] public BigInteger Amt { get; set; }
}

// ---------------------------------------------------------------------- RFM

[Function("postRequest")]
public sealed class PostRequestFunction : FunctionMessage
{
    [Parameter("bytes32", "market", 1)] public byte[] Market { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "side", 2)] public byte Side { get; set; }
    [Parameter("uint256", "quantity", 3)] public BigInteger Quantity { get; set; }
    [Parameter("uint256", "maxPriceTick", 4)] public BigInteger MaxPriceTick { get; set; }
    [Parameter("uint256", "minMatch", 5)] public BigInteger MinMatch { get; set; }
    [Parameter("uint256", "commitDeadline", 6)] public BigInteger CommitDeadline { get; set; }
    [Parameter("uint256", "revealDeadline", 7)] public BigInteger RevealDeadline { get; set; }
}

[Function("commitQuote")]
public sealed class CommitQuoteFunction : FunctionMessage
{
    [Parameter("uint256", "requestId", 1)] public BigInteger RequestId { get; set; }
    [Parameter("bytes32", "commitHash", 2)] public byte[] CommitHash { get; set; } = Array.Empty<byte>();
}

[Function("revealQuote")]
public sealed class RevealQuoteFunction : FunctionMessage
{
    [Parameter("uint256", "requestId", 1)] public BigInteger RequestId { get; set; }
    [Parameter("uint256", "priceTick", 2)] public BigInteger PriceTick { get; set; }
    [Parameter("uint256", "size", 3)] public BigInteger Size { get; set; }
    [Parameter("uint256", "salt", 4)] public BigInteger Salt { get; set; }
}

[Function("cancel")]
public sealed class RfmCancelFunction : FunctionMessage
{
    [Parameter("uint256", "requestId", 1)] public BigInteger RequestId { get; set; }
}

[Function("finalize")]
public sealed class FinalizeFunction : FunctionMessage
{
    [Parameter("uint256", "requestId", 1)] public BigInteger RequestId { get; set; }
}

// ---------------------------------------------------------------- OutcomeTokens

[Function("resolve")]
public sealed class ResolveFunction : FunctionMessage
{
    [Parameter("bytes32", "marketId", 1)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "outcome", 2)] public byte Outcome { get; set; }
}
