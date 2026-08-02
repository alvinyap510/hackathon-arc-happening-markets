using System.Numerics;
using Venue.Domain;
using Venue.Ledger;
using Venue.Infrastructure;
using Xunit;
using VaultLedger = Venue.Ledger.Ledger;

namespace Venue.Core.Tests;

public class LedgerTests
{
    private static (VaultLedger VaultLedger, Dictionary<string, Outcome> Resolved) NewLedgerWithRegistry()
    {
        var resolved = new Dictionary<string, Outcome>();
        var ledger = new VaultLedger(TestData.Vault, m => resolved.TryGetValue(Hash.NormalizeBytes32(m), out var o) ? o : null);
        return (ledger, resolved);
    }

    /// <summary>
    /// The core rebuild guarantee (PLAN_BACKEND §1 + REBUILD RULE): chainFree equals the
    /// contract's usdcBal - lockedBal for a scripted sequence covering every granular event.
    /// The granular events are applied once; the summary events (BatchSettled, RfmFill,
    /// BondSlashed) are never applied.
    /// </summary>
    [Fact]
    public void Rebuild_MatchesContractFreeBalance_AcrossAllGranularEvents()
    {
        var (ledger, resolved) = NewLedgerWithRegistry();
        var market = Hash.NormalizeBytes32("0xAAAA");
        var yesId = Assets.TokenId(market, Outcome.Yes);

        var events = new VenueEvent[]
        {
            // deposits
            new Deposited(TestData.Vault, 1, 0, "0x", TestData.Alice, 10_000_000),
            new Deposited(TestData.Vault, 2, 0, "0x", TestData.Bob, 5_000_000),
            // lock / release / consume (free accounting: locked -, release +, consume neutral to funder, + to recipient)
            new Locked(TestData.Vault, 3, 0, "0x", Ref(1), TestData.Alice, 1_000_000),
            new LockReleased(TestData.Vault, 4, 0, "0x", Ref(1), TestData.Alice, 400_000),
            new LockConsumed(TestData.Vault, 5, 0, "0x", Ref(2), TestData.Bob, 300_000, TestData.Alice),
            // transfer
            new USDCMoved(TestData.Vault, 6, 0, "0x", TestData.Alice, TestData.Bob, 2_000_000, TradeId(1)),
            new TokensMoved(TestData.Vault, 7, 0, "0x", TestData.Alice, TestData.Bob, yesId, 500, TradeId(2)),
            // mint (FREE funding debits) + tokens allocated
            new PairMinted(TestData.Vault, 8, 0, "0x", market,
                new[] { new Allocation(TestData.Alice, 1000) },
                new[] { new Allocation(TestData.Bob, 1000) },
                new[] { new Funding(FundingKind.Free, "", TestData.Alice, 400), new Funding(FundingKind.Free, "", TestData.Bob, 600) },
                1000),
            // burn (credits split by yesCredit)
            new PairBurned(TestData.Vault, 9, 0, "0x", market, TestData.Alice, TestData.Bob, 1000, 400),
            // resolve then redeem (winning token debited, USDC credited)
            new MarketResolved(TestData.Ot, 10, 0, "0x", market, Outcome.Yes),
            new Redeemed(TestData.Vault, 11, 0, "0x", TestData.Alice, market, 100),
            // OutcomeTokens redeem (pool pays wallet-held tokens) must be IGNORED
            new Redeemed(TestData.Ot, 12, 0, "0x", TestData.Carol, market, 50),
            // summary events — must NOT change any balance
            new BatchSettled(TestData.Exchange, 13, 0, "0x", BatchId(1), new[] { TradeId(1), TradeId(2) }),
            new RfmFill(TestData.Rfm, 14, 0, "0x", 1, TestData.Alice, 400, 100),
            new BondSlashed(TestData.Rfm, 15, 0, "0x", 1, TestData.Alice, TestData.Bob),
        };

        foreach (var e in events)
        {
            if (e is MarketResolved mr) resolved[Hash.NormalizeBytes32(mr.MarketId)] = mr.Outcome;
            ledger.Apply(e);
        }

        // alice: 10M - 1M(lock) + 400k(release) + 300k(consume) - 2M(usdc moved) - 400(mint) + 400(burn) + 100(redeem)
        var expectedAlice = 10_000_000 - 1_000_000 + 400_000 + 300_000 - 2_000_000 - 400 + 400 + 100;
        // bob: 5M + 2M - 600(mint) + 600(burn)  (consume neutral to bob)
        var expectedBob = 5_000_000 + 2_000_000 - 600 + 600;

        Assert.Equal(new BigInteger(expectedAlice), ledger.ChainFree(TestData.Alice));
        Assert.Equal(new BigInteger(expectedBob), ledger.ChainFree(TestData.Bob));

        // token positions: alice's YES = +1000(mint) -1000(burn) -100(redeem) = -100 -> 0; moved -500 -> 0
        Assert.Equal(BigInteger.Zero, ledger.Position(TestData.Alice, yesId));
        // bob: +1000(mint) -1000(burn) + 500(moved) 
        Assert.Equal(new BigInteger(500), ledger.Position(TestData.Bob, yesId));

        // the four-party summaries changed nothing measurable: re-apply them and diff.
        var before = ledger.ChainFree(TestData.Alice);
        ledger.Apply(new BatchSettled(TestData.Exchange, 16, 0, "0x", BatchId(2), new[] { TradeId(3) }));
        ledger.Apply(new RfmFill(TestData.Rfm, 17, 0, "0x", 1, TestData.Alice, 400, 100));
        ledger.Apply(new BondSlashed(TestData.Rfm, 18, 0, "0x", 1, TestData.Alice, TestData.Bob));
        Assert.Equal(before, ledger.ChainFree(TestData.Alice));
    }

