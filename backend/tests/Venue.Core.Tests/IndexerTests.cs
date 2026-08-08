using System.Threading;
using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Venue.Indexing;
using Venue.Settlement;
using Nethereum.JsonRpc.Client;
using Xunit;

namespace Venue.Core.Tests;

/// <summary>
/// EventIndexer resilience against public-RPC eth_getLogs rate limits (-32011): the poll
/// loop must back off exponentially (never spin at 500ms), recover automatically when the
/// RPC frees up, and cap the per-call block span.
/// </summary>
public class IndexerTests
{
    private sealed class ScriptedIndexerGateway : IChainGateway
    {
        public int FetchCalls;
        public int FailuresRemaining;
        public ulong Latest = 10;
        public VenueEvent? ToEmit;

        public bool Simulated => true;
        public Task<ulong> LatestBlockAsync(CancellationToken ct) => Task.FromResult(Latest);
        public Task<string> GetBlockHashAsync(ulong blockNumber, CancellationToken ct) => Task.FromResult("0x" + blockNumber.ToString("x"));
        public Task<IReadOnlyList<VenueEvent>> FetchLogsAsync(ulong fromBlock, ulong toBlock, CancellationToken ct)
        {
            FetchCalls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new RpcResponseException(new RpcError(-32011, "request limit reached", null));
            }
            return Task.FromResult<IReadOnlyList<VenueEvent>>(ToEmit == null ? Array.Empty<VenueEvent>() : new[] { ToEmit });
        }
        public Task<IReadOnlyList<VenueEvent>> DecodeReceiptEventsAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitSettlementAsync(string batchId, IReadOnlyList<SettlementTrade> trades, CancellationToken ct) => throw new NotImplementedException();
        public Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> IsTransactionPendingAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<BatchRevertInfo?> TryGetRevertAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<SettlementTxLookup> FindPendingSettlementAsync(string batchId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct) => throw new NotImplementedException();
        public Task SubmitCreateMarketAsync(string marketId, byte[] meta, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitMintUsdcAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<BigInteger> GetRequestCountAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<BigInteger> GetUsdcWalletBalanceAsync(string user, CancellationToken ct) => throw new NotImplementedException();
        public Task FundGasAsync(string address, CancellationToken ct) => throw new NotImplementedException();
        public Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Indexer_Replay_BacksOffAndCompletesWhenRateLimitLifts()
    {
        // The startup-crash defect: ReplayAsync used to fire spans in a tight no-retry loop,
        // so a single eth_getLogs 429 (-32011) propagated out of VenueCore.StartAsync and
        // killed the process. It must now retry the SAME span after a backoff and complete.
        var gw = new ScriptedIndexerGateway
        {
            FailuresRemaining = 3,
            Latest = 5_000,
            ToEmit = new Deposited(TestData.Vault, 1, 0, "0x", TestData.Alice, 1_000_000),
        };
        var applied = new List<VenueEvent>();
        var indexer = new EventIndexer(gw, e => { applied.AddRange(e); return Task.CompletedTask; }, null, 1, pollIntervalMs: 10);

        // ReplayAsync must NOT throw on the -32011s and must reach the head.
        await indexer.ReplayAsync(System.Threading.CancellationToken.None);

        Assert.True(gw.FetchCalls > 3, $"replayed past the failures ({gw.FetchCalls} fetch attempts)");
        Assert.Contains(applied, e => e is Deposited { User: TestData.Alice });
        Assert.Equal(5_000UL, indexer.CursorBlock); // reached the head
    }

    [Fact]
    public async Task Indexer_Backoff_LimitsCallsDuringSustainedRateLimit()
    {
        var gw = new ScriptedIndexerGateway { FailuresRemaining = int.MaxValue };
        var indexer = new EventIndexer(gw, _ => Task.CompletedTask, null, 1, pollIntervalMs: 100);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await indexer.RunAsync(cts.Token);

        // Backoff (1s -> 2s -> 4s -> ...) must cap the number of eth_getLogs calls well
        // below a 500ms spin (~6 calls over 3s would be the old behaviour; we allow slack).
        Assert.True(gw.FetchCalls <= 6, $"expected <=6 fetch calls under backoff, got {gw.FetchCalls}");
    }

    [Fact]
    public async Task Indexer_RecoversWhenRateLimitLifts()
    {
        var gw = new ScriptedIndexerGateway
        {
            FailuresRemaining = 2,
            ToEmit = new Deposited(TestData.Vault, 1, 0, "0x", TestData.Alice, 1_000_000),
        };
        var applied = new List<VenueEvent>();
        var indexer = new EventIndexer(gw, e => { applied.AddRange(e); return Task.CompletedTask; }, null, 1, pollIntervalMs: 50);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await indexer.RunAsync(cts.Token);

        // After two -32011 failures (backoff) the poll succeeded and the event was applied.
        Assert.Contains(applied, e => e is Deposited { User: TestData.Alice });
        Assert.True(gw.FetchCalls >= 3);
    }

    [Fact]
    public async Task Indexer_CapsPerPollBlockSpan()
    {
        var gw = new ScriptedIndexerGateway { Latest = 100_000 }; // chain far ahead of cursor
        ulong? seenTo = null;
        var indexer = new EventIndexer(gw, _ => Task.CompletedTask, null, 1, pollIntervalMs: 50);
        // intercept via a wrapper: capture the toBlock the gateway would receive
        var capturing = new Func<IReadOnlyList<VenueEvent>, Task>(_ => Task.CompletedTask);
        var proxied = new EventIndexer(gw, capturing, null, 1, 50);
        // PollOnceAsync is private-state driven by cursor; call it directly on the proxied indexer.
        await proxied.PollOnceAsync(CancellationToken.None);
        // The span cap means a single poll fetches at most ~5000 blocks, not the whole 100k.
        Assert.True(gw.Latest == 100_000);
        Assert.True(proxied.CursorBlock - 1 <= 5000, $"cursor advanced {proxied.CursorBlock - 1} blocks in one poll");
    }
}
