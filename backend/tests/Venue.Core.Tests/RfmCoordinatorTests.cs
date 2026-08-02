using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;
using Venue.Rfm;
using Venue.Settlement;
using Xunit;

namespace Venue.Core.Tests;

public class RfmCoordinatorTests
{
    [Fact]
    public void PhaseMirror_DerivesPhaseLikeTheContract()
    {
        var gateway = new RecordingGateway();
        var coord = new RfmCoordinator(gateway);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var posted = new RequestPosted(TestData.Rfm, 1, 0, "0x", 1, Market(0x1), RfmSide.Yes, 1_000_000, 700, 500_000, now + 120, now + 180, 700_000, 15_625);
        coord.Apply(posted);
        var req = coord.Get(1);
        Assert.NotNull(req);
        Assert.Equal(RfmPhase.Open, req!.PhaseAt(now + 10));

        coord.Apply(new QuoteCommitted(TestData.Rfm, 2, 0, "0x", 1, TestData.Alice, 0));
        Assert.Equal(RfmPhase.Commit, req.PhaseAt(now + 10));
        Assert.Equal(BigInteger.One, req.CommitCount);

        coord.Apply(new QuoteRevealed(TestData.Rfm, 3, 0, "0x", 1, TestData.Alice, 600, 200_000, true));
        var reveal = Assert.Single(req.Reveals);
        Assert.Equal(TestData.Alice, reveal.Mm);
        Assert.True(reveal.InRange);

        // still inside the reveal window -> REVEAL, not finalize-ready
        Assert.Equal(RfmPhase.Reveal, req.PhaseAt(now + 130));
        Assert.False(req.FinalizeReadyAt(now + 130));

        // after revealDeadline + margin -> ready
        Assert.True(req.FinalizeReadyAt(now + 200));
        Assert.Contains(coord.ReadyToFinalize(now + 200), id => id == 1);

        coord.Apply(new MarketBorn(TestData.Rfm, 4, 0, "0x", 1, Market(0x2), 400, 412, 600_000, RfmSide.Yes));
        coord.Apply(new RequestFinalized(TestData.Rfm, 5, 0, "0x", 1));
        Assert.Equal(RfmPhase.Finalized, req.PhaseAt(now + 200));
        Assert.Empty(coord.ReadyToFinalize(now + 200));
        Assert.Equal(Market(0x2), req.MarketId);
    }

    [Fact]
    public async Task Crank_SubmitsFinalizeForReadyRequests()
    {
        var gateway = new RecordingGateway();
        var coord = new RfmCoordinator(gateway);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        coord.Apply(new RequestPosted(TestData.Rfm, 1, 0, "0x", 1, Market(0x3), RfmSide.No, 1_000_000, 700, 500_000, now + 10, now + 20, 700_000, 15_625));

        await coord.CrankAsync(coord.ReadyToFinalize(now + 100), CancellationToken.None);
        var finalize = Assert.Single(gateway.Finalized);
        Assert.Equal(BigInteger.One, finalize);
    }

    [Fact]
    public void SaltService_SaltSurvivesRestart_WithStableSecret()
    {
        // Two instances with the SAME configured secret = the process before/after a restart.
        var before = new SaltService("the-secret");
        var after = new SaltService("the-secret");
        var requestId = new BigInteger(7);
        var saltBefore = before.Derive(requestId, TestData.Bob);
        var saltAfter = after.Derive(requestId, TestData.Bob);

        Assert.Equal(saltBefore, saltAfter);
        Assert.NotEqual(BigInteger.Zero, saltBefore);

        // A different secret derives a different salt (no accidental reuse).
        Assert.NotEqual(saltBefore, new SaltService("another-secret").Derive(requestId, TestData.Bob));

        // The committed hash is exactly reproducible from the derived salt at reveal time.
        var commitHash = Hash.QuoteHash(5042002, TestData.Rfm, requestId, TestData.Bob, 600, 1_000_000, saltBefore);
        var revealHash = Hash.QuoteHash(5042002, TestData.Rfm, requestId, TestData.Bob, 600, 1_000_000, saltAfter);
        Assert.Equal(commitHash, revealHash);
    }

    [Fact]
    public void SaltService_EmptySecret_FailsFast()
    {
        // Refusing to boot without a stable secret is the durable-by-construction guarantee:
        // a randomly generated secret would change on restart and make every commit unrevealable.
        Assert.Throws<InvalidOperationException>(() => new SaltService(""));
        Assert.Throws<InvalidOperationException>(() => new SaltService("   "));
    }

    private static string Market(int n) => Hash.NormalizeBytes32("0x" + n.ToString("x"));

    private sealed class RecordingGateway : IChainGateway
    {
        public List<BigInteger> Finalized { get; } = new();
        public bool Simulated => true;
        public Task<ulong> LatestBlockAsync(CancellationToken ct) => Task.FromResult(0UL);
        public Task<string> GetBlockHashAsync(ulong blockNumber, CancellationToken ct) => Task.FromResult("0x0");
        public Task<IReadOnlyList<VenueEvent>> FetchLogsAsync(ulong fromBlock, ulong toBlock, CancellationToken ct) => Task.FromResult<IReadOnlyList<VenueEvent>>(Array.Empty<VenueEvent>());
        public Task<IReadOnlyList<VenueEvent>> DecodeReceiptEventsAsync(string txHash, CancellationToken ct) => Task.FromResult<IReadOnlyList<VenueEvent>>(Array.Empty<VenueEvent>());
        public Task<string> SubmitSettlementAsync(string batchId, IReadOnlyList<SettlementTrade> trades, CancellationToken ct) => throw new NotImplementedException();
        public Task<SettlementReceipt> AwaitSettlementAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> IsTransactionPendingAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<BatchRevertInfo?> TryGetRevertAsync(string txHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitFinalizeAsync(BigInteger requestId, CancellationToken ct)
        {
            Finalized.Add(requestId);
            return Task.FromResult("0xfin");
        }
        public Task<string> SubmitResolveAsync(string marketId, Outcome outcome, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitDepositAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawAsync(string user, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitDepositTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitWithdrawTokensAsync(string user, string tokenId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitPostRequestAsync(string user, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCommitQuoteAsync(string user, BigInteger requestId, string commitHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRevealQuoteAsync(string user, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitCancelRequestAsync(string user, BigInteger requestId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> SubmitRedeemAsync(string user, string marketId, BigInteger amt, CancellationToken ct) => throw new NotImplementedException();
        public Task<TxStatus> TxStatusAsync(string txHash, CancellationToken ct) => Task.FromResult(TxStatus.Pending);
    }
}
