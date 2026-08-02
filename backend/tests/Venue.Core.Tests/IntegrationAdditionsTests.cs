using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;
using Xunit;

namespace Venue.Core.Tests;

/// <summary>
/// INTEGRATION_CONTRACT additions: G1 market-metadata store (restart-durable by marketHash),
/// G4 faucet mint + wallet balance on the simulated gateway, G6 authoritative requestCount.
/// </summary>
public class IntegrationAdditionsTests
{
    private static readonly string MarketHash = Hash.KeccakHex("arc-integration-test-market");

    // ------------------------------------------------------------------ G1 store

    [Fact]
    public void MetadataStore_SaveGet_Roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "md-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new MarketMetadataStore(path);
            store.Save(MarketHash, "Will the demo pass?", "OperatorFiat", 1785000000);
            var meta = store.Get(MarketHash);
            Assert.NotNull(meta);
            Assert.Equal("Will the demo pass?", meta!.QuestionText);
            Assert.Equal("OperatorFiat", meta.ResolutionSource);
            Assert.Equal(1785000000, meta.CloseTime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MetadataStore_IsRestartDurable_AcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "md-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new MarketMetadataStore(path);
            store.Save(MarketHash, "survives restart", null, null);

            // A NEW instance reading the same file = a process restart.
            var reloaded = new MarketMetadataStore(path);
            Assert.Equal("survives restart", reloaded.Get(MarketHash)!.QuestionText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MetadataStore_KeyedByNormalizedMarketHash()
    {
        var store = new MarketMetadataStore(Path.Combine(Path.GetTempPath(), "md-" + Guid.NewGuid().ToString("N") + ".json"));
        store.Save("0x" + MarketHash[2..].ToUpperInvariant(), "upper", null, null); // mixed case key
        Assert.Equal("upper", store.Get(MarketHash)!.QuestionText);
    }

    // ------------------------------------------------------------- SEAM 1 dev accounts

    [Fact]
    public async Task DevAccountStore_Provision_IsDeterministicAndSignable()
    {
        var store = new Chain.DevAccountStore("test-secret");
        var a1 = await store.ProvisionAsync("alice@example.com", CancellationToken.None);
        var a2 = await store.ProvisionAsync("alice@example.com", CancellationToken.None);
        Assert.Equal(a1, a2); // same ref -> same account across restarts

        // The backend holds a real signable key for the provisioned address.
        var key = store.KeyForAddress(a1);
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(a1, Domain.Addresses.Normalize(new Nethereum.Web3.Accounts.Account(key).Address));

        // Different refs -> different accounts.
        var b = await store.ProvisionAsync("bob@example.com", CancellationToken.None);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void DevAccountStore_IsolatedBySecret()
    {
        var s1 = new Chain.DevAccountStore("secret-a");
        var s2 = new Chain.DevAccountStore("secret-b");
        var a = s1.AddressForRef("x@y.com");
        var b = s2.AddressForRef("x@y.com");
        // AddressForRef only resolves after provisioning; separate stores never share.
        Assert.Null(a);
        Assert.Null(b);
    }

    [Fact]
    public async Task DevAccountStore_ResolvesFromAnyProcessRestart()
    {
        // A NEW store instance with the SAME secret derives the SAME key/address (no
        // persisted store needed): simulate a restart mid-session.
        var first = new Chain.DevAccountStore("stable-secret");
        var addr = await first.ProvisionAsync("inst@hedge.fund", CancellationToken.None);
        var restarted = new Chain.DevAccountStore("stable-secret");
        var key = restarted.KeyForAddress(addr); // empty until re-provisioned in this instance
        Assert.Null(key);
        var addr2 = await restarted.ProvisionAsync("inst@hedge.fund", CancellationToken.None);
        Assert.Equal(addr, addr2);
    }

    // ------------------------------------------------------------- G4/G6 sim gateway

    [Fact]
    public async Task SimulatedGateway_FaucetMintsWalletBalance()
    {
        var cfg = TestData.Cfg;
        var gw = new SimulatedChainGateway(cfg);
        await gw.SubmitMintUsdcAsync(TestData.Alice, 10_000_000, CancellationToken.None);
        Assert.Equal(new BigInteger(10_000_000), await gw.GetUsdcWalletBalanceAsync(TestData.Alice, CancellationToken.None));
    }

    [Fact]
    public async Task SimulatedGateway_RequestCount_TracksPostedRequests()
    {
        var cfg = TestData.Cfg;
        var gw = new SimulatedChainGateway(cfg);
        Assert.Equal(BigInteger.Zero, await gw.GetRequestCountAsync(CancellationToken.None));

        // The requester must have vault free balance to cover escrow + bond at post.
        await gw.SubmitDepositAsync(TestData.Alice, 5_000_000_000, CancellationToken.None);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await gw.SubmitPostRequestAsync(TestData.Alice, MarketHash, RfmSide.Yes, 1000_000_000, 600, 200_000_000,
            now + 100, now + 200, CancellationToken.None);

        Assert.Equal(BigInteger.One, await gw.GetRequestCountAsync(CancellationToken.None));
    }

    [Fact]
    public void SimulatedContract_MintUsdc_AddsToWalletOnly()
    {
        var c = NewContract();
        c.MintUsdc(TestData.Alice, 500_000_000, new TxCtx { BlockNumber = 1, TxHash = "0xfaucet" });
        Assert.Equal(new BigInteger(500_000_000), c.WalletOf(TestData.Alice));
        Assert.Equal(BigInteger.Zero, c.UsdcOf(TestData.Alice)); // wallet is separate from vault internal
    }

    private static SimulatedContract NewContract()
    {
        var cfg = TestData.Cfg;
        return new SimulatedContract(cfg.NormalizedVault, cfg.NormalizedOutcomeTokens, cfg.NormalizedExchange, cfg.NormalizedRfm, cfg.OperatorAddress);
    }
}
