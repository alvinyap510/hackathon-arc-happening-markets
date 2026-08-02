using System.Numerics;
using Venue.Circle;
using Venue.Domain;
using Venue.Settlement;

namespace Venue.Chain;

/// <summary>
/// Circle Wallets provider (Venue:WalletProvider = circle). USER actions (approve+deposit,
/// withdraw, RFM lifecycle, redeem) are submitted from the user's dev-controlled SCA via
/// Circle's transactions API as GASLESS contract executions (Gas Station, feeLevel GAS_LESS);
/// reads, settlement, finalize, resolve and the faucet stay on the wrapped Nethereum RPC
/// gateway (operator-signed). Implements ISessionProvisioner: one Circle SCA per email ref,
/// idempotent + durable (CircleWalletStore). Nethereum + Simulated providers are untouched.
/// </summary>
public sealed class CircleChainGateway : IChainGateway, ISessionProvisioner
{
    private readonly ChainConfig _cfg;
    private readonly NethereumChainGateway _rpc;
    private readonly CircleW3sClient _circle;
    private readonly CircleWalletStore _wallets;
    private long _txSeq;

    public bool Simulated => false;

    public CircleChainGateway(ChainConfig cfg, string operatorPrivateKey, CircleW3sClient circle, CircleWalletStore wallets)
    {
        _cfg = cfg;
        _circle = circle;
        _wallets = wallets;
        // User ops never reach this wrapped gateway (they go via Circle), so its key
        // resolver is a stub; reads/operator/settlement use the operator key directly.
        _rpc = new NethereumChainGateway(cfg, operatorPrivateKey, _ => null);
    }

    // ------------------------------------------------------------ session (SCA bind)

    public async Task<string> ProvisionAsync(string userRef, CancellationToken ct)
    {
        var existing = _wallets.ByRef(userRef);
        if (existing != null) return existing.Address;
        var wallet = await _circle.BindWalletAsync(userRef, ct);
        _wallets.Save(userRef, wallet.Id, wallet.Address);
        return wallet.Address;
    }

    // ----------------------------------------- reads / operator / settlement (RPC)

