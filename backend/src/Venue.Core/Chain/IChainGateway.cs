using System.Numerics;
using Venue.Domain;

namespace Venue.Chain;

public enum TxStatus
{
    Unknown,
    Pending,
    Confirmed,
    Reverted,
    Dropped,
}

/// <summary>Settlement receipt: the on-chain outcome of one whole-batch settleBatch.</summary>
public sealed record SettlementReceipt(TxStatus Status, Settlement.BatchRevertInfo? Revert);

/// <summary>Contract addresses + chain parameters (no secrets: keys come from env at host time).</summary>
public sealed record ChainConfig(
    string RpcUrl,
    BigInteger ChainId,
    ulong StartBlock,
    string Vault,
    string OutcomeTokens,
    string Exchange,
    string Rfm,
    string Usdc,
    string OperatorAddress)
{
    public string NormalizedVault => Domain.Addresses.Normalize(Vault);
    public string NormalizedOutcomeTokens => Domain.Addresses.Normalize(OutcomeTokens);
    public string NormalizedExchange => Domain.Addresses.Normalize(Exchange);
    public string NormalizedRfm => Domain.Addresses.Normalize(Rfm);
}

/// <summary>
/// The chain seam. The indexer fetches logs; the settlement batcher submits whole
/// batches and awaits their outcome; the RFM coordinator cranks finalize; the API
/// submits user ops (deposits, RFM lifecycle) and reads tx status. One process, one
/// operator SCA for settlement (serialized nonce). A Simulated implementation feeds
/// synthetic events for the local demo and tests; the Nethereum implementation talks
/// to Arc over RPC.
/// </summary>
public interface IChainGateway
{
    bool Simulated { get; }

    Task<ulong> LatestBlockAsync(CancellationToken ct);
    Task<IReadOnlyList<VenueEvent>> FetchLogsAsync(ulong fromBlock, ulong toBlock, CancellationToken ct);
    Task<IReadOnlyList<VenueEvent>> DecodeReceiptEventsAsync(string txHash, CancellationToken ct);
    Task<string> GetBlockHashAsync(ulong blockNumber, CancellationToken ct);

    Task<string> SubmitSettlementAsync(string batchId, IReadOnlyList<SettlementTrade> trades, CancellationToken ct);
    Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct);

    Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct);
    Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct);

    Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct);
    Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct);
    Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct);
    Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct);
    Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct);
    Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct);
    Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct);

    Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct);
}
