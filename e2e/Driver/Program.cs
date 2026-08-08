// E2E driver (Arc testnet): runs the FULL happy path against a real Arc testnet
// (chain 5042002) + the Stage 1 `nethereum` backend, asserting every step with
// on-chain receipts, DECODED events (including settleBatch calldata), per-participant
// absolute deltas, backend position completeness, and block-pinned delta conservation.
//
// This is the anvil driver ported per PLAN_ARC_LIFECYCLE_PROOF.md §7-§9 (rev 2, HDev2).
// - gas is REAL NATIVE USDC (18-dec) transferred from treasury; anvil_setBalance is gone (D1)
// - requestId is DECODED from the post tx's OWN receipt (D2), cross-checked vs the API
// - every tx is receipt-awaited with an explicit timeout; never optimistic (D3)
// - commit/reveal/born waits DERIVED from mirrored deadlines + buffer (D4)
// - finalize/resolve/settle hashes from NARROW bounded eth_getLogs (D5/R2-d)
// - Arc roles load from a gitignored manifest, PREFLIGHTED; operator == deployed role (D9)
// - settlement classification is DECODED from the on-chain settleBatch calldata (§8.7)
// - backend positions are enumerated per expected participant/token (§8.6/§8.8)
// - snapshots are PINNED to a block and delta-conserved vs a pre-run snapshot (§9)
// - the 1m duration preset is used and its windows asserted (§11b)
// - every tx hash retained with receipt status + decoded event in the evidence bundle (§10)
// - standalone approve removed (SubmitDepositAsync approves first) [HCR2-4]
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

    internal static string Rpc => Env("E2E_RPC", "https://rpc.testnet.arc.io");
    static string ApiUrl => Env("E2E_API", "http://localhost:8080");
    static string Shared => Env("E2E_SHARED", "e2e/.runtime");
    static string Manifest => Env("E2E_ROLE_MANIFEST", "");
    static string AddressesFile => Env("E2E_ADDRESSES", Path.Combine(Shared, "addresses.json"));
    internal static string EvidenceFile = RepoRootEvidencePath();
    internal static string BackendCommitOverride => Env("E2E_BACKEND_COMMIT", "");

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

    internal const string ArcChainId = "5042002";
    const string DurationPreset = "1m";      // §11b: proof runs use the 1-minute auction
    const long PresetCommitSec = 40;         // 1m preset: 2/3 commit
    const long PresetRevealSec = 20;         // 1m preset: 1/3 reveal (floored at 20s)

    internal sealed record Role(string Name, string Address, string Key);

    internal static Role Operator = null!;
    internal static Role Institution = null!;
    internal static Role Mm1 = null!;
    internal static Role Mm2 = null!;
    internal static Role Trader = null!;
    static readonly List<Role> CollateralUsers = new();

    internal static Addresses Addrs = null!;
    static ChainQueries Chain = null!;
    static ApiClient Api = null!;
    static Dictionary<string, string> Tokens = new();
    static string CurrentMarketId = "";
    static string CurrentYesId = "";
    static string CurrentNoId = "";
    static BigInteger CurrentRequestId = 0;
    static string PostTxHash = "";

    static Snapshot PreSnap = null!;   // baseline (before mints): pinned block, MockUSDC + internal usdc/locked
    static readonly StringBuilder Transcript = new();
    internal static readonly EvidenceBundle Evidence = new();

    public sealed record RequestPostedDecoded(BigInteger RequestId, string Market, string TxHash);

    /// <summary>
    /// Offline decode check: exercises the EXACT live-path decoders with Nethereum-encoded shapes
    /// (the same topic-0x-prefix and settleBatch-tuple encodings the backend produces). No RPC.
    /// </summary>
    static int DecodeCheck()
    {
        Addrs = new Addresses(
            "0x0000000000000000000000000000000000000001",
            "0x0000000000000000000000000000000000000002",
            "0x0000000000000000000000000000000000000003",
            "0x0000000000000000000000000000000000000004",
            "0x0000000000000000000000000000000000000005",
            "0x0000000000000000000000000000000000000006");
        Chain = new ChainQueries(new Web3(new Account("0x" + new string('1', 64)), "http://localhost:8545"), Addrs);

        Console.WriteLine("== decodecheck: settleBatch calldata decode ==");
        var tradeId = "0x" + "aa".PadRight(64, '0');
        var marketId = "0x" + "bb".PadRight(64, '0');
        var calldata = "0x" + BuildSettleBatch(tradeId, marketId, cls: 1, outcome: 0, partyA: "0x10000000000000000000000000000000000000a1", partyB: "0x10000000000000000000000000000000000000b2", tick: 600, size: "2000000000");
        var decoded = Chain.DecodeSettleBatch(calldata);
        Require(decoded.Count == 1, "decoded 1 trade");
        var d = decoded[0];
        Require(d.TradeId.Equals(tradeId, StringComparison.OrdinalIgnoreCase), "tradeId decoded");
        Require(d.MarketId.Equals(marketId, StringComparison.OrdinalIgnoreCase), "marketId decoded");
        Require(d.Class == 1, "class=MINT decoded");
        Require(d.PartyA.Equals("0x10000000000000000000000000000000000000a1", StringComparison.OrdinalIgnoreCase), "partyA decoded");
        Require(d.PartyB.Equals("0x10000000000000000000000000000000000000b2", StringComparison.OrdinalIgnoreCase), "partyB decoded");
        Require(d.Tick == 600, "tick decoded");
        Require(d.Size == BigInteger.Parse("2000000000"), "size decoded");
        Console.WriteLine("  [PASS] settleBatch tuple decode (MINT)");

        Console.WriteLine("== decodecheck: RequestPosted topic normalization (0x-prefixed vs bare Sha3Signature) ==");
        var marketHash = "0x" + "cd".PadRight(64, '0');
        var receipt = BuildRequestPostedReceipt(requestId: 7, marketHash);
        var posted = Chain.DecodeRequestPosted(receipt);
        Require(posted != null, "RequestPosted decoded despite 0x-prefixed topics");
        Require(posted!.RequestId == 7, "requestId decoded from the receipt topic");
        Require(posted.Market.Equals(marketHash, StringComparison.OrdinalIgnoreCase), "market decoded from the receipt topic");
        Require(posted.TxHash == "0xdead", "tx hash retained");
        Console.WriteLine("  [PASS] RequestPosted decoded from a 0x-prefixed receipt log");

        Console.WriteLine();
        Console.WriteLine("DECODECHECK PASSED");
        return 0;
    }

    /// <summary>Build a settleBatch(bytes32,tuple[]) calldata for one MINT trade (hand-ABI-encoded,
    /// matching the encoding the backend's Nethereum gateway produces).</summary>
    static string BuildSettleBatch(string tradeId, string marketId, byte cls, byte outcome, string partyA, string partyB, long tick, string size)
    {
        var sel = Sha3Keccack.Current.CalculateHash("settleBatch(bytes32,tuple[])")[2..10];
        var sb = new StringBuilder(sel);
        sb.Append(Word(tradeId)).Append("0000000000000000000000000000000000000000000000000000000000000040")
          .Append("0000000000000000000000000000000000000000000000000000000000000001");
        sb.Append(Word(tradeId)).Append(Word(marketId))
          .Append(Word((BigInteger)cls)).Append(Word((BigInteger)outcome))
          .Append(Word(partyA)).Append(Word(partyB))
          .Append(Word((BigInteger)tick)).Append(Word(BigInteger.Parse(size)));
        return sb.ToString();
    }

    static string Word(object value)
    {
        var raw = value switch
        {
            string s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => s[2..],
            string s => BigInteger.Parse(s).ToString("x"),
            BigInteger b => b.ToString("x"),
            int i => ((BigInteger)i).ToString("x"),
            long l => ((BigInteger)l).ToString("x"),
            byte by => ((BigInteger)by).ToString("x"),
            _ => throw new DriverAssertion("bad word value: " + value),
        };
        if (raw.Length > 64) raw = raw[^64..];
        return raw.PadLeft(64, '0');
    }

    static TransactionReceipt BuildRequestPostedReceipt(BigInteger requestId, string marketHash)
    {
        var topic0 = "0x" + Event<RequestPostedEventDTO>.GetEventABI().Sha3Signature;
        var log = new FilterLog
        {
            Address = Addrs.Rfm,
            TransactionHash = "0xdead",
            Topics = new object[] { topic0, "0x" + Word(requestId), "0x" + Word(marketHash) },
            Data = "0x" + Word(0) + Word(1000) + Word(600) + Word(200) + Word(40) + Word(60) + Word(600) + Word(1),
        };
        return new TransactionReceipt { Logs = new[] { log }, TransactionHash = "0xdead" };
    }

    // ------------------------------------------------------------------ main

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--decodecheck", StringComparer.OrdinalIgnoreCase))
            return DecodeCheck();
        // Tee stdout into the transcript so the evidence bundle carries the full run log.
        Console.SetOut(new TeeWriter(Console.Out, Transcript));
        try
        {
            await RunAsync();
            Evidence.Transcript = Transcript.ToString();
            Evidence.WriteFile(requireSuccess: true); // evidence-write failure is FATAL on success
            Console.WriteLine();
            Console.WriteLine("ALL E2E STEPS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Evidence.Transcript = Transcript.ToString();
            try { Evidence.WriteFile(requireSuccess: false); } catch { /* best-effort on failure */ }
            Console.Error.WriteLine("E2E FAILED: " + ex.Message);
            return 1;
        }
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

        // ---- pre-run snapshot (D8/§9): ONE pinned block, all reads at that block ----
        PreSnap = await Chain.SnapshotAsync(CollateralUsers);
        Evidence.PreSnapshot = PreSnap;
        Pass("pre-run snapshot pinned at block " + PreSnap.Block);

        Step("1. bind sessions for all participants (backend DemoUsers keys)");
        foreach (var u in new[] { Operator, Institution, Mm1, Mm2, Trader })
            Tokens[u.Address] = await Api.BindSessionAsync(u.Address);
        Pass("sessions bound for " + Tokens.Count + " participants");

        Step("2. fund native USDC gas (18-dec) from treasury; receipts awaited, balances asserted");
        await FundNativeGasAsync();

        Step("3. mint MockUSDC collateral (6-dec) to each collateral user from treasury");
        var treasuryWeb3 = new Web3(new Account(Institution.Key), Rpc);
        foreach (var u in CollateralUsers)
            await SendContractAsync(treasuryWeb3, Addrs.Usdc, new MintFunction { To = u.Address, Amt = BigInteger.Parse(Mint10K) }, "mock-mint-" + u.Name, Acceptance.MintCollateral);
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
            await RecordTxAsync(deposit.TxHash!, u.Name + "-deposit", Acceptance.DepositIndexed);
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
        await AssertPositionsAsync(Expected.Tokens("after-deposits"));

        Step("6. institution posts RFM request (1m preset, unique market per run)");
        var requestCountBefore = await Chain.RequestCount();
        var postStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var marketLabel = "arc-e2e-" + postStart;
        var post = await Api.PostAsync(Tokens[Institution.Address], "/v1/rfm/requests", new
        {
            market = marketLabel,
            side = "yes",
            quantity = Qty1000,
            maxPriceTick = MaxTick,
            minMatch = MinMatch200,
            duration = DurationPreset,   // §11b
        });
        Require(post.TxHash != null, "postRequest tx hash");
        PostTxHash = post.TxHash!;

        // D2: decode RequestPosted from the post tx's OWN receipt (causally tied).
        var postReceipt = await AwaitReceipt(Chain.Web3, PostTxHash, "rfm-postRequest", TimeSpan.FromSeconds(90));
        Evidence.RecordTx("rfm-postRequest", PostTxHash, Addrs.Rfm, "success", Acceptance.RequestPosted, "RequestPosted(requestId=" + CurrentRequestId + ")");
        var posted = Chain.DecodeRequestPosted(postReceipt);
        Require(posted != null, "RequestPosted decoded from the post tx receipt");
        var requestId = posted!.RequestId;
        CurrentRequestId = requestId;
        Require(post.RequestId == requestId.ToString(),
            "API requestId " + post.RequestId + " != decoded requestId " + requestId + " (causal link broken)");
        Require(posted.Market.Equals(Chain.Keccak(marketLabel), StringComparison.OrdinalIgnoreCase),
            "post market commitment is keccak(marketLabel)");
        var requestCountAfter = await Chain.RequestCount();
        Require(requestCountAfter > requestCountBefore,
            "RFM.requestCount() did not increase (" + requestCountBefore + " -> " + requestCountAfter + ")");
        Pass("RFM posted; decoded requestId " + requestId + " (requestCount " + requestCountBefore + " -> " + requestCountAfter + ", tx " + shortHash(PostTxHash) + ")");

        var rfmView = await WaitFor(TimeSpan.FromSeconds(60), async () => await Api.GetRfmAsync(requestId), r => r != null);
        Require(rfmView!.Phase is "open" or "commit", "request mirrored, phase=" + rfmView.Phase);
        var commitDeadline = Unix(rfmView.CommitDeadline);
        var revealDeadline = Unix(rfmView.RevealDeadline);
        Require(commitDeadline > 0 && revealDeadline > commitDeadline, "mirrored deadlines sane");
        // §11b: the 1m preset must mirror exactly 40s commit and a 20s reveal span.
        Require(revealDeadline - commitDeadline == PresetRevealSec,
            "mirrored reveal span " + (revealDeadline - commitDeadline) + "s != " + PresetRevealSec + "s (1m preset)");
        Require(Math.Abs((commitDeadline - postStart) - PresetCommitSec) <= 2,
            "mirrored commit window " + (commitDeadline - postStart) + "s != " + PresetCommitSec + "s (1m preset)");
        Pass("request mirrored (phase " + rfmView.Phase + "); 1m preset windows verified (commit 40s / reveal 20s)");

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
        Require(SameHex(bornLog!.Log.Topics.Length > 2 ? bornLog.Log.Topics[2] : null, born!.MarketId),
            "born marketId matches the observed born");
        await Chain.RecordReceiptAsync(bornLog.Log.TransactionHash, "rfm-finalize", Acceptance.MarketBorn, "MarketBorn(marketId=" + shortHash(born.MarketId) + ")");
        Require(born!.MarginalYesTick != born.VwapYesTick,
            "marginal (" + born.MarginalYesTick + ") must differ from vwap (" + born.VwapYesTick + ")");
        Pass("market born: marginal " + born.MarginalYesTick + " != vwap " + born.VwapYesTick + " (genuine competition)");

        CurrentMarketId = born.MarketId!;
        CurrentYesId = await Chain.TokenIdHex(CurrentMarketId, 0);
        CurrentNoId = await Chain.TokenIdHex(CurrentMarketId, 1);

        Step("10. assert auction positions minted on-chain (expected values, both outcomes) + ledger matches");
        await AssertPositionsAsync(Expected.Tokens("after-birth"));

        Step("11. MINT (BUY YES x BUY NO)");
        await PlaceOrder(Tokens[Institution.Address], "yes", "buy", Trade2000, 600);   // rests YES bid @600
        var mintFills = await PlaceOrder(Tokens[Trader.Address], "no", "buy", Trade2000, 400); // crosses
        RequireFillsCaseInsensitive(mintFills, "MINT");
        await CaptureBatchAsync(1, "settle-mint", mintFills);
        await AssertPositionsAsync(Expected.Tokens("after-mint"));

        Step("12. TRANSFER YES (BUY YES x SELL YES)");
        await PlaceOrder(Tokens[Institution.Address], "yes", "sell", Trade1000, 500);  // rests YES ask @500
        var xferYes = await PlaceOrder(Tokens[Trader.Address], "yes", "buy", Trade1000, 500);
        RequireFillsCaseInsensitive(xferYes, "TRANSFER");
        await CaptureBatchAsync(2, "settle-transfer-yes", xferYes);
        await AssertPositionsAsync(Expected.Tokens("after-transfer-yes"));

        Step("13. TRANSFER NO (BUY NO x SELL NO)");
        await PlaceOrder(Tokens[Trader.Address], "no", "sell", Trade1000, 500);        // rests NO bid @500
        var xferNo = await PlaceOrder(Tokens[Institution.Address], "no", "buy", Trade1000, 500);
        RequireFillsCaseInsensitive(xferNo, "TRANSFER");
        await CaptureBatchAsync(3, "settle-transfer-no", xferNo);
        await AssertPositionsAsync(Expected.Tokens("after-transfer-no"));

        Step("14. MERGE (SELL YES x SELL NO)");
        await PlaceOrder(Tokens[Institution.Address], "yes", "sell", Trade500, 500);   // rests YES ask @500
        var mergeFills = await PlaceOrder(Tokens[Trader.Address], "no", "sell", Trade500, 500);
        RequireFillsCaseInsensitive(mergeFills, "MERGE");
        await CaptureBatchAsync(4, "settle-merge", mergeFills);
        await AssertPositionsAsync(Expected.Tokens("after-merge"));

        Step("15. operator resolves (YES wins); MarketResolved hash via narrow query");
        var resolved = await Api.PostAsync(Tokens[Operator.Address], "/v1/markets/" + CurrentMarketId + "/resolve", new { outcome = "yes" });
        Require(resolved.Resolved == true, "resolve accepted");
        var resView = await WaitFor(TimeSpan.FromSeconds(90), async () =>
            await Api.GetAsync<ResolutionView>(Tokens[Institution.Address], "/v1/resolution/" + CurrentMarketId), r => r is { Resolved: true });
        Require(resView!.Resolved, "market resolved");
        var resolveBlock = await Chain.BlockNumber();
        var resolveLog = await Chain.FindEventAroundAsync(Addrs.OutcomeTokens, resolveBlock, new MarketResolvedEventDTO(), CurrentMarketId, "MarketResolved");
        Require(resolveLog != null, "MarketResolved log not found near block " + resolveBlock);
        await Chain.RecordReceiptAsync(resolveLog!.Log.TransactionHash, "operator-resolve", Acceptance.Resolved, "MarketResolved(marketId=" + shortHash(CurrentMarketId) + ")");
        Pass("market resolved (winning = yes)");

        Step("16. winners redeem 1:1; winner USDC increases by exactly the burned amount");
        var beforeRedeem = await Chain.UsdcBal(Institution.Address);
        var redeem = await Api.PostAsync(Tokens[Institution.Address], "/v1/markets/" + CurrentMarketId + "/redeem", new { amount = Qty1000 });
        Require(redeem.TxHash != null, "redeem tx hash");
        await RecordTxAsync(redeem.TxHash!, "institution-redeem", Acceptance.RedeemExact);
        await WaitUntil(TimeSpan.FromSeconds(180), async () => await Chain.TokenBalHex(Institution.Address, CurrentYesId) == Expected.InstitutionYesAfterRedeem,
            "institution YES remainder after redeem");
        var afterRedeem = await Chain.UsdcBal(Institution.Address);
        Require(afterRedeem - beforeRedeem == BigInteger.Parse(Qty1000),
            "winner USDC increased by " + (afterRedeem - beforeRedeem) + " != burned 1000");
        await AssertPositionsAsync(Expected.Tokens("after-redeem"));
        Pass("redeem: winner +1000 USDC == burned 1000 YES");

        Step("17. terminal state clean: RFM locks zero, no stuck order reservations (USDC or tokens)");
        await AssertTerminalLocksAsync(requestId);
        foreach (var u in CollateralUsers)
        {
            var b = await Api.GetAsync<BalancesView>(Tokens[u.Address], "/v1/balances");
            Require(BigInteger.Parse(b.Reserved) == 0, "stuck order reservation for " + u.Name);
        }
        // Token reservations: assert the venue's token positions equal the on-chain positions for
        // the born market (any remaining sell reservation would make the backend under-report).
        await AssertPositionsAsync(Expected.Tokens("after-redeem"));
        Pass("terminal state clean: RFM locks dead, no stuck USDC or token reservations");

        Step("18. delta conservation (§9, block-pinned)");
        var postSnap = await Chain.SnapshotAsync(CollateralUsers, CurrentYesId, CurrentNoId);
        Evidence.PostSnapshot = postSnap;
        AssertConservation(PreSnap, postSnap);
        Pass("delta conservation reconciles");
    }

    // ------------------------------------------------------------------ helpers

    static void Step(string s) => Console.WriteLine("\n== " + s + " ==");
    internal static void Pass(string s) => Console.WriteLine("  [PASS] " + s);
    static void Require(bool cond, string what)
    {
        if (!cond) throw new DriverAssertion("assertion failed: " + what);
    }

    static bool SameHex(object? a, string? b)
    {
        string Norm(object? v) => (v?.ToString() ?? "").Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? v!.ToString()![2..].ToLowerInvariant()
            : (v?.ToString() ?? "").Trim().ToLowerInvariant();
        var bs = (b ?? "").Trim();
        if (bs.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) bs = bs[2..];
        return Norm(a) == bs.ToLowerInvariant();
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
            await RecordTxAsync(hash, "gas-" + r.Name, Acceptance.GasFunded);
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
        await RecordTxAsync(r.TxHash!, who + "-" + path.Split('/').Last(), Acceptance.SignedQuote);
    }

    static async Task<List<FillView>> PlaceOrder(string token, string outcome, string side, string size, long price)
    {
        var r = await Api.PostAsync(token, "/v1/orders", new { marketId = CurrentMarketId, outcome, side, size, price, type = "limit" });
        if (r.Fills == null) throw new DriverAssertion("order produced no fills: " + JsonSerializer.Serialize(r));
        return r.Fills;
    }

    /// <summary>API fill labels are lowercase; compare case-insensitively (secondary check — the
    /// authoritative class assert is the on-chain settleBatch decode in CaptureBatchAsync).</summary>
    static void RequireFillsCaseInsensitive(List<FillView> fills, string expectedClass)
    {
        Require(fills.Count > 0, expectedClass + " produced fills");
        foreach (var f in fills)
            Require(f.TradeClass.Equals(expectedClass, StringComparison.OrdinalIgnoreCase),
                expectedClass + " fill, got " + f.TradeClass + " (trade " + f.TradeId + ")");
    }

    /// <summary>
    /// Capture the N-th BatchSettled tx on the born market, DECODE its settleBatch calldata, and
    /// assert market/class/parties/tick/size plus that the API fill's tradeId appears in the
    /// on-chain tradeIds (§8.7) — not just the API label.
    /// </summary>
    static async Task CaptureBatchAsync(int nth, string label, List<FillView> fills)
    {
        await WaitTrades(nth);
        var trades = (await Api.GetMarketAsync(CurrentMarketId)).Trades;
        var batchId = trades.Count >= nth ? trades[nth - 1].BatchId : null;
        Require(batchId != null, "batchId for " + label);
        var block = await Chain.BlockNumber();
        var log = await Chain.FindEventAroundAsync(Addrs.Exchange, block, new BatchSettledEventDTO(), batchId, label);
        Require(log != null, label + " BatchSettled log not found near block " + block);
        var txHash = log!.Log.TransactionHash;

        // Correlate the on-chain BatchSettled tradeIds with the API fill ids.
        var settledIds = log.Event.TradeIds.Select(b => "0x" + Convert.ToHexStringLower(b)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fills)
            Require(settledIds.Contains(f.TradeId), label + ": fill tradeId " + f.TradeId + " not in BatchSettled.tradeIds");

        // Decode the settlement tx's calldata and assert the encoded trade (the authoritative class).
        var tx = await Chain.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txHash);
        Require(tx != null && !string.IsNullOrEmpty(tx.Input), label + " has no input calldata");
        var decoded = Chain.DecodeSettleBatch(tx!.Input);
        Require(decoded.Count > 0, label + " settleBatch decoded no trades");
        var expected = Expected.Trade(label);
        var matched = decoded.Any(d => SameHex(d.MarketId, CurrentMarketId)
            && d.Class == expected.Class
            && (expected.Outcome == null || d.Outcome == expected.Outcome)
            && SameHex(d.PartyA, expected.PartyA) && SameHex(d.PartyB, expected.PartyB)
            && d.Tick == expected.Tick && d.Size == expected.Size);
        Require(matched, label + ": decoded settleBatch trade does not match expected " + expected);
        var decodedTradeId = decoded.First(d => SameHex(d.MarketId, CurrentMarketId)).TradeId;
        Require(settledIds.Contains(decodedTradeId), label + ": decoded tradeId not in BatchSettled.tradeIds");

        await Chain.RecordReceiptAsync(txHash, label, Acceptance.CrossingSettled,
            expected.Class switch { 0 => "TRANSFER", 1 => "MINT", _ => "MERGE" } + " tradeId=" + shortHash(decodedTradeId));
    }

    static async Task WaitTrades(int expected)
        => await WaitUntil(TimeSpan.FromSeconds(180), async () => (await Api.GetMarketAsync(CurrentMarketId)).Trades.Count >= expected, expected + " trades settled");

    /// <summary>
    /// §8.6/§8.8 + §8.10: per-participant ABSOLUTE on-chain token balances (born market is unique)
    /// and usdc deltas from the pre-run snapshot, PLUS backend position completeness — every
    /// expected (participant, token) must be reported with the exact amount, and no unexpected
    /// nonzero position on the born market may appear.
    /// </summary>
    static async Task AssertPositionsAsync(IReadOnlyDictionary<Role, ExpectedTokens> expected)
    {
        // On-chain absolute asserts.
        foreach (var (who, want) in expected)
        {
            var usdcDelta = await Chain.UsdcBal(who.Address) - PreSnap.UsdcBalance(who.Address);
            Require(usdcDelta == BigInteger.Parse(want.Usdc), who.Name + " usdc delta " + usdcDelta + " != expected " + want.Usdc);
            if (CurrentMarketId != "")
            {
                var yes = await Chain.TokenBalHex(who.Address, CurrentYesId);
                var no = await Chain.TokenBalHex(who.Address, CurrentNoId);
                Require(yes == want.Yes, who.Name + " YES " + yes + " != expected " + want.Yes);
                Require(no == want.No, who.Name + " NO " + no + " != expected " + want.No);
            }
        }

        // Backend completeness: the API must report exactly the expected nonzero positions for the
        // born market, and nothing else nonzero.
        foreach (var (who, want) in expected)
        {
            if (CurrentMarketId == "") break;
            var b = await Api.GetAsync<BalancesView>(Tokens[who.Address], "/v1/balances");
            var expects = new List<(string Id, BigInteger Amt)>();
            if (want.Yes > 0) expects.Add((CurrentYesId, want.Yes));
            if (want.No > 0) expects.Add((CurrentNoId, want.No));
            var reported = new Dictionary<string, BigInteger>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in b.Positions)
            {
                if (p.MarketId == null || p.Outcome == null || !SameHex(p.MarketId, CurrentMarketId)) continue;
                var id = await Chain.TokenIdHex(p.MarketId!, p.Outcome!.Equals("no", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0);
                reported[id] = BigInteger.Parse(p.Amount);
            }
            foreach (var (id, amt) in expects)
            {
                Require(reported.TryGetValue(id, out var got) && got == amt,
                    who.Name + " backend missing/mismatched position " + shortHash(id) + ": want " + amt + " got " + (reported.TryGetValue(id, out var g) ? g.ToString() : "absent"));
            }
            // Reject unexpected nonzero positions on the born market (an indexer omission of a
            // position we expect is caught above; a phantom position is caught here).
            foreach (var (id, amt) in reported)
            {
                if (amt == 0) continue;
                BigInteger? expectedHere = id.Equals(CurrentYesId, StringComparison.OrdinalIgnoreCase)
                    ? want.Yes
                    : (id.Equals(CurrentNoId, StringComparison.OrdinalIgnoreCase) ? want.No : (BigInteger?)null);
                Require(expectedHere != null && expectedHere.Value == amt,
                    who.Name + " unexpected position " + shortHash(id) + "=" + amt + " on the born market");
            }
        }

        // The general ledger-vs-chain reconciliation (chainFree + reported positions == chain).
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
                }
                return true;
            }
            catch { return false; }
        }, "ledger chainFree == on-chain");
        Pass("per-participant absolute deltas + backend position completeness on-chain");
    }

    /// <summary>§8.10/§9: post-run RFM lock refs (escrow, institution bond, mm bonds, mm reveal
    /// locks) must all be dead — stranded locks are a failure, not "remaining escrow".</summary>
    static async Task AssertTerminalLocksAsync(BigInteger requestId)
    {
        var refs = Chain.RfmLockRefs(requestId);
        foreach (var (label, refHex) in refs)
        {
            var lk = await Chain.LockInfo(refHex);
            Require(!lk.Live && lk.Amount == 0,
                $"RFM lock {label} ({refHex}) still live amount={lk.Amount} user={lk.User} (stranded)");
        }
        foreach (var u in CollateralUsers)
            Require(await Chain.LockedBal(u.Address) == 0, "lockedBal != 0 for " + u.Name + " (stranded RFM lock)");
        Pass("RFM locks dead: escrow + institution bond + both MM bonds + reveal locks");
    }

    static void AssertConservation(Snapshot preSnap, Snapshot postSnap)
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
        // Pool movement for this run == this market's net collateral (split 1000 + 2000 - merge 500
        // - redeem 1000 = 1500) and equals the ABSOLUTE remaining winning (YES) supply (unique market).
        Require(poolDelta == BigInteger.Parse("1500000000"), "pool MockUSDC delta " + poolDelta + " != 1500");
        var remainingYes = postSnap.Tokens.GetValueOrDefault((Institution.Address, CurrentYesId)) + postSnap.Tokens.GetValueOrDefault((Trader.Address, CurrentYesId));
        Require(poolDelta == remainingYes, "pool delta " + poolDelta + " != remaining YES supply " + remainingYes);
        foreach (var u in CollateralUsers)
            Require(postSnap.LockedBalance(u.Address) == 0, "post-run lockedBal != 0 for " + u.Name);
        Require(postSnap.Block >= preSnap.Block, "snapshots pinned to blocks");
        Pass("delta conservation reconciles (snapshots at blocks " + preSnap.Block + " -> " + postSnap.Block + ")");
    }

    // ------------------------------------------------------------------ chain plumbing

    static async Task<string> SendContractAsync(Web3 web3, string to, FunctionMessage msg, string kind, string acceptance)
    {
        var receipt = await web3.Eth.GetContractHandler(to).SendRequestAndWaitForReceiptAsync(msg);
        Require(receipt?.Status?.Value == 1 && receipt.TransactionHash != null, "tx to " + to + " reverted or no receipt");
        Evidence.RecordTx(kind, receipt!.TransactionHash!, to, "success", acceptance, "contract call");
        return receipt.TransactionHash!;
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

    /// <summary>D3: every hash is receipt-awaited with an explicit timeout, then recorded with its
    /// status/block so the evidence proves success (not just a submitted hash).</summary>
    static async Task RecordTxAsync(string txHash, string kind, string acceptance)
    {
        await AwaitReceipt(Chain.Web3, txHash, kind, TimeSpan.FromSeconds(120));
        var receipt = await Chain.Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        var status = receipt?.Status?.Value == 1 ? "success" : "reverted";
        Evidence.RecordTx(kind, txHash, receipt?.To, status, acceptance, "status=" + status + " block=" + receipt?.BlockNumber?.Value);
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
        return new Addresses(Get("usdc"), Get("outcomeTokens"), Get("vault"), Get("exchange"), Get("rfm"), Get("operator", Operator.Address));
    }

    static string RepoRootEvidencePath()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "e2e")) && Directory.Exists(Path.Combine(dir, "backend")))
                return Path.Combine(dir, "e2e", "ARC_LIFECYCLE_PROOF_EVIDENCE.md");
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine("e2e", "ARC_LIFECYCLE_PROOF_EVIDENCE.md"); // fall back to a relative path
    }

    // ------------------------------------------------------------------ expected values (enumerated from the spec, not backend-reported)

    internal sealed record ExpectedTokens(string Usdc, BigInteger Yes, BigInteger No);
    internal sealed record ExpectedTrade(byte Class, byte? Outcome, string PartyA, string PartyB, BigInteger Tick, BigInteger Size)
    {
        public override string ToString() =>
            $"class={(Class == 1 ? "MINT" : Class == 2 ? "MERGE" : "TRANSFER")} outcome={Outcome} partyA={PartyA[..10]} partyB={PartyB[..10]} tick={Tick} size={Size}";
    }

    static class Expected
    {
        public static readonly BigInteger InstitutionYesAfterRedeem = BigInteger.Parse("500000000");

        /// <summary>Absolute YES/NO balances per participant (the born market is unique per run, so
        /// absolute == deltas for tokens) + usdc delta from the pre-run snapshot.</summary>
        public static IReadOnlyDictionary<Role, ExpectedTokens> Tokens(string stage) => stage switch
        {
            "after-deposits" => new Dictionary<Role, ExpectedTokens>
            {
                [Institution] = new("5000000000", 0, 0),
                [Trader] = new("5000000000", 0, 0),
                [Mm1] = new("5000000000", 0, 0),
                [Mm2] = new("5000000000", 0, 0),
            },
            "after-birth" => new Dictionary<Role, ExpectedTokens>
            {
                [Institution] = new("4470000000", BigInteger.Parse(Qty1000), 0),
                [Trader] = new("5000000000", 0, 0),
                [Mm1] = new("4650000000", 0, BigInteger.Parse(MmQty700)),
                [Mm2] = new("4880000000", 0, BigInteger.Parse("300000000")),
            },
            "after-mint" => new Dictionary<Role, ExpectedTokens>
            {
                [Institution] = new("3270000000", BigInteger.Parse("3000000000"), 0),
                [Trader] = new("4200000000", 0, BigInteger.Parse(Trade2000)),
                [Mm1] = new("4650000000", 0, BigInteger.Parse(MmQty700)),
                [Mm2] = new("4880000000", 0, BigInteger.Parse("300000000")),
            },
            "after-transfer-yes" => new Dictionary<Role, ExpectedTokens>
            {
                [Institution] = new("3770000000", BigInteger.Parse("2000000000"), 0),
                [Trader] = new("3700000000", BigInteger.Parse(Trade1000), BigInteger.Parse(Trade2000)),
                [Mm1] = new("4650000000", 0, BigInteger.Parse(MmQty700)),
                [Mm2] = new("4880000000", 0, BigInteger.Parse("300000000")),
            },
            "after-transfer-no" => new Dictionary<Role, ExpectedTokens>
            {
                [Institution] = new("3270000000", BigInteger.Parse("2000000000"), BigInteger.Parse(Trade1000)),
                [Trader] = new("4200000000", BigInteger.Parse(Trade1000), BigInteger.Parse(Trade1000)),
                [Mm1] = new("4650000000", 0, BigInteger.Parse(MmQty700)),
                [Mm2] = new("4880000000", 0, BigInteger.Parse("300000000")),
            },
            "after-merge" => new Dictionary<Role, ExpectedTokens>
            {
                [Institution] = new("3520000000", BigInteger.Parse("1500000000"), BigInteger.Parse(Trade1000)),
                [Trader] = new("4450000000", BigInteger.Parse(Trade1000), BigInteger.Parse("500000000")),
                [Mm1] = new("4650000000", 0, BigInteger.Parse(MmQty700)),
                [Mm2] = new("4880000000", 0, BigInteger.Parse("300000000")),
            },
            "after-redeem" => new Dictionary<Role, ExpectedTokens>
            {
                [Institution] = new("4520000000", InstitutionYesAfterRedeem, BigInteger.Parse(Trade1000)),
                [Trader] = new("4450000000", BigInteger.Parse(Trade1000), BigInteger.Parse("500000000")),
                [Mm1] = new("4650000000", 0, BigInteger.Parse(MmQty700)),
                [Mm2] = new("4880000000", 0, BigInteger.Parse("300000000")),
            },
            _ => throw new DriverAssertion("unknown stage " + stage),
        };

        /// <summary>The single expected on-chain settlement trade for each crossing.</summary>
        public static ExpectedTrade Trade(string label) => label switch
        {
            "settle-mint" => new ExpectedTrade(1, null, Institution.Address, Trader.Address, 600, BigInteger.Parse(Trade2000)),
            "settle-transfer-yes" => new ExpectedTrade(0, 0, Institution.Address, Trader.Address, 500, BigInteger.Parse(Trade1000)),
            "settle-transfer-no" => new ExpectedTrade(0, 1, Trader.Address, Institution.Address, 500, BigInteger.Parse(Trade1000)),
            "settle-merge" => new ExpectedTrade(2, null, Institution.Address, Trader.Address, 500, BigInteger.Parse(Trade500)),
            _ => throw new DriverAssertion("unknown trade label " + label),
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
    static readonly string LocksSel = Selector("locks(bytes32)");
    static readonly string SettleBatchSel = Selector("settleBatch(bytes32,tuple[])");

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

    public Task<BigInteger> NativeBalance(string addr) => NativeBalanceAt(addr, "latest");
    public async Task<BigInteger> NativeBalanceAt(string addr, string blockTag)
    {
        var bp = new Nethereum.RPC.Eth.DTOs.BlockParameter();
        bp.SetValue(blockTag);
        return (await Web3.Eth.GetBalance.SendRequestAsync(addr, bp)).Value;
    }

    public async Task<BigInteger> BlockNumber()
        => (await Web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;

    public async Task<string> OperatorAddress()
    {
        var raw = await CallRaw(_a.OutcomeTokens, OperatorSel, "latest");
        var h = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        h = h.PadLeft(64, '0');
        return "0x" + h[^40..];
    }
    public Task<BigInteger> UsdcBal(string user) => Call(_a.Vault, UsdcBalSel + A32(user), "latest");
    public Task<BigInteger> LockedBal(string user) => Call(_a.Vault, LockedBalSel + A32(user), "latest");
    public Task<BigInteger> TokenBal(string user, BigInteger id) => Call(_a.Vault, TokenBalSel + A32(user) + U256(id), "latest");
    public Task<BigInteger> TokenBalHex(string user, string tokenIdHex) => TokenBal(user, HexToU256(tokenIdHex));
    public Task<BigInteger> TokenId(string marketId, byte outcome) => Call(_a.OutcomeTokens, TokenIdSel + H32(marketId) + U256(outcome), "latest");
    public Task<string> TokenIdHex(string marketId, byte outcome) => TokenId(marketId, outcome).ContinueWith(t => "0x" + t.Result.ToString("x64"));
    public Task<BigInteger> TotalSupply() => Call(_a.Usdc, TotalSupplySel, "latest");
    public Task<BigInteger> RequestCount() => Call(_a.Rfm, RequestCountSel, "latest");
    public Task<BigInteger> MockWalletBalance(string addr) => Call(_a.Usdc, BalanceOfSel + A32(addr), "latest");

    public Task<BigInteger> CallAt(string to, string data, string blockTag) => Call(to, data, blockTag);

    async Task<BigInteger> Call(string to, string data, string blockTag)
        => (await CallRaw(to, data, blockTag)) is var raw && (string.IsNullOrEmpty(raw) || raw == "0x")
            ? BigInteger.Zero
            : BigInteger.Parse("0" + raw[2..], System.Globalization.NumberStyles.HexNumber);

    async Task<string> CallRaw(string to, string data, string blockTag)
    {
        var call = new { from = _a.Operator, to, data = data.ToLowerInvariant() };
        var req = new RpcRequest(Guid.NewGuid().ToString(), "eth_call", new object[] { call, blockTag });
        return await _client.SendRequestAsync<string>(req) ?? "0x";
    }

    public async Task<string> CallRawAt(string to, string data, string blockTag) => await CallRaw(to, data, blockTag);

    // ---- Vault.locks(bytes32) -> (user, amount, live) ----

    public async Task<LockView> LockInfo(string refHex)
    {
        var raw = await CallRaw(_a.Vault, LocksSel + H32(refHex), "latest");
        var h = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        if (h.Length < 192) return new LockView("0x0", BigInteger.Zero, false);
        var user = "0x" + h[24..64];
        var amount = BigInteger.Parse("0" + h[64..128], System.Globalization.NumberStyles.HexNumber);
        var live = h.Length >= 192 && BigInteger.Parse("0" + h[128..192], System.Globalization.NumberStyles.HexNumber) != 0;
        return new LockView(user, amount, live);
    }

    /// <summary>The RFM lock refs for a request: escrow, institution bond, and both MMs' bond+reveal.
    /// Mirrors RFM.sol (keccak of abi.encode).</summary>
    public IReadOnlyList<(string Label, string Ref)> RfmLockRefs(BigInteger requestId)
    {
        var mm1 = Program.Mm1.Address;
        var mm2 = Program.Mm2.Address;
        return new (string, string)[]
        {
            ("escrow", LockRef(requestId, "ESCROW")),
            ("instBond", LockRef(requestId, "INSTBOND")),
            ("mm1Bond", LockRef(requestId, mm1, "BOND")),
            ("mm1Reveal", LockRef(requestId, mm1, "REVEAL")),
            ("mm2Bond", LockRef(requestId, mm2, "BOND")),
            ("mm2Reveal", LockRef(requestId, mm2, "REVEAL")),
        };
    }

    // RFM refs mirror RFM.sol: keccak256(abi.encode(...)). abi.encode of a dynamic string is
    // [offset][len][padded], so the preimage for (uint256, string) is [r][offset=32][len][padded]
    // and for (uint256, address, string) is [r][addr][offset=64][len][padded].
    static string LockRef(BigInteger requestId, string tag) => Keccak(EncodeU256(requestId), EncodeU256(32), EncodeStringAbi(tag));
    static string LockRef(BigInteger requestId, string address, string tag) => Keccak(EncodeU256(requestId), EncodeAddress(address), EncodeU256(64), EncodeStringAbi(tag));

    internal string Keccak(string s) => Keccak(Encoding.UTF8.GetBytes(s));

    internal static string Keccak(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var buf = new byte[total];
        var off = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, buf, off, p.Length); off += p.Length; }
        return "0x" + Convert.ToHexStringLower(Sha3Keccack.Current.CalculateHash(buf));
    }

    static byte[] EncodeU256(BigInteger v)
    {
        var bytes = v.ToByteArray();
        if (bytes.Length > 1 && bytes[^1] == 0 && (bytes[^2] & 0x80) == 0) Array.Resize(ref bytes, bytes.Length - 1);
        var outb = new byte[32];
        for (var i = 0; i < bytes.Length && i < 32; i++) outb[31 - i] = bytes[i];
        return outb;
    }

    static byte[] EncodeAddress(string address)
    {
        var h = address.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        h = h.PadLeft(40, '0').ToLowerInvariant();
        var outb = new byte[32];
        Buffer.BlockCopy(Convert.FromHexString(h), 0, outb, 12, 20);
        return outb;
    }

    static byte[] EncodeStringAbi(string s)
    {
        var raw = Encoding.UTF8.GetBytes(s);
        var padded = new byte[32];
        Buffer.BlockCopy(raw, 0, padded, 0, Math.Min(raw.Length, 32));
        var tail = new byte[64];
        Buffer.BlockCopy(EncodeU256(raw.Length), 0, tail, 0, 32);
        Buffer.BlockCopy(padded, 0, tail, 32, 32);
        return tail; // abi.encode dynamic string tail: [len][padded]
    }

    // ---- settlement calldata decode (§8.7) ----

    public sealed record DecodedTrade(string TradeId, string MarketId, byte Class, byte? Outcome, string PartyA, string PartyB, BigInteger Tick, BigInteger Size);

    /// <summary>Decode settleBatch(bytes32, tuple[] trades) input: batchId, then each Trade tuple
    /// (tradeId, marketId, class, outcome, partyA, partyB, outcomeTick, size).</summary>
    public IReadOnlyList<DecodedTrade> DecodeSettleBatch(string inputHex)
    {
        var h = inputHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? inputHex[2..] : inputHex;
        RequireField(h.Length >= 8 && h[..8].Equals(SettleBatchSel[2..], StringComparison.OrdinalIgnoreCase), "calldata is settleBatch");
        var trades = new List<DecodedTrade>();
        var offsetWords = (int)BigInteger.Parse("0" + h.Substring(72, 64), System.Globalization.NumberStyles.HexNumber);
        var arrayStart = 8 + offsetWords * 2; // hex chars: 8-byte selector + offset bytes * 2
        if (arrayStart + 64 > h.Length) return trades;
        var len = (int)BigInteger.Parse("0" + h.Substring(arrayStart, 64), System.Globalization.NumberStyles.HexNumber);
        for (var i = 0; i < len; i++)
        {
            var baseHex = arrayStart + 64 + i * 8 * 64;
            if (baseHex + 8 * 64 > h.Length) break;
            var tradeId = "0x" + h.Substring(baseHex, 64);
            var marketId = "0x" + h.Substring(baseHex + 64, 64);
            var cls = (byte)BigInteger.Parse("0" + h.Substring(baseHex + 128, 64), System.Globalization.NumberStyles.HexNumber);
            var outcome = (byte)BigInteger.Parse("0" + h.Substring(baseHex + 192, 64), System.Globalization.NumberStyles.HexNumber);
            var partyA = "0x" + h.Substring(baseHex + 256, 64)[24..];
            var partyB = "0x" + h.Substring(baseHex + 320, 64)[24..];
            var tick = BigInteger.Parse("0" + h.Substring(baseHex + 384, 64), System.Globalization.NumberStyles.HexNumber);
            var size = BigInteger.Parse("0" + h.Substring(baseHex + 448, 64), System.Globalization.NumberStyles.HexNumber);
            trades.Add(new DecodedTrade(tradeId, marketId, cls, cls == 0 ? outcome : null, partyA, partyB, tick, size));
        }
        return trades;
    }

    // ---- event decoding + narrow log queries (topic normalization: Nethereum Sha3Signature is bare) ----

    public Program.RequestPostedDecoded? DecodeRequestPosted(TransactionReceipt receipt)
    {
        if (receipt.Logs == null) return null;
        var topic0 = Event<RequestPostedEventDTO>.GetEventABI().Sha3Signature; // bare
        foreach (var log in receipt.Logs)
        {
            if (!log.Address.Equals(_a.Rfm, StringComparison.OrdinalIgnoreCase)) continue;
            var logTopic = log.Topics.Length > 0 ? Strip0x(log.Topics[0]) : null;
            if (logTopic == null || !logTopic.Equals(topic0, StringComparison.OrdinalIgnoreCase)) continue;
            var decoded = Event<RequestPostedEventDTO>.DecodeEvent(log);
            if (decoded == null) return null;
            var e = decoded.Event;
            return new Program.RequestPostedDecoded(e.RequestId, "0x" + Convert.ToHexStringLower(e.Market), log.TransactionHash);
        }
        return null;
    }

    /// <summary>Narrow bounded eth_getLogs around an observed block for one event/topic1.</summary>
    public async Task<EventLog<T>?> FindEventAroundAsync<T>(string address, BigInteger observedBlock, T _, string? topic1, string label)
        where T : IEventDTO, new()
    {
        var topic0 = "0x" + Event<T>.GetEventABI().Sha3Signature; // RPC topics are 0x-prefixed
        foreach (var window in new[] { 3, 8, 20 })
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

    public async Task RecordReceiptAsync(string txHash, string label, string acceptance, string decodedEvent)
    {
        var receipt = await Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        if (receipt == null) throw new DriverAssertion(label + " tx " + txHash + " has no receipt");
        if (receipt.Status?.Value != 1) throw new DriverAssertion(label + " tx " + txHash + " REVERTED (status " + receipt.Status?.Value + ")");
        Program.Evidence.RecordTx(label, txHash, receipt.To, "success", acceptance, decodedEvent);
        Console.WriteLine($"  [tx] {label}: {Short(txHash)} status=success block={receipt.BlockNumber?.Value} {decodedEvent}");
    }

    // ---- block-pinned snapshot (§9) ----

    internal async Task<Snapshot> SnapshotAsync(IReadOnlyList<Program.Role> roles, string? yesId = null, string? noId = null)
    {
        var block = await BlockNumber();
        var blockTag = "0x" + block.ToString("x");
        var s = new Snapshot { Block = block };
        s.MockTotalSupply = await CallAt(_a.Usdc, TotalSupplySel, blockTag);
        s.MockVault = await CallAt(_a.Usdc, BalanceOfSel + A32(Program.Addrs.Vault), blockTag);
        s.MockPool = await CallAt(_a.Usdc, BalanceOfSel + A32(Program.Addrs.OutcomeTokens), blockTag);
        foreach (var r in roles)
        {
            s.MockWallet[r.Address] = await CallAt(_a.Usdc, BalanceOfSel + A32(r.Address), blockTag);
            s.Usdc[r.Address] = await CallAt(_a.Vault, UsdcBalSel + A32(r.Address), blockTag);
            s.Locked[r.Address] = await CallAt(_a.Vault, LockedBalSel + A32(r.Address), blockTag);
            if (yesId != null) s.Tokens[(r.Address, yesId)] = await CallAt(_a.Vault, TokenBalSel + A32(r.Address) + U256(HexToU256(yesId)), blockTag);
            if (noId != null) s.Tokens[(r.Address, noId)] = await CallAt(_a.Vault, TokenBalSel + A32(r.Address) + U256(HexToU256(noId)), blockTag);
        }
        return s;
    }

    public static BigInteger HexToU256(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);
    }

    static string Strip0x(object? v) => (v?.ToString() ?? "").Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? v!.ToString()![2..] : (v?.ToString() ?? "").Trim();

    static void RequireField(bool cond, string what)
    {
        if (!cond) throw new DriverAssertion(what);
    }

    static string Short(string h) => h.Length <= 16 ? h : h[..10] + "…" + h[^4..];

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
}

