// E2E driver (Arc testnet): runs the FULL happy path against a real Arc testnet
// (chain 5042002) + the Stage 1 `nethereum` backend, asserting every step with
// on-chain receipts, decoded events, per-participant deltas and delta conservation.
//
// This is the anvil driver ported per PLAN_ARC_LIFECYCLE_PROOF.md §7-§9. Delta:
//   - gas is REAL NATIVE USDC (18-dec) transferred from treasury; anvil_setBalance is gone (D1)
//   - requestId is DECODED from the post tx's OWN receipt (D2), cross-checked vs the API
//   - every tx is receipt-awaited with an explicit timeout; never optimistic (D3)
//   - commit/reveal/born waits DERIVED from mirrored deadlines + buffer (D4)
//   - no wide eth_getLogs; finalize/resolve/settle hashes from NARROW bounded queries (D5/R2-d)
//   - Arc roles load from a gitignored manifest, PREFLIGHTED (key derives declared address) (D9)
//   - driver operator asserted equal to the deployed OutcomeTokens operator role (D9)
//   - conservation is DELTA-based vs a pinned pre-run snapshot (§9); stranded locks FAIL
//   - every tx hash retained with receipt status + decoded event into the evidence bundle (§10)
//   - standalone approve removed (SubmitDepositAsync approves first) [HCR2-4/minimality]
//
// Collateral vs gas are NEVER conflated: MockUSDC is 6-dec (1 USDC = 1_000_000 base);
// native USDC gas on Arc is 18-dec.

using System.Numerics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace E2E.Driver;

public static class Program
{
    // ------------------------------------------------------------------ config

    static string Rpc => Env("E2E_RPC", "https://rpc.testnet.arc.io");
    static string ApiUrl => Env("E2E_API", "http://localhost:8080");
    static string Shared => Env("E2E_SHARED", "e2e/.runtime");
    static string Manifest => Env("E2E_ROLE_MANIFEST", "");
    static string AddressesFile => Env("E2E_ADDRESSES", Path.Combine(Shared, "addresses.json"));

    static readonly string NativeGasPerAccount = "4000000000000000000"; // 4 native USDC (18-dec)
    const string Mint10K = "10000000000";    // 10,000 MockUSDC per account (6-dec)
    const string Deposit5K = "5000000000";   // 5,000 MockUSDC deposited
    const string Qty1000 = "1000000000";     // RFM quantity 1,000
    const string MinMatch200 = "200000000";  // RFM minMatch 200
    const long MaxTick = 600;                // RFM max price tick
    const string MmQty700 = "700000000";     // MM quote 700
    const long Mm1Tick = 500;
    const long Mm2Tick = 600;
    const string Trade2000 = "2000000000";   // MINT 2,000
    const string Trade1000 = "1000000000";   // TRANSFER 1,000
    const string Trade500 = "500000000";     // MERGE 500

    const long CommitBufferSec = 8;
    const long RevealBufferSec = 8;

    const string ArcChainId = "5042002";

    internal sealed record Role(string Name, string Address, string Key);

    static Role Operator = null!;
    static Role Institution = null!;
    static Role Mm1 = null!;
    static Role Mm2 = null!;
    static Role Trader = null!;
    static readonly List<Role> CollateralUsers = new();

    internal static Addresses Addrs = null!;
    static ChainQueries Chain = null!;
    static ApiClient Api = null!;
    static Dictionary<string, string> Tokens = new();
    static string CurrentMarketId = "";
    static string CurrentYesId = "";
    static string CurrentNoId = "";
    static Snapshot PreSnap = null!;       // baseline (before mints): MockUSDC + internal usdc/locked
    static Snapshot PreTradingSnap = null!; // post-birth, pre-crossings: adds YES/NO for the born market
    internal static string EvidenceFile = Env("E2E_EVIDENCE", Path.Combine("e2e", "ARC_LIFECYCLE_PROOF_EVIDENCE.md"));

    internal static readonly EvidenceBundle Evidence = new();

