using System.Numerics;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Venue.Domain;
using Venue.Settlement;

namespace Venue.Chain;

/// <summary>
/// Real Arc RPC gateway (Nethereum 6.x). One operator SCA signs settlement,
/// finalize and resolve (serialized nonce); user-facing ops are sent from the
/// user's dev-controlled SCA key resolved via <paramref name="userKeyResolver"/>
/// (Circle Wallets path in production; demo keys from env here). Revert data for a
/// failed whole batch is recovered by re-simulating the call and parsed by
/// RevertParser. The indexer uses eth_getLogs range catch-up with a
/// (blockNumber, blockHash, logIndex) cursor checked against the block hash.
/// </summary>
public sealed class NethereumChainGateway : IChainGateway
{
    private readonly ChainConfig _cfg;
    private readonly Web3 _operatorWeb3;
    private readonly EventDecoder _decoder;
    private readonly Func<string, string?> _userKeyResolver;
    private readonly SemaphoreSlim _settlementGate = new(1, 1);

    public bool Simulated => false;

    public NethereumChainGateway(ChainConfig cfg, string operatorPrivateKey, Func<string, string?> userKeyResolver)
    {
        _cfg = cfg;
        _userKeyResolver = userKeyResolver;
        _operatorWeb3 = CreateWeb3(operatorPrivateKey);
        _decoder = new EventDecoder(cfg);
    }

    public async Task<ulong> LatestBlockAsync(CancellationToken ct)
    {
        var block = await _operatorWeb3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        return (ulong)block.Value;
    }