public sealed record LockView(string User, BigInteger Amount, bool Live);

public sealed class Snapshot
{
    public BigInteger Block;
    public BigInteger MockTotalSupply;
    public BigInteger MockVault;
    public BigInteger MockPool;
    public Dictionary<string, BigInteger> MockWallet = new();
    public Dictionary<string, BigInteger> Usdc = new();
    public Dictionary<string, BigInteger> Locked = new();
    public Dictionary<(string, string), BigInteger> Tokens = new();

    public BigInteger MockWalletBalance(string a) => MockWallet.GetValueOrDefault(a);
    public BigInteger UsdcBalance(string a) => Usdc.GetValueOrDefault(a);
    public BigInteger LockedBalance(string a) => Locked.GetValueOrDefault(a);
}

// ================================================================== evidence bundle

public static class Acceptance
{
    public const string GasFunded = "§8.1 chain identity + gas preflight";
    public const string MintCollateral = "§8.2 collateral minted";
    public const string DepositIndexed = "§8.8 deposits settle + ledger";
    public const string RequestPosted = "§8.2 request posted + decoded RequestPosted";
    public const string SignedQuote = "§8.3/§8.4 commit+reveal";
    public const string MarketBorn = "§8.5 MarketBorn + marginal!=vwap";
    public const string CrossingSettled = "§8.7/§8.8 crossing TradeClass (decoded) + deltas";
    public const string Resolved = "§8.9 resolution";
    public const string RedeemExact = "§8.9 redeem 1:1";
}