    [Fact]
    public void Rebuild_FromScratch_IsIdempotent()
    {
        var (ledger, resolved) = NewLedgerWithRegistry();
        var events = new VenueEvent[]
        {
            new Deposited(TestData.Vault, 1, 0, "0x", TestData.Alice, 1_000),
            new Locked(TestData.Vault, 2, 0, "0x", Ref(9), TestData.Alice, 100),
            new Withdrawn(TestData.Vault, 3, 0, "0x", TestData.Alice, 200),
        };
        ledger.Rebuild(events);
        Assert.Equal(new BigInteger(700), ledger.ChainFree(TestData.Alice)); // 1000 - 100 - 200

        ledger.Rebuild(events); // replay must produce the identical result
        Assert.Equal(new BigInteger(700), ledger.ChainFree(TestData.Alice));
    }

    [Fact]
    public void Reservations_AreAssetScoped_AndNotRebuiltFromEvents()
    {
        var (ledger, _) = NewLedgerWithRegistry();
        TestData.SeedUsdc(ledger, TestData.Alice, 1_000);
        ledger.Reserve(TestData.Alice, Assets.Usdc, 300);
        Assert.Equal(new BigInteger(700), ledger.Available(TestData.Alice, Assets.Usdc));
        Assert.Equal(new BigInteger(300), ledger.Reserved(TestData.Alice, Assets.Usdc));

        // A rebuild clears off-chain reservations (they are volatile, not chain state).
        ledger.Rebuild(new[] { new Deposited(TestData.Vault, 1, 0, "0x", TestData.Alice, 1_000) });
        Assert.Equal(new BigInteger(1_000), ledger.Available(TestData.Alice, Assets.Usdc));
        Assert.Equal(BigInteger.Zero, ledger.Reserved(TestData.Alice, Assets.Usdc));
    }

    private static string Ref(int n) => Hash.NormalizeBytes32("0x" + n.ToString("x"));
    private static string TradeId(int n) => Hash.NormalizeBytes32("0x" + n.ToString("x"));
    private static string BatchId(int n) => Hash.NormalizeBytes32("0x" + n.ToString("x"));
}