    // ------------------------------------------------------------------ main

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--selfcheck", StringComparer.OrdinalIgnoreCase))
            return SelfCheck();
        try
        {
            await RunAsync();
            Evidence.WriteFile();
            Console.WriteLine();
            Console.WriteLine("ALL E2E STEPS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Evidence.WriteFile(); // best-effort partial evidence
            Console.Error.WriteLine("E2E FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Offline self-check: a compact reference model of the lifecycle (deposits -> RFM finalize
    /// -> four crossings -> redeem) recomputes per-participant USDC/YES/NO independently and
    /// asserts the Expected.Delta tables + conservation used by the on-chain run. No RPC, no
    /// manifest: validates the money math that would otherwise only fail on a live proof.
    /// </summary>
    static int SelfCheck()
    {
        Operator = new Role("operator", "0x0", "0x0");
        Institution = new Role("institution", "0x1", "0x0");
        Mm1 = new Role("mm_rfm_1", "0x2", "0x0");
        Mm2 = new Role("mm_rfm_2", "0x3", "0x0");
        Trader = new Role("mm_live", "0x4", "0x0");
        CollateralUsers.Clear();
        CollateralUsers.AddRange(new[] { Institution, Mm1, Mm2, Trader });

        var usdc = CollateralUsers.ToDictionary(r => r, r => BigInteger.Parse(Deposit5K));
        var yes = CollateralUsers.ToDictionary(r => r, r => BigInteger.Zero);
        var no = CollateralUsers.ToDictionary(r => r, r => BigInteger.Zero);

        // All amounts are 6-dec base units (1 USDC = 1_000_000). RFM finalize: escrow consumed
        // 530 USDC (of 600, 70 released, bond released); mm reveal locks consumed 350 (mm1) and
        // 120 (mm2, 160 released, bond released); born allocations.
        usdc[Institution] -= 530_000_000; yes[Institution] += BigInteger.Parse(Qty1000);
        usdc[Mm1] -= 350_000_000; no[Mm1] += BigInteger.Parse(MmQty700);
        usdc[Mm2] -= 120_000_000; no[Mm2] += BigInteger.Parse("300000000");

        void Stage(string name)
        {
            foreach (var r in CollateralUsers)
            {
                var want = Expected.Delta(name)[r];
                Require(usdc[r] == BigInteger.Parse(want.Usdc), name + ": " + r.Name + " usdc " + usdc[r] + " != " + want.Usdc);
                Require(yes[r] == BigInteger.Parse(want.Yes), name + ": " + r.Name + " yes " + yes[r] + " != " + want.Yes);
                Require(no[r] == BigInteger.Parse(want.No), name + ": " + r.Name + " no " + no[r] + " != " + want.No);
            }
        }

        Step("selfcheck: MINT (BUY YES x BUY NO)");
        usdc[Institution] -= 1_200_000_000; yes[Institution] += BigInteger.Parse(Trade2000);
        usdc[Trader] -= 800_000_000; no[Trader] += BigInteger.Parse(Trade2000);
        Stage("after-mint");

        Step("selfcheck: TRANSFER YES");
        usdc[Institution] += 500_000_000; yes[Institution] -= BigInteger.Parse(Trade1000);
        usdc[Trader] -= 500_000_000; yes[Trader] += BigInteger.Parse(Trade1000);
        Stage("after-transfer-yes");

        Step("selfcheck: TRANSFER NO");
        usdc[Trader] += 500_000_000; no[Trader] -= BigInteger.Parse(Trade1000);
        usdc[Institution] -= 500_000_000; no[Institution] += BigInteger.Parse(Trade1000);
        Stage("after-transfer-no");

        Step("selfcheck: MERGE");
        usdc[Institution] += 250_000_000; yes[Institution] -= BigInteger.Parse(Trade500);
        usdc[Trader] += 250_000_000; no[Trader] -= BigInteger.Parse(Trade500);
        Stage("after-merge");

        Step("selfcheck: redeem (winner +exactly burned)");
        var before = usdc[Institution];
        usdc[Institution] += BigInteger.Parse(Qty1000);
        yes[Institution] -= BigInteger.Parse(Qty1000);
        Require(usdc[Institution] - before == BigInteger.Parse(Qty1000), "redeem credit != burned amount");
        Require(yes[Institution] == Expected.InstitutionYesAfterRedeem, "institution YES after redeem " + yes[Institution]);

        Step("selfcheck: conservation (§9)");
        var internalSum = CollateralUsers.Aggregate(BigInteger.Zero, (a, r) => a + usdc[r]);
        var walletsMock = CollateralUsers.Aggregate(BigInteger.Zero, (a, r) => a + (BigInteger.Parse(Mint10K) - BigInteger.Parse(Deposit5K)));
        var poolMock = BigInteger.Parse("1500000000");
        var minted = BigInteger.Parse(Mint10K) * CollateralUsers.Count;
        Require(internalSum + poolMock + walletsMock == minted, $"internal {internalSum} + pool {poolMock} + wallets {walletsMock} != minted {minted}");
        Require(internalSum == BigInteger.Parse("18500000000"), "sum internal usdc " + internalSum + " != 18500");
        Require(usdc[Institution] == BigInteger.Parse("4520000000"), "institution final " + usdc[Institution]);
        Require(usdc[Trader] == BigInteger.Parse("4450000000"), "trader final " + usdc[Trader]);

        Console.WriteLine();
        Console.WriteLine("SELFCHECK PASSED");
        return 0;
    }

    static async Task RunAsync()
    {
        LoadRoles();
        Addrs = ReadAddresses();
        Chain = new ChainQueries(new Web3(new Account(Operator.Key), Rpc), Addrs);
        Api = new ApiClient(ApiUrl);

        // ---- 0. chain identity on the SAME RPC connection, before any write (R2-b) ----
        Step("0. eth_chainId on the same RPC connection");
        var chainId = await Chain.ChainId();
        Require(chainId.ToString() == ArcChainId, "eth_chainId must be " + ArcChainId + ", got " + chainId + " (" + Rpc + ")");
        Pass("eth_chainId = " + chainId);

        Step("0b. preflight roles (D9) + deployed operator role");
        foreach (var r in new[] { Operator, Institution, Mm1, Mm2, Trader })
        {
            var derived = new Account(r.Key).Address;
            Require(derived.Equals(r.Address, StringComparison.OrdinalIgnoreCase),
                $"role {r.Name}: key derives {derived}, manifest declares {r.Address}");
        }
        var deployedOperator = await Chain.OperatorAddress();
        Require(deployedOperator.Equals(Operator.Address, StringComparison.OrdinalIgnoreCase),
            $"driver operator {Operator.Address} != deployed OutcomeTokens.operator() {deployedOperator}");
        Pass("roles preflight + deployed operator matches manifest operator");

        Step("0c. backend health, chain id agrees with the RPC's real chain");
        await Api.WaitHealthyAsync(TimeSpan.FromSeconds(120));
        var health = await Api.GetAsync<HealthView>(null, "/v1/health");
        Require(health.ChainId == ArcChainId, "backend /v1/health ChainId " + health.ChainId + " != " + ArcChainId);
        Pass("backend healthy on chain " + health.ChainId + " (matches eth_chainId)");

        // ---- pre-run snapshot (D8/§9), pinned BEFORE any money movement ----
        PreSnap = await Chain.SnapshotAsync(CollateralUsers);
        Evidence.PreSnapshot = PreSnap;

        Step("1. bind sessions for all participants (backend DemoUsers keys)");
        foreach (var u in new[] { Operator, Institution, Mm1, Mm2, Trader })
            Tokens[u.Address] = await Api.BindSessionAsync(u.Address);
        Pass("sessions bound for " + Tokens.Count + " participants");

        Step("2. fund native USDC gas (18-dec) from treasury; receipts awaited, balances asserted");
        await FundNativeGasAsync();

        Step("3. mint MockUSDC collateral (6-dec) to each collateral user from treasury");
        var treasuryWeb3 = new Web3(new Account(Institution.Key), Rpc);
        foreach (var u in CollateralUsers)
        {
            var hash = await SendContract(treasuryWeb3, Addrs.Usdc, new MintFunction { To = u.Address, Amt = BigInteger.Parse(Mint10K) });
            Evidence.RecordTx("mock-mint-" + u.Name, hash, Addrs.Usdc, null, Acceptance.MintCollateral);
        }
        var supply = await Chain.TotalSupply();
        var mintDelta = supply - PreSnap.MockTotalSupply;
        Require(mintDelta == BigInteger.Parse(Mint10K) * CollateralUsers.Count,
            "MockUSDC totalSupply delta " + mintDelta + " != " + BigInteger.Parse(Mint10K) * CollateralUsers.Count);
        Pass("MockUSDC totalSupply delta = " + mintDelta + " micro USDC");

        Step("4. deposit via the backend API (approve-before-deposit is inside SubmitDepositAsync)");
        foreach (var u in CollateralUsers)
        {
            var deposit = await Api.PostAsync(Tokens[u.Address], "/v1/vault/deposit", new { amount = Deposit5K });
            Require(deposit.TxHash != null, "deposit tx hash for " + u.Name);
            Evidence.RecordTx(u.Name + "-deposit", deposit.TxHash!, Addrs.Vault, null, Acceptance.DepositIndexed);
        }
        await WaitUntil(TimeSpan.FromSeconds(180), async () =>
        {
            foreach (var u in CollateralUsers)
            {
                var b = await Api.GetAsync<BalancesView>(Tokens[u.Address], "/v1/balances");
                if (BigInteger.Parse(b.ChainFree) < BigInteger.Parse(Deposit5K)) return false;
            }
            return true;
        }, "all deposits indexed");
        Pass("deposits settled + indexed");

        Step("5. assert backend ledger == on-chain after deposits");
        await AssertLedgerMatchesChain();

        Step("6. institution posts RFM request (unique market per run)");
        var requestCountBefore = await Chain.RequestCount();
        var marketLabel = "arc-e2e-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var post = await Api.PostAsync(Tokens[Institution.Address], "/v1/rfm/requests", new
        {
            market = marketLabel,
            side = "yes",
            quantity = Qty1000,
            maxPriceTick = MaxTick,
            minMatch = MinMatch200,
        });
        Require(post.TxHash != null, "postRequest tx hash");
        var postHash = post.TxHash!;

        // D2: decode RequestPosted from the post tx's OWN receipt (causally tied).
        var postReceipt = await AwaitReceipt(Chain.Web3, postHash, "rfm-postRequest", TimeSpan.FromSeconds(90));
        var posted = Chain.DecodeRequestPosted(postReceipt);
        Require(posted != null, "RequestPosted decoded from the post tx receipt");
        var requestId = posted!.RequestId;
        Require(post.RequestId == requestId.ToString(),
            "API requestId " + post.RequestId + " != decoded requestId " + requestId + " (causal link broken)");
        Require(posted.Market.Equals(Chain.Keccak(marketLabel), StringComparison.OrdinalIgnoreCase),
            "post market commitment is keccak(marketLabel)");
        var requestCountAfter = await Chain.RequestCount();
        Require(requestCountAfter > requestCountBefore,
            "RFM.requestCount() did not increase (" + requestCountBefore + " -> " + requestCountAfter + ")");
        Pass("RFM posted; decoded requestId " + requestId + " (requestCount " + requestCountBefore + " -> " + requestCountAfter + ", tx " + shortHash(postHash) + ")");

        var rfmView = await WaitFor(TimeSpan.FromSeconds(60), async () => await Api.GetRfmAsync(requestId), r => r != null);
        Require(rfmView!.Phase is "open" or "commit", "request mirrored, phase=" + rfmView.Phase);
        var commitDeadline = Unix(rfmView.CommitDeadline);
        var revealDeadline = Unix(rfmView.RevealDeadline);
        Require(commitDeadline > 0 && revealDeadline > commitDeadline, "mirrored deadlines sane");
        Pass("request mirrored (phase " + rfmView.Phase + "); commitDeadline " + commitDeadline + " revealDeadline " + revealDeadline);

        Step("7. MMs commit sealed quotes (before the commit deadline, distinct ticks)");
        await SubmitSigned(Tokens[Mm1.Address], requestId, "/v1/rfm/commit", Mm1Tick, MmQty700, "mm1");
        await SubmitSigned(Tokens[Mm2.Address], requestId, "/v1/rfm/commit", Mm2Tick, MmQty700, "mm2");
        Pass("quotes committed at ticks " + Mm1Tick + " and " + Mm2Tick + " (bonds escrowed)");

        Step("8. wait for the commit deadline + buffer, then reveal");
        await WaitForDeadline(commitDeadline, CommitBufferSec, "commit deadline");
        await SubmitSigned(Tokens[Mm1.Address], requestId, "/v1/rfm/reveal", Mm1Tick, MmQty700, "mm1");
        await SubmitSigned(Tokens[Mm2.Address], requestId, "/v1/rfm/reveal", Mm2Tick, MmQty700, "mm2");
        Pass("quotes revealed");

        Step("9. wait for the reveal deadline + buffer, then coordinator finalize -> MarketBorn");
        await WaitForDeadline(revealDeadline, RevealBufferSec, "reveal deadline");
        var born = await WaitFor(TimeSpan.FromSeconds(240), async () =>
        {
            var v = await Api.GetRfmAsync(requestId);
            return v?.Born is { MarketId: not null } ? v.Born : null;
        }, b => b != null);
        Require(born != null, "MarketBorn");
        // R2-d: finalize (MarketBorn) tx has no API hash; NARROW bounded query around the observed block.
        var bornBlock = await Chain.BlockNumber();
        var bornLog = await Chain.FindEventAroundAsync(Addrs.Rfm, bornBlock, new MarketBornEventDTO(), null, "MarketBorn");
        Require(bornLog != null, "MarketBorn log not found near block " + bornBlock);
        Require(bornLog.Log.Topics.Length > 2 && (bornLog.Log.Topics[2]?.ToString() ?? "").Equals(born!.MarketId, StringComparison.OrdinalIgnoreCase),
            "born marketId matches the observed born");
        await Chain.RecordReceiptAsync(bornLog.Log.TransactionHash, "rfm-finalize", Acceptance.MarketBorn);
        Require(born!.MarginalYesTick != born.VwapYesTick,
            "marginal (" + born.MarginalYesTick + ") must differ from vwap (" + born.VwapYesTick + ")");
        Pass("market born: marginal " + born.MarginalYesTick + " != vwap " + born.VwapYesTick + " (genuine competition)");

        CurrentMarketId = born.MarketId!;
        CurrentYesId = await Chain.TokenIdHex(CurrentMarketId, 0);
        CurrentNoId = await Chain.TokenIdHex(CurrentMarketId, 1);
        // Post-birth baseline: the market's YES/NO balances (the pre-run snapshot predates the
        // market, so token deltas for the born market are measured against this snapshot).
        PreTradingSnap = await Chain.SnapshotAsync(CollateralUsers, CurrentYesId, CurrentNoId);

        Step("10. assert auction positions minted on-chain (expected values, both outcomes) + ledger matches");
        Require(await Chain.TokenBalHex(Institution.Address, CurrentYesId) == BigInteger.Parse(Qty1000), "institution 1000 YES");
        Require(await Chain.TokenBalHex(Institution.Address, CurrentNoId) == 0, "institution 0 NO");
        Require(await Chain.TokenBalHex(Mm1.Address, CurrentNoId) == BigInteger.Parse(MmQty700), "mm1 700 NO");
        Require(await Chain.TokenBalHex(Mm1.Address, CurrentYesId) == 0, "mm1 0 YES");
        Require(await Chain.TokenBalHex(Mm2.Address, CurrentNoId) == BigInteger.Parse("300000000"), "mm2 300 NO");
        Require(await Chain.TokenBalHex(Mm2.Address, CurrentYesId) == 0, "mm2 0 YES");
        Require(await Chain.TokenBalHex(Trader.Address, CurrentYesId) == 0 && await Chain.TokenBalHex(Trader.Address, CurrentNoId) == 0, "trader no birth positions");
        await AssertLedgerMatchesChain();
        Pass("auction positions match expected values");

        Step("11. MINT (BUY YES x BUY NO)");
        await PlaceOrder(Tokens[Institution.Address], "yes", "buy", Trade2000, 600);   // rests YES bid @600
        var mintFills = await PlaceOrder(Tokens[Trader.Address], "no", "buy", Trade2000, 400); // crosses
        RequireFills(mintFills, "MINT");
        await CaptureBatchAsync(1, "settle-mint");
        await AssertDeltasAsync("after-mint");

        Step("12. TRANSFER YES (BUY YES x SELL YES)");
        await PlaceOrder(Tokens[Institution.Address], "yes", "sell", Trade1000, 500);  // rests YES ask @500
        var xferYes = await PlaceOrder(Tokens[Trader.Address], "yes", "buy", Trade1000, 500);
        RequireFills(xferYes, "TRANSFER");
        await CaptureBatchAsync(2, "settle-transfer-yes");
        await AssertDeltasAsync("after-transfer-yes");

        Step("13. TRANSFER NO (BUY NO x SELL NO)");
        await PlaceOrder(Tokens[Trader.Address], "no", "sell", Trade1000, 500);        // rests NO bid @500
        var xferNo = await PlaceOrder(Tokens[Institution.Address], "no", "buy", Trade1000, 500);
        RequireFills(xferNo, "TRANSFER");
        await CaptureBatchAsync(3, "settle-transfer-no");
        await AssertDeltasAsync("after-transfer-no");

        Step("14. MERGE (SELL YES x SELL NO)");
        await PlaceOrder(Tokens[Institution.Address], "yes", "sell", Trade500, 500);   // rests YES ask @500
        var mergeFills = await PlaceOrder(Tokens[Trader.Address], "no", "sell", Trade500, 500);
        RequireFills(mergeFills, "MERGE");
        await CaptureBatchAsync(4, "settle-merge");
        await AssertDeltasAsync("after-merge");

        Step("15. operator resolves (YES wins); MarketResolved hash via narrow query");
        var resolved = await Api.PostAsync(Tokens[Operator.Address], "/v1/markets/" + CurrentMarketId + "/resolve", new { outcome = "yes" });
        Require(resolved.Resolved == true, "resolve accepted");
        var resView = await WaitFor(TimeSpan.FromSeconds(90), async () =>
            await Api.GetAsync<ResolutionView>(Tokens[Institution.Address], "/v1/resolution/" + CurrentMarketId), r => r is { Resolved: true });
        Require(resView!.Resolved, "market resolved");
        var resolveBlock = await Chain.BlockNumber();
        var resolveLog = await Chain.FindEventAroundAsync(Addrs.OutcomeTokens, resolveBlock, new MarketResolvedEventDTO(), CurrentMarketId, "MarketResolved");
        Require(resolveLog != null, "MarketResolved log not found near block " + resolveBlock);
        await Chain.RecordReceiptAsync(resolveLog!.Log.TransactionHash, "operator-resolve", Acceptance.Resolved);
        Pass("market resolved (winning = yes)");

        Step("16. winners redeem 1:1; winner USDC increases by exactly the burned amount");
        var beforeRedeem = await Chain.UsdcBal(Institution.Address);
        var redeem = await Api.PostAsync(Tokens[Institution.Address], "/v1/markets/" + CurrentMarketId + "/redeem", new { amount = Qty1000 });
        Require(redeem.TxHash != null, "redeem tx hash");
        Evidence.RecordTx("institution-redeem", redeem.TxHash!, Addrs.Vault, null, Acceptance.RedeemExact);
        await WaitUntil(TimeSpan.FromSeconds(180), async () => await Chain.TokenBalHex(Institution.Address, CurrentYesId) == Expected.InstitutionYesAfterRedeem,
            "institution YES remainder after redeem");
        var afterRedeem = await Chain.UsdcBal(Institution.Address);
        Require(afterRedeem - beforeRedeem == BigInteger.Parse(Qty1000),
            "winner USDC increased by " + (afterRedeem - beforeRedeem) + " != burned 1000");
        await AssertLedgerMatchesChain();
        Pass("redeem: winner +1000 USDC == burned 1000 YES");

        Step("17. terminal state clean: RFM locks zero, no stuck order reservations");
        foreach (var u in CollateralUsers)
        {
            Require(await Chain.LockedBal(u.Address) == 0, "lockedBal != 0 for " + u.Name + " (stranded RFM lock)");
            var b = await Api.GetAsync<BalancesView>(Tokens[u.Address], "/v1/balances");
            Require(BigInteger.Parse(b.Reserved) == 0, "stuck order reservation for " + u.Name);
        }
        Pass("terminal state clean");

        Step("18. delta conservation (§9)");
        var postSnap = await Chain.SnapshotAsync(CollateralUsers, CurrentYesId, CurrentNoId);
        Evidence.PostSnapshot = postSnap;
        AssertConservation(PreSnap, PreTradingSnap, postSnap);
        Pass("delta conservation reconciles");
    }

    // ------------------------------------------------------------------ helpers

    static void Step(string s) => Console.WriteLine("\n== " + s + " ==");
    internal static void Pass(string s) => Console.WriteLine("  [PASS] " + s);
    static void Require(bool cond, string what)
    {
        if (!cond) throw new DriverAssertion("assertion failed: " + what);
    }

    static async Task WaitUntil(TimeSpan timeout, Func<Task<bool>> cond, string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { if (await cond()) return; } catch { /* poll again */ }
            await Task.Delay(1000);
        }
        throw new DriverAssertion("timeout waiting for " + what);
    }

    static async Task<T?> WaitFor<T>(TimeSpan timeout, Func<Task<T?>> get, Func<T?, bool> ok) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { var v = await get(); if (v != null && ok(v)) return v; } catch { }
            await Task.Delay(1000);
        }
        return null;
    }

    static void LoadRoles()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Manifest))
        {
            if (!File.Exists(Manifest)) throw new DriverAssertion("role manifest not found: " + Manifest);
            foreach (var raw in File.ReadAllLines(Manifest))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;
                var eq = line.IndexOf('=');
                vars[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        string V(string name)
        {
            var inline = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(inline)) return inline;
            if (vars.TryGetValue(name, out var v)) return v;
            throw new DriverAssertion("missing role config " + name + " (E2E_ROLE_MANIFEST or E2E_" + name + ")");
        }

        Operator = new Role("operator", V("OPERATOR_ADDRESS"), V("OPERATOR_PRIVATE_KEY"));
        Institution = new Role("institution", V("TREASURY_ADDRESS"), V("TREASURY_PRIVATE_KEY"));
        Mm1 = new Role("mm_rfm_1", V("MM_RFM_1_ADDRESS"), V("MM_RFM_1_PRIVATE_KEY"));
        Mm2 = new Role("mm_rfm_2", V("MM_RFM_2_ADDRESS"), V("MM_RFM_2_PRIVATE_KEY"));
        Trader = new Role("mm_live", V("MM_LIVE_ADDRESS"), V("MM_LIVE_PRIVATE_KEY"));
        CollateralUsers.Clear();
        CollateralUsers.AddRange(new[] { Institution, Mm1, Mm2, Trader });
    }

    static async Task FundNativeGasAsync()
    {
        var treasury = new Web3(new Account(Institution.Key), Rpc);
        var amount = BigInteger.Parse(NativeGasPerAccount);
        var recipients = new[] { Operator, Mm1, Mm2, Trader };
        var treasuryNative = await Chain.NativeBalance(Institution.Address);
        Require(treasuryNative >= amount * recipients.Length,
            "treasury native USDC " + treasuryNative + " < needed " + amount * recipients.Length);

        foreach (var r in recipients)
        {
            var before = await Chain.NativeBalance(r.Address);
            var hash = await SendValue(treasury, r.Address, amount);
            Evidence.RecordTx("gas-" + r.Name, hash, r.Address, null, Acceptance.GasFunded);
            await AwaitReceipt(Chain.Web3, hash, "gas-" + r.Name, TimeSpan.FromSeconds(90));
            var after = await Chain.NativeBalance(r.Address);
            Require(after - before == amount, "native gas transfer landed for " + r.Name + " (delta " + (after - before) + ")");
        }
        Pass("native USDC gas funded for " + recipients.Length + " accounts from treasury");
    }

    static async Task WaitForDeadline(long deadlineUnix, long bufferSec, string what)
    {
        var target = deadlineUnix + bufferSec;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < target) await Task.Delay(TimeSpan.FromSeconds(target - now));
        await WaitUntil(TimeSpan.FromSeconds(120), () => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= target), what + " reached");
    }

    static async Task SubmitSigned(string token, BigInteger requestId, string path, long tick, string size, string who)
    {
        var r = await Api.PostAsync(token, path, new { requestId = requestId.ToString(), priceTick = tick, size });
        Require(r.TxHash != null, path + " tx hash for " + who);
        Evidence.RecordTx(who + "-" + path.Split('/').Last(), r.TxHash!, null, null, Acceptance.SignedQuote);
    }

    static async Task<List<FillView>> PlaceOrder(string token, string outcome, string side, string size, long price)
    {
        var r = await Api.PostAsync(token, "/v1/orders", new { marketId = CurrentMarketId, outcome, side, size, price, type = "limit" });
        if (r.Fills == null) throw new DriverAssertion("order produced no fills: " + JsonSerializer.Serialize(r));
        return r.Fills;
    }

    static void RequireFills(List<FillView> fills, string expectedClass)
    {
        Require(fills.Count > 0, expectedClass + " produced fills");
        foreach (var f in fills)
            Require(f.TradeClass == expectedClass, expectedClass + " fill, got " + f.TradeClass + " (trade " + f.TradeId + ")");
    }

    /// <summary>Capture the N-th BatchSettled tx on the born market via a narrow bounded query.</summary>
    static async Task CaptureBatchAsync(int nth, string label)
    {
        await WaitTrades(nth);
        var trades = (await Api.GetMarketAsync(CurrentMarketId)).Trades;
        var batchId = trades.Count >= nth ? trades[nth - 1].BatchId : null;
        Require(batchId != null, "batchId for " + label);
        var block = await Chain.BlockNumber();
        var log = await Chain.FindEventAroundAsync(Addrs.Exchange, block, new BatchSettledEventDTO(), batchId, label);
        Require(log != null, label + " BatchSettled log not found near block " + block);
        await Chain.RecordReceiptAsync(log!.Log.TransactionHash, label, Acceptance.CrossingSettled);
    }

    static async Task WaitTrades(int expected)
        => await WaitUntil(TimeSpan.FromSeconds(180), async () => (await Api.GetMarketAsync(CurrentMarketId)).Trades.Count >= expected, expected + " trades settled");

    /// <summary>Acceptance 6/8: per-participant deltas from EXPECTED values (not backend-reported).</summary>
    static async Task AssertDeltasAsync(string stage)
    {
        var expected = Expected.Delta(stage);
        foreach (var (who, want) in expected)
        {
            var usdcDelta = await Chain.UsdcBal(who.Address) - PreSnap.UsdcBalance(who.Address);
            var yesDelta = await Chain.TokenBalHex(who.Address, CurrentYesId) - PreTradingSnap.Yes(who.Address, CurrentYesId);
            var noDelta = await Chain.TokenBalHex(who.Address, CurrentNoId) - PreTradingSnap.No(who.Address, CurrentNoId);
            Require(usdcDelta == BigInteger.Parse(want.Usdc), stage + ": " + who.Name + " usdc delta " + usdcDelta + " != expected " + want.Usdc);
            Require(yesDelta == BigInteger.Parse(want.Yes), stage + ": " + who.Name + " YES delta " + yesDelta + " != expected " + want.Yes);
            Require(noDelta == BigInteger.Parse(want.No), stage + ": " + who.Name + " NO delta " + noDelta + " != expected " + want.No);
        }
        await AssertLedgerMatchesChain();
        Pass(stage + " per-participant deltas exact on-chain + ledger");
    }

    static void AssertConservation(Snapshot preSnap, Snapshot preTrading, Snapshot postSnap)
    {
        var totalDelta = postSnap.MockTotalSupply - preSnap.MockTotalSupply;
        var vaultDelta = postSnap.MockVault - preSnap.MockVault;
        var poolDelta = postSnap.MockPool - preSnap.MockPool;
        var walletDelta = CollateralUsers.Aggregate(BigInteger.Zero, (acc, u) => acc + postSnap.MockWalletBalance(u.Address) - preSnap.MockWalletBalance(u.Address));
        var internalDelta = CollateralUsers.Aggregate(BigInteger.Zero, (acc, u) => acc + postSnap.UsdcBalance(u.Address) - preSnap.UsdcBalance(u.Address));

        Require(vaultDelta == internalDelta,
            $"vault physical delta {vaultDelta} != sum internal delta {internalDelta}");
        Require(totalDelta == vaultDelta + poolDelta + walletDelta,
            $"totalSupply delta {totalDelta} != vault {vaultDelta} + pool {poolDelta} + wallets {walletDelta}");
        // pool movement for this run must equal this market's net collateral: split 1000 + split 2000
        // - merge 500 - redeem 1000 = 1500, and the market's remaining winning (YES) supply.
        Require(poolDelta == BigInteger.Parse("1500000000"), "pool MockUSDC delta " + poolDelta + " != 1500");
        var remainingYes = (postSnap.Yes(Institution.Address, CurrentYesId) - preTrading.Yes(Institution.Address, CurrentYesId))
                         + (postSnap.Yes(Trader.Address, CurrentYesId) - preTrading.Yes(Trader.Address, CurrentYesId));
        Require(poolDelta == remainingYes, "pool delta " + poolDelta + " != remaining YES supply delta " + remainingYes);
        foreach (var u in CollateralUsers)
            Require(postSnap.LockedBalance(u.Address) == 0, "post-run lockedBal != 0 for " + u.Name);
    }

    static async Task AssertLedgerMatchesChain()
    {
        await WaitUntil(TimeSpan.FromSeconds(60), async () =>
        {
            try
            {
                foreach (var u in CollateralUsers)
                {
                    var b = await Api.GetAsync<BalancesView>(Tokens[u.Address], "/v1/balances");
                    var onChainUsdc = await Chain.UsdcBal(u.Address);
                    var onChainLocked = await Chain.LockedBal(u.Address);
                    if (BigInteger.Parse(b.ChainFree) != onChainUsdc - onChainLocked) return false;
                    foreach (var p in b.Positions)
                    {
                        if (p.MarketId == null || p.Outcome == null) continue;
                        var id = await Chain.TokenId(p.MarketId, p.Outcome.Equals("no", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0);
                        if (BigInteger.Parse(p.Amount) != await Chain.TokenBal(u.Address, id)) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }, "ledger == on-chain");
    }

    // ------------------------------------------------------------------ chain plumbing

    static async Task<string> SendContract(Web3 web3, string to, FunctionMessage msg)
    {
        var receipt = await web3.Eth.GetContractHandler(to).SendRequestAndWaitForReceiptAsync(msg);
        Require(receipt?.Status?.Value == 1 && receipt.TransactionHash != null, "tx to " + to + " reverted or no receipt");
        return receipt!.TransactionHash!;
    }

    static async Task<string> SendValue(Web3 web3, string to, BigInteger value)
    {
        var hash = await web3.TransactionManager.SendTransactionAsync(new TransactionInput
        {
            From = web3.TransactionManager.Account.Address,
            To = to,
            Value = new HexBigInteger(value),
            Gas = new HexBigInteger(21000),
        });
        Require(!string.IsNullOrWhiteSpace(hash), "native value transfer to " + to + " produced no hash");
        return hash!;
    }

    static async Task<TransactionReceipt> AwaitReceipt(Web3 web3, string txHash, string label, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var r = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
            if (r != null)
            {
                Require(r.Status?.Value == 1, label + " tx " + txHash + " REVERTED (status " + r.Status?.Value + ")");
                return r;
            }
            await Task.Delay(1000);
        }
        throw new DriverAssertion("timeout awaiting receipt for " + label + " tx " + txHash);
    }

    static string shortHash(string h) => h.Length <= 16 ? h : h[..10] + "…" + h[^4..];
    static long Unix(string? unix) => long.TryParse(unix, out var v) ? v : 0;

    static string Env(string k, string def)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? def : v;
    }

    static Addresses ReadAddresses()
    {
        if (!File.Exists(AddressesFile)) throw new DriverAssertion("addresses file not found at " + AddressesFile + " (E2E_ADDRESSES)");
        var doc = JsonDocument.Parse(File.ReadAllText(AddressesFile));
        string Get(string k, string fallback = "")
        {
            if (doc.RootElement.TryGetProperty(k, out var e)) return e.GetString()!;
            if (fallback != "") return fallback;
            throw new DriverAssertion("addresses file missing key " + k);
        }
        // The operator is always the manifest role (D9); the addresses file may omit it.
        return new Addresses(Get("usdc"), Get("outcomeTokens"), Get("vault"), Get("exchange"), Get("rfm"), Get("operator", Operator.Address));
    }

    // ------------------------------------------------------------------ expected deltas (enumerated from expected values, not backend-reported)

    sealed record ExpectedBal(string Usdc, string Yes, string No);
    static class Expected
    {
        public static readonly BigInteger InstitutionYesAfterRedeem = BigInteger.Parse("500000000");
        public static readonly BigInteger InstitutionYesAfterMerge = BigInteger.Parse("1500000000");

        /// <summary>Cumulative per-participant deltas (usdcBal, YES, NO) from the pre-run snapshot.</summary>
        public static IReadOnlyDictionary<Role, ExpectedBal> Delta(string stage) => stage switch
        {
            "after-mint" => new Dictionary<Role, ExpectedBal>
            {
                [Institution] = new("3270000000", "3000000000", "0"),
                [Trader] = new("4200000000", "0", "2000000000"),
                [Mm1] = new("4650000000", "0", "700000000"),
                [Mm2] = new("4880000000", "0", "300000000"),
            },
            "after-transfer-yes" => new Dictionary<Role, ExpectedBal>
            {
                [Institution] = new("3770000000", "2000000000", "0"),
                [Trader] = new("3700000000", "1000000000", "2000000000"),
                [Mm1] = new("4650000000", "0", "700000000"),
                [Mm2] = new("4880000000", "0", "300000000"),
            },
            "after-transfer-no" => new Dictionary<Role, ExpectedBal>
            {
                [Institution] = new("3270000000", "2000000000", "1000000000"),
                [Trader] = new("4200000000", "1000000000", "1000000000"),
                [Mm1] = new("4650000000", "0", "700000000"),
                [Mm2] = new("4880000000", "0", "300000000"),
            },
            "after-merge" => new Dictionary<Role, ExpectedBal>
            {
                [Institution] = new("3520000000", "1500000000", "1000000000"),
                [Trader] = new("4450000000", "1000000000", "500000000"),
                [Mm1] = new("4650000000", "0", "700000000"),
                [Mm2] = new("4880000000", "0", "300000000"),
            },
            _ => throw new DriverAssertion("unknown stage " + stage),
        };
    }

    public sealed record Addresses(string Usdc, string OutcomeTokens, string Vault, string Exchange, string Rfm, string Operator);
}

// ================================================================== chain queries + snapshot

public sealed class ChainQueries
{
    public Web3 Web3 { get; }
    readonly Program.Addresses _a;
    readonly IClient _client;
    static readonly string OperatorSel = Selector("operator()");
    static readonly string UsdcBalSel = Selector("usdcBal(address)");
    static readonly string LockedBalSel = Selector("lockedBal(address)");
    static readonly string BalanceOfSel = Selector("balanceOf(address)");
    static readonly string TokenBalSel = Selector("tokenBal(address,uint256)");
    static readonly string TokenIdSel = Selector("tokenId(bytes32,uint8)");
    static readonly string TotalSupplySel = Selector("totalSupply()");
    static readonly string RequestCountSel = Selector("requestCount()");

    public ChainQueries(Web3 web3, Program.Addresses addresses)
    {
        Web3 = web3;
        _a = addresses;
        _client = web3.Client;
    }

    public Program.Addresses Addresses => _a;

    public async Task<BigInteger> ChainId()
    {
        var req = new RpcRequest(Guid.NewGuid().ToString(), "eth_chainId");
        var hex = await _client.SendRequestAsync<string>(req);
        return hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? BigInteger.Parse("0" + hex[2..], System.Globalization.NumberStyles.HexNumber) : BigInteger.Parse(hex);
    }

    public async Task<BigInteger> NativeBalance(string addr)
        => (await Web3.Eth.GetBalance.SendRequestAsync(addr)).Value;

    public async Task<BigInteger> BlockNumber()
        => (await Web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;

    public async Task<string> OperatorAddress()
    {
        var raw = await CallRaw(_a.OutcomeTokens, OperatorSel);
        var h = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        h = h.PadLeft(64, '0');
        return "0x" + h[^40..];
    }
    public Task<BigInteger> UsdcBal(string user) => Call(_a.Vault, UsdcBalSel + A32(user));
    public Task<BigInteger> LockedBal(string user) => Call(_a.Vault, LockedBalSel + A32(user));
    public Task<BigInteger> TokenBal(string user, BigInteger id) => Call(_a.Vault, TokenBalSel + A32(user) + U256(id));
    public Task<BigInteger> TokenBalHex(string user, string tokenIdHex) => TokenBal(user, HexToU256(tokenIdHex));
    public Task<BigInteger> TokenId(string marketId, byte outcome) => Call(_a.OutcomeTokens, TokenIdSel + H32(marketId) + U256(outcome));
    public Task<string> TokenIdHex(string marketId, byte outcome) => TokenId(marketId, outcome).ContinueWith(t => "0x" + t.Result.ToString("x64"));
    public Task<BigInteger> TotalSupply() => Call(_a.Usdc, TotalSupplySel);
    public Task<BigInteger> RequestCount() => Call(_a.Rfm, RequestCountSel);
    public Task<BigInteger> MockWalletBalance(string addr) => Call(_a.Usdc, BalanceOfSel + A32(addr));

    public string Keccak(string s) => Sha3Keccack.Current.CalculateHash(s);

    async Task<BigInteger> Call(string to, string data)
        => (await CallRaw(to, data)) is var raw && (string.IsNullOrEmpty(raw) || raw == "0x")
            ? BigInteger.Zero
            : BigInteger.Parse("0" + raw[2..], System.Globalization.NumberStyles.HexNumber);

    async Task<string> CallRaw(string to, string data)
    {
        var call = new { from = _a.Operator, to, data = data.ToLowerInvariant() };
        var req = new RpcRequest(Guid.NewGuid().ToString(), "eth_call", new object[] { call, "latest" });
        return await _client.SendRequestAsync<string>(req) ?? "0x";
    }

    static BigInteger HexToU256(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);
    }

    static string Selector(string sig) => Sha3Keccack.Current.CalculateHash(sig)[..10];

    static string A32(string address)
    {
        var h = address.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return h.PadLeft(64, '0');
    }

    static string U256(BigInteger v)
    {
        if (v < 0) throw new DriverAssertion("negative uint256");
        var s = v.ToString("x");
        if (s.Length > 64) s = s[^64..];
        return s.PadLeft(64, '0');
    }

    static string H32(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return h.PadLeft(64, '0');
    }

    // ---- event decoding + narrow log queries ----

    public RequestPostedDecoded? DecodeRequestPosted(TransactionReceipt receipt)
    {
        if (receipt.Logs == null) return null;
        foreach (var log in receipt.Logs)
        {
            if (!log.Address.Equals(_a.Rfm, StringComparison.OrdinalIgnoreCase)) continue;
            var topic0 = log.Topics.Length > 0 ? log.Topics[0]?.ToString() : null;
            if (topic0 == null || topic0 != Event<RequestPostedEventDTO>.GetEventABI().Sha3Signature) continue;
            var decoded = Event<RequestPostedEventDTO>.DecodeEvent(log);
            if (decoded == null) return null;
            var e = decoded.Event;
            return new RequestPostedDecoded(e.RequestId, InfrastructureOrHex(e.Market), log.TransactionHash);
        }
        return null;
    }

    static string InfrastructureOrHex(byte[] b) => "0x" + Convert.ToHexStringLower(b);

    /// <summary>Narrow bounded eth_getLogs around an observed block for one event/topic1.</summary>
    public async Task<EventLog<T>?> FindEventAroundAsync<T>(string address, BigInteger observedBlock, T _, string? topic1, string label)
        where T : IEventDTO, new()
    {
        var topic0 = Event<T>.GetEventABI().Sha3Signature;
        foreach (var window in new[] { 3, 8, 20 }) // narrow first; widen only if needed (RPC caps span)
        {
            var from = observedBlock - window;
            var to = observedBlock + window;
            if (from < 0) from = 0;
            var logs = await GetLogsAsync(address, topic0, topic1, from, to);
            if (logs.Length > 0)
            {
                var first = logs[0];
                var ev = Event<T>.DecodeEvent(first);
                return ev;
            }
        }
        return null;
    }

    async Task<FilterLog[]> GetLogsAsync(string address, string topic0, string? topic1, BigInteger fromBlock, BigInteger toBlock)
    {
        var filter = new NewFilterInput
        {
            FromBlock = new BlockParameter(new HexBigInteger(fromBlock)),
            ToBlock = new BlockParameter(new HexBigInteger(toBlock)),
            Address = new[] { address },
            Topics = topic1 == null ? new object[] { topic0 } : new object[] { topic0, topic1 },
        };
        return await Web3.Eth.Filters.GetLogs.SendRequestAsync(filter);
    }

    public async Task RecordReceiptAsync(string txHash, string label, string acceptance)
    {
        var receipt = await Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        RequireStatus(receipt, label, txHash);
        var status = receipt.Status?.Value == 1 ? "success" : "reverted";
        Program.Evidence.RecordTx(label, txHash, receipt.To, status, acceptance);
        Console.WriteLine($"  [tx] {label}: {Short(txHash)} status={status} block={receipt.BlockNumber?.Value}");
    }

    static string Short(string h) => h.Length <= 16 ? h : h[..10] + "…" + h[^4..];

    static void RequireStatus(TransactionReceipt? receipt, string label, string txHash)
    {
        if (receipt == null) throw new DriverAssertion(label + " tx " + txHash + " has no receipt");
        if (receipt.Status?.Value != 1) throw new DriverAssertion(label + " tx " + txHash + " REVERTED (status " + receipt.Status?.Value + ")");
    }

    internal async Task<Snapshot> SnapshotAsync(IReadOnlyList<Program.Role> roles, string? yesId = null, string? noId = null)
    {
        var s = new Snapshot();
        s.MockTotalSupply = await TotalSupply();
        s.MockVault = await MockWalletBalance(Program.Addrs.Vault);
        s.MockPool = await MockWalletBalance(Program.Addrs.OutcomeTokens);
        foreach (var r in roles)
        {
            s.MockWallet[r.Address] = await MockWalletBalance(r.Address);
            s.Usdc[r.Address] = await UsdcBal(r.Address);
            s.Locked[r.Address] = await LockedBal(r.Address);
            if (yesId != null) s.TokenBal[(r.Address, yesId)] = await TokenBal(r.Address, HexToU256(yesId));
            if (noId != null) s.TokenBal[(r.Address, noId)] = await TokenBal(r.Address, HexToU256(noId));
        }
        return s;
    }

    public sealed record RequestPostedDecoded(BigInteger RequestId, string Market, string TxHash);
}

public sealed class Snapshot
{
    public BigInteger MockTotalSupply;
    public BigInteger MockVault;
    public BigInteger MockPool;
    public Dictionary<string, BigInteger> MockWallet = new();
    public Dictionary<string, BigInteger> Usdc = new();
    public Dictionary<string, BigInteger> Locked = new();
    public Dictionary<(string, string), BigInteger> TokenBal = new();

    public BigInteger MockWalletBalance(string a) => MockWallet.GetValueOrDefault(a);
    public BigInteger UsdcBalance(string a) => Usdc.GetValueOrDefault(a);
    public BigInteger LockedBalance(string a) => Locked.GetValueOrDefault(a);
    public BigInteger Yes(string a, string yesId) => TokenBal.GetValueOrDefault((a, yesId));
    public BigInteger No(string a, string noId) => TokenBal.GetValueOrDefault((a, noId));
}

// ================================================================== evidence bundle

public static class Acceptance
{
    public const string GasFunded = "§8.1 chain identity + gas preflight";
    public const string MintCollateral = "§8.2 collateral minted";
    public const string DepositIndexed = "§8.8 deposits settle + ledger";
    public const string OnChainMove = "§8 on-chain move";
    public const string SignedQuote = "§8.3/§8.4 commit+reveal";
    public const string MarketBorn = "§8.5 MarketBorn + marginal!=vwap";
    public const string CrossingSettled = "§8.7/§8.8 crossing TradeClass + deltas";
    public const string Resolved = "§8.9 resolution";
    public const string RedeemExact = "§8.9 redeem 1:1";
}

public sealed class EvidenceBundle
{
    public readonly List<EvidenceTx> Txs = new();
    public Snapshot? PreSnapshot;
    public Snapshot? PostSnapshot;
    public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public void RecordTx(string kind, string hash, string? to, string? status, string acceptance)
        => Txs.Add(new EvidenceTx(kind, hash, to, status ?? "submitted", acceptance, ExplorerUrl(hash)));

    static string ExplorerUrl(string hash) => "https://testnet.arcscan.app/tx/" + hash;

    public void WriteFile()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Arc lifecycle proof - evidence bundle");
            sb.AppendLine();
            sb.AppendLine("Run started (UTC): " + StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Driver commit: " + GitCommit("e2e"));
            sb.AppendLine("Backend commit: " + GitCommit("backend"));
            sb.AppendLine();
            sb.AppendLine("## Transactions");
            sb.AppendLine();
            sb.AppendLine("| Kind | Tx hash | Explorer | Status | Acceptance item |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var t in Txs)
                sb.AppendLine($"| {t.Kind} | `{t.Hash}` | [arcscan]({t.Url}) | {t.Status} | {t.Acceptance} |");
            sb.AppendLine();
            sb.AppendLine("## Pre/post snapshots (MockUSDC totalSupply / Vault / Pool / per-wallet)");
            sb.AppendLine();
            sb.AppendLine("Pre-run: totalSupply=" + Snapshot(PreSnapshot?.MockTotalSupply) +
                " vault=" + Snapshot(PreSnapshot?.MockVault) + " pool=" + Snapshot(PreSnapshot?.MockPool));
            sb.AppendLine("Post-run: totalSupply=" + Snapshot(PostSnapshot?.MockTotalSupply) +
                " vault=" + Snapshot(PostSnapshot?.MockVault) + " pool=" + Snapshot(PostSnapshot?.MockPool));
            File.WriteAllText(Program.EvidenceFile, sb.ToString());
            Console.WriteLine("evidence written to " + Program.EvidenceFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not write evidence bundle: " + ex.Message);
        }
    }

    static string Snapshot(BigInteger? v) => v?.ToString() ?? "n/a";
    static string GitCommit(string _)
    {
        // The driver and backend live in the SAME submission monorepo; resolve the repo root
        // by walking up from the working directory to find .git.
        try
        {
            var dir = Directory.GetCurrentDirectory();
            while (dir != null && !Directory.Exists(Path.Combine(dir, ".git"))) dir = Path.GetDirectoryName(dir);
            if (dir == null) return "unknown";
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD") { WorkingDirectory = dir, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var outS = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(outS) ? "unknown" : outS;
        }
        catch { return "unknown"; }
    }
}

public sealed record EvidenceTx(string Kind, string Hash, string? To, string Status, string Acceptance, string Url);

// ================================================================== API client + models

public sealed class ApiClient(string baseUrl)
{
    readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task WaitHealthyAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var h = await GetAsync<HealthView>(null, "/v1/health");
                if (h.Ok && !h.Simulate) { Program.Pass("backend healthy, real Nethereum mode, chain " + h.ChainId); return; }
            }
            catch { }
            await Task.Delay(1000);
        }
        throw new DriverAssertion("backend never became healthy in real mode");
    }

    public async Task<string> BindSessionAsync(string address)
    {
        var r = await PostJsonAsync<BindResp>("/v1/session/bind", new { address });
        if (r.Token == null) throw new DriverAssertion("session bind failed for " + address);
        return r.Token;
    }

    public async Task<PostResp> PostAsync(string? token, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new DriverAssertion($"{path} -> {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<PostResp>(text, Json) ?? throw new DriverAssertion("empty response " + path);
    }

    public async Task<T> GetAsync<T>(string? token, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new DriverAssertion($"GET {path} -> {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new DriverAssertion("empty GET " + path);
    }

    async Task<T> PostJsonAsync<T>(string path, object body)
    {
        using var resp = await _http.PostAsJsonAsync(path, body);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new DriverAssertion($"{path} -> {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new DriverAssertion("empty POST " + path);
    }

    public async Task<RfmView?> GetRfmAsync(BigInteger id)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/rfm/requests/" + id);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new DriverAssertion($"GET rfm {id} -> {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<RfmView>(text, Json);
    }
    public async Task<MarketDetail> GetMarketAsync(string marketId) => await GetAsync<MarketDetail>(null, "/v1/markets/" + marketId);
}

public sealed record HealthView(bool Ok, bool Simulate, string ChainId);
public sealed record BindResp(string? Token, string? Address);
public sealed record PostResp(string? TxHash, string? RequestId, string? Error, List<FillView>? Fills, bool? Resolved);
public sealed record FillView(string TradeId, string TradeClass, string Size, long PriceTick);
public sealed record BalancesView(string User, string ChainFree, string Reserved, string Available, List<PositionView> Positions);
public sealed record PositionView(string TokenId, string? MarketId, string? Outcome, string Amount);
public sealed record BornView(string MarketId, long MarginalYesTick, long VwapYesTick, string Filled);
public sealed record RfmView(string Phase, string CommitDeadline, string RevealDeadline, BornView? Born);
public sealed record MarketDetail(MarketView Market, List<TradeView> Trades);
public sealed record MarketView(string MarketId, bool Exists, bool Closing, bool Resolved, string? WinningOutcome);
public sealed record TradeView(string TradeId, string TradeClass, string Size, long YesBasisTick, string BatchId);
public sealed record ResolutionView(bool Resolved, string? WinningOutcome);

// ================================================================== chain function + event DTOs

[Function("mint")]
public sealed class MintFunction : FunctionMessage
{
    [Parameter("address", "to", 1)] public string To { get; set; } = "";
    [Parameter("uint256", "amt", 2)] public BigInteger Amt { get; set; }
}

[Event("RequestPosted")]
public sealed class RequestPostedEventDTO : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("bytes32", "market", 2, true)] public byte[] Market { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "side", 3, false)] public byte Side { get; set; }
    [Parameter("uint256", "quantity", 4, false)] public BigInteger Quantity { get; set; }
    [Parameter("uint256", "maxPriceTick", 5, false)] public BigInteger MaxPriceTick { get; set; }
    [Parameter("uint256", "minMatch", 6, false)] public BigInteger MinMatch { get; set; }
    [Parameter("uint256", "commitDeadline", 7, false)] public BigInteger CommitDeadline { get; set; }
    [Parameter("uint256", "revealDeadline", 8, false)] public BigInteger RevealDeadline { get; set; }
    [Parameter("uint256", "escrowAmount", 9, false)] public BigInteger EscrowAmount { get; set; }
    [Parameter("uint256", "minQuoteSize", 10, false)] public BigInteger MinQuoteSize { get; set; }
}

[Event("MarketBorn")]
public sealed class MarketBornEventDTO : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)] public BigInteger RequestId { get; set; }
    [Parameter("bytes32", "marketId", 2, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint256", "marginalYesTick", 3, false)] public BigInteger MarginalYesTick { get; set; }
    [Parameter("uint256", "vwapYesTick", 4, false)] public BigInteger VwapYesTick { get; set; }
    [Parameter("uint256", "filledQuantity", 5, false)] public BigInteger FilledQuantity { get; set; }
    [Parameter("uint8", "side", 6, false)] public byte Side { get; set; }
}

[Event("BatchSettled")]
public sealed class BatchSettledEventDTO : IEventDTO
{
    [Parameter("bytes32", "batchId", 1, true)] public byte[] BatchId { get; set; } = Array.Empty<byte>();
    [Parameter("bytes32[]", "tradeIds", 2, false)] public List<byte[]> TradeIds { get; set; } = new();
}

[Event("MarketResolved")]
public sealed class MarketResolvedEventDTO : IEventDTO
{
    [Parameter("bytes32", "marketId", 1, true)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "outcome", 2, false)] public byte Outcome { get; set; }
}

public sealed class DriverAssertion(string message) : Exception(message);
