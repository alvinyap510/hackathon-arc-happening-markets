using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;
using Xunit;

namespace Venue.Core.Tests;

/// <summary>SimulatedContract semantics: whole-batch atomicity, RFM pool funding, lock refs.</summary>
public class SimulatedContractTests
{
    private static readonly string Market = Hash.NormalizeBytes32("0xDDDD");
    private static readonly string YesId = Assets.TokenId(Market, Outcome.Yes);

    private static SimulatedContract NewContract()
    {
        var cfg = TestData.Cfg;
        return new SimulatedContract(cfg.NormalizedVault, cfg.NormalizedOutcomeTokens, cfg.NormalizedExchange, cfg.NormalizedRfm, cfg.OperatorAddress);
    }

    private static void Deposit(SimulatedContract c, string user, BigInteger usdc, BigInteger yesTokens = default)
    {
        c.Deposit(user, usdc, new TxCtx { BlockNumber = 1, TxHash = "0xdep" });
        if (yesTokens > 0) c.DepositTokens(user, YesId, yesTokens, new TxCtx { BlockNumber = 1, TxHash = "0xdept" });
    }

    private static SettlementTrade Transfer(int i, string seller, string buyer, BigInteger size, long tick)
        => new(TradeId(i), Market, TradeClass.Transfer, Outcome.Yes, seller, buyer, tick, size);

    private static SettlementTrade Mint(int i, string yesParty, string noParty, BigInteger size, long tick)
        => new(TradeId(i), Market, TradeClass.Mint, null, yesParty, noParty, tick, size);

    private static string TradeId(int i) => Hash.NormalizeBytes32("0x" + (1000 + i).ToString("x"));

    [Fact]
    public void SettleBatch_WholeBatchAtomicity_RollsBackEarlierTradesOnLaterFailure()
    {
        var c = NewContract();
        Deposit(c, TestData.Alice, 1_000, yesTokens: 200);
        Deposit(c, TestData.Bob, 1_000);
        Deposit(c, TestData.Carol, 10); // too little to fund the MINT in trade 1

        var trades = new[] { Transfer(1, TestData.Alice, TestData.Bob, 100, 500), Mint(2, TestData.Carol, TestData.Bob, 100, 600) };
        var result = c.SettleBatch(Hash.NormalizeBytes32("0xb1"), trades, new TxCtx { BlockNumber = 2, TxHash = "0xsb" });

        Assert.False(result.Success);
        Assert.Equal(1, result.Revert!.FailIndex);
        Assert.Empty(result.Events); // whole batch reverted: no events, no state

        // Trade 0's transfer must have been rolled back too.
        Assert.Equal(new BigInteger(200), c.TokenOf(TestData.Alice, YesId));
        Assert.Equal(new BigInteger(1_000), c.UsdcOf(TestData.Bob));
        Assert.Equal(new BigInteger(1_000), c.UsdcOf(TestData.Alice));

        // The reverted batch's usedTradeIds were rolled back: the same trade 0 now settles alone.
        var retry = c.SettleBatch(Hash.NormalizeBytes32("0xb2"), new[] { trades[0] }, new TxCtx { BlockNumber = 3, TxHash = "0xsb2" });
        Assert.True(retry.Success);
        Assert.Equal(3, retry.Events.Count); // USDCMoved + TokensMoved + BatchSettled
        Assert.Equal(new BigInteger(100), c.TokenOf(TestData.Alice, YesId));
        Assert.Equal(new BigInteger(950), c.UsdcOf(TestData.Bob));
    }

    [Fact]
    public void SettleBatch_SuccessfulBatchAppliesAllTrades()
    {
        var c = NewContract();
        Deposit(c, TestData.Alice, 1_000, yesTokens: 100);
        Deposit(c, TestData.Bob, 1_000);

        var result = c.SettleBatch(Hash.NormalizeBytes32("0xb3"), new[] { Transfer(3, TestData.Alice, TestData.Bob, 100, 500) }, new TxCtx { BlockNumber = 2, TxHash = "0xsb" });
        Assert.True(result.Success);
        Assert.Equal(3, result.Events.Count); // USDCMoved + TokensMoved + BatchSettled
        Assert.Equal(new BigInteger(950), c.UsdcOf(TestData.Bob));
        Assert.Equal(BigInteger.Zero, c.TokenOf(TestData.Alice, YesId));
        Assert.Equal(new BigInteger(100), c.TokenOf(TestData.Bob, YesId));
    }
}
