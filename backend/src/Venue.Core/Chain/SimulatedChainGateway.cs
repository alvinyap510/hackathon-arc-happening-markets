using System.Numerics;
using Venue.Domain;
using Venue.Settlement;

#pragma warning disable CS1998 // sync implementations of an async interface (in-memory "node")

namespace Venue.Chain;

/// <summary>
/// Simulated Arc RPC for the local demo and tests. The in-memory SimulatedContract is
/// the "node": it validates like the real contracts and emits their exact events into a
/// log stream that the indexer consumes — so the whole backend runs unchanged against
/// either a real RPC or this seam. Restart replays the stored log, exercising the same
/// rebuild path as production.
/// </summary>
public sealed class SimulatedChainGateway : IChainGateway
{
    private readonly SimulatedContract _contract;
    private readonly object _sync = new();
    private readonly List<(ulong Block, ulong LogIndex, VenueEvent Event)> _logs = new();
    private readonly Dictionary<string, List<VenueEvent>> _txEvents = new();
    private readonly Dictionary<string, SettlementReceipt> _settleOutcomes = new();
    private readonly Dictionary<string, string> _batchTx = new(); // batchId -> txHash
    private ulong _latestBlock;
    private ulong _txCounter;

    public bool Simulated => true;

    public SimulatedChainGateway(ChainConfig cfg)
    {
        _contract = new SimulatedContract(
            cfg.NormalizedVault, cfg.NormalizedOutcomeTokens, cfg.NormalizedExchange, cfg.NormalizedRfm, cfg.OperatorAddress);
    }

    public Task<ulong> LatestBlockAsync(CancellationToken ct)
        => Task.FromResult(_latestBlock);

    public Task<string> GetBlockHashAsync(ulong blockNumber, CancellationToken ct)
        => Task.FromResult("0x" + blockNumber.ToString("x16"));

    public Task<IReadOnlyList<VenueEvent>> FetchLogsAsync(ulong fromBlock, ulong toBlock, CancellationToken ct)
    {
        lock (_sync)
        {
            var events = _logs.Where(l => l.Block >= fromBlock && l.Block <= toBlock).OrderBy(l => l.Block).ThenBy(l => l.LogIndex)
                .Select(l => l.Event).ToList();
            return Task.FromResult<IReadOnlyList<VenueEvent>>(events);
        }
    }

    public Task<IReadOnlyList<VenueEvent>> DecodeReceiptEventsAsync(string txHash, CancellationToken ct)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<VenueEvent>>(_txEvents.TryGetValue(txHash, out var e) ? e : Array.Empty<VenueEvent>());
    }

    public async Task<string> SubmitSettlementAsync(string batchId, IReadOnlyList<SettlementTrade> trades, CancellationToken ct)
    {
        var txHash = RecordSettlement(r => _contract.SettleBatch(batchId, trades, r));
        lock (_sync) _batchTx[Infrastructure.Hash.NormalizeBytes32(batchId)] = txHash;
        return txHash;
    }

    public async Task<string?> FindPendingSettlementAsync(string batchId, CancellationToken ct)
    {
        lock (_sync)
            return _batchTx.TryGetValue(Infrastructure.Hash.NormalizeBytes32(batchId), out var tx) ? tx : null;
    }

    public async Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct)
    {
        lock (_sync)
            return _settleOutcomes.TryGetValue(txHash, out var o) ? o : new SettlementReceipt(TxStatus.Unknown, null);
    }

    public async Task<bool> IsTransactionPendingAsync(string txHash, CancellationToken ct)
        => false; // simulated txs mine instantly; a pending state never exists

    public async Task<BatchRevertInfo?> TryGetRevertAsync(string txHash, CancellationToken ct)
    {
        lock (_sync)
            return _settleOutcomes.TryGetValue(txHash, out var o) ? o.Revert : null;
    }

    public async Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct)
    {
        var txHash = Record(r => _contract.Finalize(requestId, r));
        if (_settleOutcomes.TryGetValue(txHash, out var o) && o.Status == TxStatus.Reverted)
            throw new InvalidOperationException($"finalize reverted: {o.Revert?.ErrorName}");
        return txHash;
    }

    public async Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct)
        => Record(r => _contract.Resolve(marketId, outcome, r));

    public async Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct)
        => Record(r => _contract.Deposit(user, amt, r));

    public async Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct)
        => Record(r => _contract.Withdraw(user, amt, r));

    public async Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct)
        => Record(r => _contract.DepositTokens(user, tokenId, amt, r));

    public async Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct)
        => Record(r => _contract.WithdrawTokens(user, tokenId, amt, r));

    public async Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct)
        => Record(r => _contract.Redeem(user, marketId, amt, r));

    public async Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct)
        => Record(r => _contract.PostRequest(user, market, side, quantity, maxPriceTick, minMatch, commitDeadline, revealDeadline, r));

    public async Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct)
        => Record(r => _contract.CommitQuote(user, requestId, commitHash, r));

    public async Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct)
        => Record(r => _contract.RevealQuote(user, requestId, priceTick, size, salt, r));

    public async Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct)
        => Record(r => _contract.CancelRequest(user, requestId, r));

    public Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_txEvents.ContainsKey(txHash)) return Task.FromResult(TxStatus.Confirmed);
            if (_settleOutcomes.TryGetValue(txHash, out var o))
                return Task.FromResult(o.Status == TxStatus.Reverted ? TxStatus.Reverted : TxStatus.Confirmed);
            return Task.FromResult(TxStatus.Pending);
        }
    }

    /// <summary>Expose the raw simulated contract (tests + demo seeders may drive it directly).</summary>
    public SimulatedContract Contract => _contract;

    private string Record(Func<TxCtx, SimOpResult> op)
    {
        var (txHash, result) = Execute(op);
        if (!result.Success) throw new SimulationRevertException(result.Revert?.ErrorName ?? "revert");
        return txHash;
    }

    /// <summary>Settlement submits never throw: a rejected batch is surfaced to the batcher via the
    /// settlement receipt (AwaitSettlementAsync), which drives the repair loop.</summary>
    private string RecordSettlement(Func<TxCtx, SimOpResult> op)
    {
        var (txHash, result) = Execute(op);
        return txHash;
    }

    private (string TxHash, SimOpResult Result) Execute(Func<TxCtx, SimOpResult> op)
    {
        lock (_sync)
        {
            var block = ++_latestBlock;
            var txHash = "0x" + (++_txCounter).ToString("x16");
            var ctx = new TxCtx { BlockNumber = block, TxHash = txHash };
            var result = op(ctx);
            var events = new List<VenueEvent>(result.Events);
            foreach (var e in events)
                _logs.Add((e.BlockNumber, e.LogIndex, e));
            _txEvents[txHash] = events;
            _settleOutcomes[txHash] = result.Success
                ? new SettlementReceipt(TxStatus.Confirmed, null)
                : new SettlementReceipt(TxStatus.Reverted, result.Revert);
            return (txHash, result);
        }
    }
}

/// <summary>Thrown when the simulated contract rejects a user op (maps to HTTP 400 in the API).</summary>
public sealed class SimulationRevertException : Exception
{
    public SimulationRevertException(string reason) : base($"simulated contract reverted: {reason}")
    {
        Reason = reason;
    }

    public string Reason { get; }
}