public sealed class EvidenceBundle
{
    public readonly List<EvidenceTx> Txs = new();
    public Snapshot? PreSnapshot;
    public Snapshot? PostSnapshot;
    public string? Transcript;
    public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public void RecordTx(string kind, string hash, string? to, string status, string acceptance, string eventSummary)
        => Txs.Add(new EvidenceTx(kind, hash, to ?? "", status, acceptance, ExplorerUrl(hash), eventSummary));

    static string ExplorerUrl(string hash) => "https://testnet.arcscan.app/tx/" + hash;

    /// <summary>Write the evidence bundle. On a SUCCESSFUL run a write failure is FATAL (the bundle
    /// is a deliverable); on a failed run it is best-effort.</summary>
    public void WriteFile(bool requireSuccess)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Arc lifecycle proof - evidence bundle");
            sb.AppendLine();
            sb.AppendLine("Run started (UTC): " + StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Driver/backend commit: " + GitCommit() + " (submission monorepo HEAD)");
            var be = Program.BackendCommitOverride;
            sb.AppendLine("Backend build commit (E2E_BACKEND_COMMIT): " + (be == "" ? "unset - same monorepo HEAD as above" : be));
            sb.AppendLine("Target: chain " + Program.ArcChainId + " RPC " + Program.Rpc);
            sb.AppendLine();
            sb.AppendLine("## Transactions");
            sb.AppendLine();
            sb.AppendLine("| Kind | Tx hash | Explorer | Status | Decoded event | Acceptance item |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var t in Txs)
                sb.AppendLine($"| {t.Kind} | `{t.Hash}` | [arcscan]({t.Url}) | {t.Status} | {t.EventSummary} | {t.Acceptance} |");
            sb.AppendLine();
            sb.AppendLine("## Snapshots (block-pinned)");
            sb.AppendLine();
            AppendSnapshot(sb, "Pre-run", PreSnapshot);
            AppendSnapshot(sb, "Post-run", PostSnapshot);
            sb.AppendLine();
            sb.AppendLine("## Transcript");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(Transcript ?? "(no transcript captured)");
            sb.AppendLine("```");
            File.WriteAllText(Program.EvidenceFile, sb.ToString());
            Console.WriteLine("evidence written to " + Program.EvidenceFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not write evidence bundle: " + ex.Message);
            if (requireSuccess) throw new DriverAssertion("evidence bundle write failed on a successful run: " + ex.Message);
        }
    }

    static void AppendSnapshot(StringBuilder sb, string label, Snapshot? s)
    {
        if (s == null) { sb.AppendLine(label + ": (not captured)"); return; }
        sb.AppendLine(label + " @ block " + s.Block);
        sb.AppendLine("  MockUSDC totalSupply=" + s.MockTotalSupply + " vault=" + s.MockVault + " pool=" + s.MockPool);
        foreach (var a in s.Usdc.Keys.OrderBy(k => k))
        {
            var mock = s.MockWallet.GetValueOrDefault(a);
            var usdc = s.Usdc[a];
            var locked = s.Locked.GetValueOrDefault(a);
            var tokens = string.Join(", ", s.Tokens.Where(kv => kv.Key.Item1 == a).Select(kv => shortHash(kv.Key.Item2) + "=" + kv.Value));
            sb.AppendLine($"  wallet {a}: mockUSDC={mock} usdcBal={usdc} lockedBal={locked} tokens[{tokens}]");
        }
    }

    static string shortHash(string h) => h.Length <= 16 ? h : h[..10] + "…" + h[^4..];

    static string GitCommit()
    {
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

public sealed record EvidenceTx(string Kind, string Hash, string To, string Status, string Acceptance, string Url, string EventSummary);

public sealed class TeeWriter(TextWriter inner, StringBuilder sink) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;
    public override void Write(char value) { inner.Write(value); sink.Append(value); }
    public override void Write(string? value) { inner.Write(value); sink.Append(value); }
    public override void WriteLine(string? value) { inner.WriteLine(value); sink.AppendLine(value); }
}

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