using System.Numerics;
using Venue.Domain;
using Venue.Infrastructure;
using Xunit;

namespace Venue.Core.Tests;

public class HashTests
{
    [Fact]
    public void TokenId_IsDeterministic_AndDiffersByOutcome()
    {
        var market = Hash.NormalizeBytes32("0x1234");
        var yes = Hash.TokenId(market, Outcome.Yes);
        var no = Hash.TokenId(market, Outcome.No);
        var yes2 = Hash.TokenId(market, Outcome.Yes);

        Assert.Equal(yes, yes2);
        Assert.NotEqual(yes, no);
        Assert.StartsWith("0x", yes);
        Assert.Equal(66, yes.Length); // 0x + 64 hex
        Assert.StartsWith("0x", no);
    }

    [Fact]
    public void QuoteHash_MatchesSolidityShape_AndIsSensitiveToAllInputs()
    {
        const string rfm = "0x0000000000000000000000000000000000000004";
        const string mm = TestData.Alice;
        var baseHash = Hash.QuoteHash(5042002, rfm, 1, mm, 600, 200_000, 42);

        Assert.Equal(baseHash, Hash.QuoteHash(5042002, rfm, 1, mm, 600, 200_000, 42));
        Assert.NotEqual(baseHash, Hash.QuoteHash(5042002, rfm, 1, mm, 600, 200_000, 43));   // salt
        Assert.NotEqual(baseHash, Hash.QuoteHash(5042002, rfm, 1, mm, 601, 200_000, 42));   // tick
        Assert.NotEqual(baseHash, Hash.QuoteHash(5042002, rfm, 1, mm, 600, 199_999, 42));   // size
        Assert.NotEqual(baseHash, Hash.QuoteHash(5042002, rfm, 2, mm, 600, 200_000, 42));   // request
        Assert.NotEqual(baseHash, Hash.QuoteHash(5042002, rfm, 1, TestData.Bob, 600, 200_000, 42)); // mm
    }

    [Fact]
    public void TradeId_IsDeterministic_PerFillSeq()
    {
        var m = Hash.NormalizeBytes32("0xaa");
        var a = Hash.TradeId(m, "maker1", "taker1", 1);
        var b = Hash.TradeId(m, "maker1", "taker1", 1);
        var c = Hash.TradeId(m, "maker1", "taker1", 2);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Complement_MapsNoTicksToYesTicks()
    {
        Assert.Equal(400, Prices.Complement(600));
        Assert.Equal(600, Prices.Complement(400));
        Assert.Equal(1000, Prices.Complement(0));
    }

    [Fact]
    public void LegCost_IsFloorDivision_AndConservesWithCounterLeg()
    {
        Assert.Equal(new BigInteger(500), Prices.LegCost(1000, 500));
        Assert.Equal(new BigInteger(400), Prices.LegCost(1000, 400));
        // legA + counterLeg == size by construction (never independently rounded)
        var legA = Prices.LegCost(1000, 601);
        Assert.Equal(new BigInteger(601), legA);
        Assert.Equal(new BigInteger(1000), legA + Prices.CounterLeg(1000, legA));
    }
}