    public async Task<string> GetBlockHashAsync(ulong blockNumber, CancellationToken ct)
    {
        var block = await _operatorWeb3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new HexBigInteger(blockNumber));
        return block?.BlockHash ?? "";
    }

    public async Task<IReadOnlyList<VenueEvent>> FetchLogsAsync(ulong fromBlock, ulong toBlock, CancellationToken ct)
    {
        if (toBlock < fromBlock) return Array.Empty<VenueEvent>();
        var filter = new NewFilterInput
        {
            FromBlock = new BlockParameter(new HexBigInteger(fromBlock)),
            ToBlock = new BlockParameter(new HexBigInteger(toBlock)),
            Address = new[] { _cfg.NormalizedVault, _cfg.NormalizedOutcomeTokens, _cfg.NormalizedExchange, _cfg.NormalizedRfm },
        };
        var logs = await _operatorWeb3.Eth.Filters.GetLogs.SendRequestAsync(filter);
        return _decoder.DecodeAll(logs);
    }

    public async Task<IReadOnlyList<VenueEvent>> DecodeReceiptEventsAsync(string txHash, CancellationToken ct)
    {
        var receipt = await _operatorWeb3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        if (receipt?.Logs == null) return Array.Empty<VenueEvent>();
        var decoded = new List<VenueEvent>();
        foreach (var log in receipt.Logs)
        {
            if (log is FilterLog fl)
            {
                var e = _decoder.Decode(fl);
                if (e != null) decoded.Add(e);
            }
        }
        return decoded;
    }

    // ----------------------------------------------------------- settlement

    public async Task<string> SubmitSettlementAsync(string batchId, IReadOnlyList<SettlementTrade> trades, CancellationToken ct)
    {
        await _settlementGate.WaitAsync(ct);
        try
        {
            var msg = new SettleBatchFunction
            {
                BatchId = Infrastructure.Hash.HexToBytes(batchId),
                Trades = trades.Select(ToTradeDto).ToList(),
            };
            var handler = _operatorWeb3.Eth.GetContractHandler(_cfg.NormalizedExchange);
            return await handler.SendRequestAsync(msg);
        }
        finally
        {
            _settlementGate.Release();
        }
    }

    public async Task<bool> IsTransactionPendingAsync(string txHash, CancellationToken ct)
    {
        var tx = await _operatorWeb3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txHash);
        return tx != null && tx.BlockNumber == null; // in the mempool, not yet mined
    }

    public async Task<BatchRevertInfo?> TryGetRevertAsync(string txHash, CancellationToken ct)
        => await RecoverRevertAsync(txHash, ct);

    public async Task<string?> FindPendingSettlementAsync(string batchId, CancellationToken ct)
    {
        // The batchId is the first calldata word after the settleBatch selector
        // (settleBatch(bytes32, tuple[])). Scan the pending pool then recent blocks for an
        // operator->exchange tx carrying it.
        var expected = Infrastructure.Hash.NormalizeBytes32(batchId)[2..];
        Func<Transaction, bool> isOurs = tx =>
            string.Equals(tx.From, _cfg.OperatorAddress, StringComparison.OrdinalIgnoreCase)
            && string.Equals(tx.To, _cfg.NormalizedExchange, StringComparison.OrdinalIgnoreCase)
            && tx.Input is { Length: >= 74 }
            && tx.Input.Substring(10, 64).Equals(expected, StringComparison.OrdinalIgnoreCase);

        try
        {
            var pending = await _operatorWeb3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(BlockParameter.CreatePending());
            var hit = pending?.Transactions?.FirstOrDefault(t => isOurs(t));
            if (hit != null) return hit.TransactionHash;

            var latest = await LatestBlockAsync(ct);
            for (var b = latest; b > latest - 25 && b > 0; b--)
            {
                var block = await _operatorWeb3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(new HexBigInteger(b));
                hit = block?.Transactions?.FirstOrDefault(t => isOurs(t));
                if (hit != null) return hit.TransactionHash;
            }
        }
        catch
        {
            // Node hiccup: report "not found" so the batcher does not unwind based on a guess;
            // it will re-run the reconciliation on the next attempt cycle.
        }
        return null;
    }

    public async Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var receipt = await _operatorWeb3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
            if (receipt != null && receipt.Status != null)
            {
                if (receipt.Status.Value == 1) return new SettlementReceipt(TxStatus.Confirmed, null);
                if (receipt.Status.Value == 0)
                {
                    var revert = await RecoverRevertAsync(txHash, ct);
                    return new SettlementReceipt(TxStatus.Reverted, revert);
                }
            }
            await Task.Delay(1000, ct);
        }
        return new SettlementReceipt(TxStatus.Unknown, null);
    }

    private async Task<Settlement.BatchRevertInfo?> RecoverRevertAsync(string txHash, CancellationToken ct)
    {
        try
        {
            var tx = await _operatorWeb3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txHash);
            if (tx?.BlockNumber == null) return null;
            var call = new CallInput(tx.Input, _cfg.NormalizedExchange)
            {
                From = _cfg.OperatorAddress,
                Value = tx.Value,
            };
            var block = new BlockParameter(tx.BlockNumber);
            var raw = await ReplayCallAsync(call, block, ct);
            return raw == null ? null : Settlement.RevertParser.Parse(raw);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ReplayCallAsync(CallInput call, BlockParameter block, CancellationToken ct)
    {
        try
        {
            // eth_call returns the raw revert data as the result on some nodes.
            return await _operatorWeb3.Eth.Transactions.Call.SendRequestAsync(call, block);
        }
        catch (RpcResponseException ex)
        {
            return ex.RpcError?.GetDataAsString();
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------- coordinator ops

    public async Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct)
    {
        var handler = _operatorWeb3.Eth.GetContractHandler(_cfg.NormalizedRfm);
        return await handler.SendRequestAsync(new FinalizeFunction { RequestId = requestId });
    }

    public async Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct)
    {
        var handler = _operatorWeb3.Eth.GetContractHandler(_cfg.NormalizedOutcomeTokens);
        return await handler.SendRequestAsync(new ResolveFunction { MarketId = Infrastructure.Hash.HexToBytes(marketId), Outcome = (byte)outcome });
    }

    // ----------------------------------------------------------- user ops

    public async Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedVault, new DepositFunction { Amt = amt }, ct);

    public async Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedVault, new WithdrawFunction { Amt = amt }, ct);

    public async Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedVault, new DepositTokensFunction { Id = HashToUint(tokenId), Amt = amt }, ct);

    public async Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedVault, new WithdrawTokensFunction { Id = HashToUint(tokenId), Amt = amt }, ct);

    private static BigInteger HashToUint(string hex)
    {
        var h = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);
    }

    public async Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedVault, new VaultRedeemFunction { MarketId = Infrastructure.Hash.HexToBytes(marketId), Amt = amt }, ct);

    public async Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedRfm, new PostRequestFunction
        {
            Market = Infrastructure.Hash.HexToBytes(market),
            Side = (byte)side,
            Quantity = quantity,
            MaxPriceTick = maxPriceTick,
            MinMatch = minMatch,
            CommitDeadline = commitDeadline,
            RevealDeadline = revealDeadline,
        }, ct);

    public async Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedRfm, new CommitQuoteFunction { RequestId = requestId, CommitHash = Infrastructure.Hash.HexToBytes(commitHash) }, ct);

    public async Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedRfm, new RevealQuoteFunction { RequestId = requestId, PriceTick = priceTick, Size = size, Salt = salt }, ct);

    public async Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct)
        => await SendAsUser(user, _cfg.NormalizedRfm, new RfmCancelFunction { RequestId = requestId }, ct);

    public async Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct)
    {
        var receipt = await _operatorWeb3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        if (receipt == null) return TxStatus.Pending;
        if (receipt.Status == null) return TxStatus.Pending;
        return receipt.Status.Value == 1 ? TxStatus.Confirmed : TxStatus.Reverted;
    }

    // ------------------------------------------------------------ internal

    private static TradeStructDto ToTradeDto(SettlementTrade t) => new()
    {
        TradeId = Infrastructure.Hash.HexToBytes(t.TradeId),
        MarketId = Infrastructure.Hash.HexToBytes(t.MarketId),
        Class = (byte)t.Class,
        Outcome = (byte)(t.Outcome ?? Outcome.Yes),
        PartyA = Domain.Addresses.Normalize(t.PartyA),
        PartyB = Domain.Addresses.Normalize(t.PartyB),
        OutcomeTick = t.OutcomeTick,
        Size = t.Size,
    };

    private Web3 CreateWeb3(string privateKeyHex)
    {
        var account = new Account(new EthECKey(privateKeyHex), _cfg.ChainId);
        return new Web3(account, _cfg.RpcUrl, null, null);
    }

    private async Task<string> SendAsUser<T>(string user, string to, T msg, CancellationToken ct)
        where T : FunctionMessage, new()
    {
        var key = _userKeyResolver(Domain.Addresses.Normalize(user));
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("no SCA key for user (requires Circle Wallets dev-controlled session)");
        var web3 = CreateWeb3(key);
        var handler = web3.Eth.GetContractHandler(to);
        return await handler.SendRequestAsync(msg);
    }
}