    public Task<ulong> LatestBlockAsync(CancellationToken ct) => _rpc.LatestBlockAsync(ct);
    public Task<string> GetBlockHashAsync(ulong blockNumber, CancellationToken ct) => _rpc.GetBlockHashAsync(blockNumber, ct);
    public Task<IReadOnlyList<VenueEvent>> FetchLogsAsync(ulong fromBlock, ulong toBlock, CancellationToken ct) => _rpc.FetchLogsAsync(fromBlock, toBlock, ct);
    public Task<IReadOnlyList<VenueEvent>> DecodeReceiptEventsAsync(string txHash, CancellationToken ct) => _rpc.DecodeReceiptEventsAsync(txHash, ct);
    public Task<string> SubmitSettlementAsync(string batchId, IReadOnlyList<SettlementTrade> trades, CancellationToken ct) => _rpc.SubmitSettlementAsync(batchId, trades, ct);
    public Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct) => _rpc.AwaitSettlementAsync(txHash, ct);
    public Task<bool> IsTransactionPendingAsync(string txHash, CancellationToken ct) => _rpc.IsTransactionPendingAsync(txHash, ct);
    public Task<BatchRevertInfo?> TryGetRevertAsync(string txHash, CancellationToken ct) => _rpc.TryGetRevertAsync(txHash, ct);
    public Task<SettlementTxLookup> FindPendingSettlementAsync(string batchId, CancellationToken ct) => _rpc.FindPendingSettlementAsync(batchId, ct);
    public Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct) => _rpc.SubmitFinalizeAsync(requestId, ct);
    public Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct) => _rpc.SubmitResolveAsync(marketId, outcome, ct);
    public Task SubmitCreateMarketAsync(string marketId, byte[] meta, CancellationToken ct) => _rpc.SubmitCreateMarketAsync(marketId, meta, ct);
    public Task<BigInteger> GetRequestCountAsync(CancellationToken ct) => _rpc.GetRequestCountAsync(ct);
    public Task<string> SubmitMintUsdcAsync(string user, BigInteger amt, CancellationToken ct) => _rpc.SubmitMintUsdcAsync(user, amt, ct);
    public Task<BigInteger> GetUsdcWalletBalanceAsync(string user, CancellationToken ct) => _rpc.GetUsdcWalletBalanceAsync(user, ct);
    public Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct) => _rpc.TxStatusAsync(txHash, ct);
    public Task FundGasAsync(string address, CancellationToken ct) => Task.CompletedTask; // Gas Station sponsors SCA fees

    // ----------------------------------------------------- user ops via Circle SCA

    public async Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct)
    {
        // approve-before-deposit from the SCA (zero allowance on a fresh wallet).
        var wallet = WalletFor(user);
        await SubmitTx(wallet, _cfg.NormalizedUsdc, "approve(address,uint256)",
            new[] { _cfg.NormalizedVault, amt.ToString() }, ct);
        return await SubmitTx(wallet, _cfg.NormalizedVault, "deposit(uint256)", new[] { amt.ToString() }, ct);
    }

    public Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedVault, "withdraw(uint256)", new[] { amt.ToString() }, ct);

    public Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedVault, "depositTokens(uint256,uint256)", new[] { TokenIdToDecimal(tokenId), amt.ToString() }, ct);

    public Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedVault, "withdrawTokens(uint256,uint256)", new[] { TokenIdToDecimal(tokenId), amt.ToString() }, ct);

    public Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedVault, "redeem(bytes32,uint256)", new[] { Normalize32(marketId), amt.ToString() }, ct);

    public Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity,
        BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedRfm, "postRequest(bytes32,uint8,uint256,uint256,uint256,uint256,uint256)",
            new[] { Normalize32(market), ((byte)side).ToString(), quantity.ToString(), maxPriceTick.ToString(), minMatch.ToString(), commitDeadline.ToString(), revealDeadline.ToString() }, ct);

    public Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedRfm, "commitQuote(uint256,bytes32)", new[] { requestId.ToString(), Normalize32(commitHash) }, ct);

    public Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedRfm, "revealQuote(uint256,uint256,uint256,uint256)",
            new[] { requestId.ToString(), priceTick.ToString(), size.ToString(), salt.ToString() }, ct);

    public Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct)
        => SubmitTx(WalletFor(user), _cfg.NormalizedRfm, "cancel(uint256)", new[] { requestId.ToString() }, ct);

    // ------------------------------------------------------------------ internals

    private CircleWalletInfo WalletFor(string sessionAddress)
    {
        var wallet = _wallets.ByAddress(sessionAddress);
        if (wallet == null)
            throw new InvalidOperationException("no Circle SCA bound for this session address (re-login via POST /v1/session)");
        return wallet;
    }

    private async Task<string> SubmitTx(CircleWalletInfo wallet, string contract, string signature, string[] args, CancellationToken ct)
    {
        var idem = $"tx-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _txSeq)}";
        var circleTxId = await _circle.SubmitContractExecutionAsync(wallet.Id, contract, signature, args, idem, ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var info = await _circle.GetTransactionAsync(circleTxId, ct);
            if (info.State is "COMPLETE" or "COMPLETED")
            {
                if (string.IsNullOrEmpty(info.TransactionHash))
                    throw new InvalidOperationException($"Circle tx {circleTxId} COMPLETE without an on-chain hash");
                return info.TransactionHash;
            }
            if (info.State is "FAILED" or "CANCELED" or "CANCELLED")
                throw new InvalidOperationException($"Circle tx {circleTxId} {info.State}: {info.Error}");
            await Task.Delay(2000, ct);
        }
        throw new InvalidOperationException($"Circle tx {circleTxId} did not complete within 90s");
    }

    private static string TokenIdToDecimal(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber).ToString();
    }

    private static string Normalize32(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return "0x" + h.PadLeft(64, '0').ToLowerInvariant();
    }
}
