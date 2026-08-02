using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;
using Venue.Ledger;
using Venue.Rfm;
using TradingEngine = Venue.Engine.Engine;
using VaultLedger = Venue.Ledger.Ledger;

namespace Venue.Core.Tests;

/// <summary>Shared fixtures for venue tests.</summary>
public static class TestData
{
    public const string Vault = "0x0000000000000000000000000000000000000001";
    public const string Ot = "0x0000000000000000000000000000000000000002";
    public const string Exchange = "0x0000000000000000000000000000000000000003";
    public const string Rfm = "0x0000000000000000000000000000000000000004";
    public const string Usdc = "0x3600000000000000000000000000000000000000";
    public const string Operator = "0x0000000000000000000000000000000000000005";

    public const string Alice = "0x00000000000000000000000000000000000000a1";
    public const string Bob = "0x00000000000000000000000000000000000000b2";
    public const string Carol = "0x00000000000000000000000000000000000000c3";

    public static ChainConfig Cfg => new("https://rpc.devnet.arc.io", 5042002, 0, Vault, Ot, Exchange, Rfm, Usdc, Operator);

    public static VaultLedger NewLedger(Func<string, Outcome?>? resolved = null)
        => new(Vault, resolved ?? (_ => null));

    public static TradingEngine NewEngine(VaultLedger ledger, string marketId)
    {
        var markets = new Dictionary<string, Market> { [Hash.NormalizeBytes32(marketId)] = new() { MarketId = Hash.NormalizeBytes32(marketId), Exists = true } };
        return new TradingEngine(ledger, markets);
    }

    public static Market MarketFor(string marketId)
        => new() { MarketId = Hash.NormalizeBytes32(marketId), Exists = true };

    public static void SeedUsdc(VaultLedger ledger, string user, BigInteger amt)
        => ledger.Apply(new Deposited(Vault, 1, 0, "0xseed", user, amt));

    public static void SeedTokens(VaultLedger ledger, string user, string marketId, Outcome outcome, BigInteger amt)
        => ledger.Apply(new TokensDeposited(Vault, 1, 0, "0xseedt", user, Assets.TokenId(marketId, outcome), amt));

    public static VenueEvent Ev(ulong block, ulong index) => new Deposited(Vault, block, index, "0xtx", Alice, 1);
}
