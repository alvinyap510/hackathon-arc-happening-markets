using System.Numerics;
using Venue.Domain;
using Venue.Settlement;

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

/// <summary>Tri-state result of locating a possibly-accepted settlement tx by batchId.</summary>
public enum SettlementTxState
{
    /// <summary>The tx was found (pending or mined) — reconcile it.</summary>
    Submitted,
    /// <summary>Definitively NOT submitted (clean scan of pending + recent blocks found nothing).</summary>
    NotSubmitted,
    /// <summary>Inconclusive (RPC error / partial scan) — the tx may still be out there; keep reservations HELD.</summary>
    Unknown,
}

public sealed record SettlementTxLookup(SettlementTxState State, string? TxHash);

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
    public string NormalizedUsdc => Domain.Addresses.Normalize(Usdc);
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

    /// <summary>Is the tx still in the mempool (submitted but not mined and not dropped)?</summary>
    Task<bool> IsTransactionPendingAsync(string txHash, CancellationToken ct);

    /// <summary>Best-effort revert attribution for a reverted tx (may be null).</summary>
    Task<BatchRevertInfo?> TryGetRevertAsync(string txHash, CancellationToken ct);

    /// <summary>
    /// Locate a settlement tx for a batchId when the submission call threw before returning a
    /// hash (the RPC may have ACCEPTED the tx and only the response was lost). Tri-state: a
    /// found tx is Submitted; a clean scan that finds nothing is NotSubmitted; an RPC error or
    /// inconclusive scan is Unknown — callers must treat Unknown as possibly-submitted and keep
    /// reservations held (never unwind on Unknown).
    /// </summary>
    Task<SettlementTxLookup> FindPendingSettlementAsync(string batchId, CancellationToken ct);

    Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct);
    Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct);

    Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct);
    Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct);
    Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct);
    Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct);
    Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct);
    Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct);
    Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct);
    Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct);
    Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct);

    /// <summary>Authoritative on-chain RFM requestCount (G6: the requestId of the last post).</summary>
    Task<BigInteger> GetRequestCountAsync(CancellationToken ct);

    /// <summary>Mint the self-deployed collateral MockUSDC to a user (G4 faucet).</summary>
    Task<string> SubmitMintUsdcAsync(string user, BigInteger amt, CancellationToken ct);

    /// <summary>On-chain MockUSDC wallet balance (G4: `wallet` on GET /v1/balances).</summary>
    Task<BigInteger> GetUsdcWalletBalanceAsync(string user, CancellationToken ct);

    Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct);
}
