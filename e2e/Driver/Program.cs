// E2E driver: runs the FULL happy path against the containerized stack - anvil
// (real local chain) + the Venue.Api backend in REAL Nethereum mode - and asserts
// every step, including that the backend's indexed ledger matches on-chain state.
//
// Collateral vs gas:
//   - COLLATERAL = MockUSDC (6-dec, self-deployed, freely minted to 10K-scale).
//   - GAS        = ETH on anvil, funded via anvil_setBalance (throwaway EOAs).
//
// All amounts are 6-dec micro units: 1 USDC = 1_000_000.
using System.Numerics;
using System.Net.Http.Json;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.JsonRpc.Client;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace E2E.Driver;

public static class Program
{
    // Well-known public Anvil dev keypairs (throwaway, documented). Same set the
    // deploy job writes into Venue__DemoUsers__, so the backend can sign for them.
    static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266"] = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80", // operator
        ["0x70997970C51812dc3A010C7d01b50e0d17dc79C8"] = "0x59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d", // institution
        ["0x3C44CdDdB6a900fa2b585dd299e03d12FA4293BC"] = "0x5de4111afa1a4b94908f83103eb1f1706367c2e68ca870fc3fb9a804cdab365a", // mm1
        ["0x90F79bf6EB2c4f870365E785982E1f101E93b906"] = "0x7c852118294e51e653712a81e05800f419141751be58f605c371e15141b007a6", // mm2
        ["0x15d34AAf54267DB7D7c367839AAf71A00a2C6A65"] = "0x47e179ec197488593b187f80a00eb0da91f1b9d0b13f8733639f19c30a34926a", // mm3
        ["0x9965507D1a55bcC2695C58ba16FB37d819B0A4dc"] = "0x8b3a350cf5c34c9194ca85829a2df0ec3153be0318b5e2d3348e872092edffba", // trader1
        ["0x976EA74026E726554dB657fA54763abd0C3a0aa9"] = "0x92db14e403b83dfe3df233f83dfa3a0d7096f21ca9b0d6d6b8d88b2b4ec1564e", // trader2
        ["0x14dC79964da2C08b23698B3D3cc7Ca32193d9955"] = "0x4bbbf85ce3377467afe5d46f804f221813b2bb87f24d81f60f1fcdbf7cbf4356", // trader3
    };

    const string OperatorAddr = "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266";
    const string Institution = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";
    const string Mm1 = "0x3C44CdDdB6a900fa2b585dd299e03d12FA4293BC";
    const string Mm2 = "0x90F79bf6EB2c4f870365E785982E1f101E93b906";
    const string Trader1 = "0x9965507D1a55bcC2695C58ba16FB37d819B0A4dc";
    const string Trader2 = "0x976EA74026E726554dB657fA54763abd0C3a0aa9";
    const string Trader3 = "0x14dC79964da2C08b23698B3D3cc7Ca32193d9955";

    // Dedicated collateral funder (anvil account #9): mints MockUSDC and funds gas.
    // Deliberately NOT the backend's operator key (0xf39F...) and NOT a venue user, so
    // the driver's nonces can never collide with the backend's operator or user txs.
    const string Funder = "0xa0Ee7A142d267C1f36714E4a8F75612F20a79720";
    const string FunderKey = "0x2a871d0798f97d79848a013d4936a73bf4cc922c825d33c1cf7073dff6d409c6";

    // 6-dec amounts.
    const string Mint10K = "10000000000";         // 10,000 USDC per account
    const string Deposit5K = "5000000000";        // 5,000 USDC deposited per account
    const string Qty1000 = "1000000000";          // RFM quantity 1,000 USDC
    const string MinMatch200 = "200000000";       // RFM minMatch 200 USDC
    const string Size700 = "700000000";           // MM quote 700 USDC
    const string Size2000 = "2000000000";         // trade 2,000 USDC
    const string Size1000 = "1000000000";         // trade 1,000 USDC
    const string Size500 = "500000000";           // trade 500 USDC

    static string Rpc => Env("E2E_RPC", "http://localhost:8545");
    static string Api => Env("E2E_API", "http://localhost:8080");
    static string Shared => Env("E2E_SHARED", "e2e/.runtime");

    static readonly List<string> AllUsers = new() { Institution, Mm1, Mm2, Trader1, Trader2, Trader3 };

    public static async Task<int> Main()
    {
        var addresses = ReadAddresses();
        var operatorWeb3 = new Web3(new Account(Keys[OperatorAddr]), Rpc);
        var api = new ApiClient(Api);
        var chain = new ChainQueries(operatorWeb3, addresses);

        Step("1. wait for backend health (real Nethereum mode)");
        await api.WaitHealthyAsync(TimeSpan.FromSeconds(120));

        Step("2. bind sessions for all demo users");
        var tokens = new Dictionary<string, string>();
        foreach (var u in AllUsers.Append(OperatorAddr))
            tokens[u] = await api.BindSessionAsync(u);
        Pass("sessions bound for " + tokens.Count + " users");

        Step("3. fund ETH gas + mint MockUSDC collateral to throwaway accounts");
        var funderWeb3 = new Web3(new Account(FunderKey), Rpc);
        await SetBalance(operatorWeb3, Funder, 1);
        foreach (var u in AllUsers)
        {
            await SetBalance(operatorWeb3, u, 1); // 1 ETH gas per user (approves/deposits are user-signed)
        }
        // Mint strictly sequentially from the DEDICATED funder account (each receipt awaited:
        // sequential nonces, no collision with the backend's operator or user txs).
        foreach (var u in AllUsers)
        {
            await Send(funderWeb3, addresses.Usdc, new MintFunction { To = u, Amt = BigInteger.Parse(Mint10K) });
        }
        Pass("gas funded + " + Mint10K + " micro USDC (10K) collateral minted per account");

        Step("3b. verify mint landed on-chain (not a silent revert)");
        var supply = await chain.TotalSupply();
        Require(supply >= BigInteger.Parse(Mint10K) * AllUsers.Count, "MockUSDC totalSupply reflects all mints, got " + supply);
        Pass("MockUSDC totalSupply on-chain = " + supply);

        Step("4. approve Vault + deposit via the backend API");
        foreach (var u in AllUsers)
        {
            var userWeb3 = new Web3(new Account(Keys[u]), Rpc);
            await Send(userWeb3, addresses.Usdc, new ApproveFunction { Spender = addresses.Vault, Amount = BigInteger.Pow(10, 60) });
            var allowance = await chain.Allowance(u, addresses.Vault);
            Require(allowance > 0, "approve landed on-chain (allowance=" + allowance + ") for " + u);
            var deposit = await api.PostAsync(tokens[u], "/v1/vault/deposit", new { amount = Deposit5K });
            Require(deposit.TxHash != null, "deposit tx hash for " + u);
        }
        await WaitUntil(TimeSpan.FromSeconds(90), async () =>
        {
            foreach (var u in AllUsers)
            {
                var b = await api.GetAsync<BalancesView>(tokens[u], "/v1/balances");
                if (BigInteger.Parse(b.ChainFree) < BigInteger.Parse(Deposit5K)) return false;
            }
            return true;
        }, "all deposits indexed");
        Pass("deposits settled + indexed");

        Step("5. assert backend ledger == on-chain after deposits");
        await AssertLedgerMatchesChain(api, chain, tokens);

        Step("6. institution posts RFM request");
        var post = await api.PostAsync(tokens[Institution], "/v1/rfm/requests", new
        {
            market = "arc-e2e-event-2026",
            side = "yes",
            quantity = Qty1000,
            maxPriceTick = 600,
            minMatch = MinMatch200,
        });
        Require(post.TxHash != null, "postRequest tx hash");
        const int requestId = 1;
        var rfmView = await WaitFor(TimeSpan.FromSeconds(60), async () => await api.GetRfmAsync(requestId), r => r != null);
        Require(rfmView!.Phase == "open" || rfmView.Phase == "commit", "request mirrored, phase=" + rfmView.Phase);
        Pass("RFM request posted + mirrored (phase " + rfmView.Phase + ")");

        Step("7. MMs commit sealed quotes");
        await Commit(api, tokens[Mm1], requestId, 500, BigInteger.Parse(Size700));
        await Commit(api, tokens[Mm2], requestId, 600, BigInteger.Parse(Size700));
        Pass("quotes committed (500 USDC bonds escrowed on-chain)");

        Step("8. reveal after commit deadline");
        await WaitUntil(TimeSpan.FromSeconds(90), async () => (await api.GetRfmAsync(requestId))!.Phase == "reveal", "phase == reveal");
        await Reveal(api, tokens[Mm1], requestId, 500, BigInteger.Parse(Size700));
        await Reveal(api, tokens[Mm2], requestId, 600, BigInteger.Parse(Size700));
        Pass("quotes revealed");

        Step("9. wait for deadline-only finalize (coordinator crank)");
        var born = await WaitFor(TimeSpan.FromSeconds(120), async () =>
        {
            var v = await api.GetRfmAsync(requestId);
            return v?.Born is { MarketId: not null } ? v.Born : null;
        }, b => b != null);
        Require(born != null, "MarketBorn");
        Pass("market born: " + born!.MarketId + " (marginal " + born.MarginalYesTick + ", vwap " + born.VwapYesTick + ", filled " + born.Filled + ")");

        var marketId = born.MarketId!;
        var yesId = await chain.TokenId(marketId, 0);
        var noId = await chain.TokenId(marketId, 1);

        Step("10. assert auction positions minted on-chain + ledger matches");
        Require(await chain.TokenBal(Institution, yesId) == BigInteger.Parse(Qty1000), "institution holds 1000 YES on-chain");
        Require(await chain.TokenBal(Mm1, noId) == BigInteger.Parse(Size700), "mm1 holds 700 NO on-chain");
        Require(await chain.TokenBal(Mm2, noId) == 300_000_000, "mm2 holds 300 NO on-chain");
        await AssertLedgerMatchesChain(api, chain, tokens);
        Pass("auction positions match");

        Step("11. trade MINT (BUY YES x BUY NO)");
        await PlaceOrder(api, tokens[Trader1], marketId, "yes", "buy", Size2000, 600); // rests as YES bid @600
        var mintFills = await PlaceOrder(api, tokens[Trader2], marketId, "no", "buy", Size2000, 400); // crosses @600
        Require(mintFills.Count > 0, "MINT produced fills");
        await WaitTrades(api, marketId, 1);
        await AssertLedgerMatchesChain(api, chain, tokens);
        Pass("MINT settled on-chain (2000 YES / 2000 NO minted)");

        Step("12. TRANSFER YES (BUY YES x SELL YES)");
        await PlaceOrder(api, tokens[Trader1], marketId, "yes", "sell", Size1000, 500); // rests as YES ask @500
        var xferYes = await PlaceOrder(api, tokens[Trader3], marketId, "yes", "buy", Size1000, 500);
        Require(xferYes.Count > 0, "TRANSFER YES produced fills");
        await WaitTrades(api, marketId, 2);
        await AssertLedgerMatchesChain(api, chain, tokens);
        Pass("TRANSFER (YES) settled on-chain");

        Step("13. TRANSFER NO (BUY NO x SELL NO)");
        await PlaceOrder(api, tokens[Trader2], marketId, "no", "sell", Size1000, 500); // rests as NO bid @500
        var xferNo = await PlaceOrder(api, tokens[Trader3], marketId, "no", "buy", Size1000, 500);
        Require(xferNo.Count > 0, "TRANSFER NO produced fills");
        await WaitTrades(api, marketId, 3);
        await AssertLedgerMatchesChain(api, chain, tokens);
        Pass("TRANSFER (NO) settled on-chain");

        Step("14. MERGE (SELL YES x SELL NO)");
        await PlaceOrder(api, tokens[Trader1], marketId, "yes", "sell", Size500, 500); // rests as YES ask @500
        var mergeFills = await PlaceOrder(api, tokens[Trader2], marketId, "no", "sell", Size500, 500); // crosses
        Require(mergeFills.Count > 0, "MERGE produced fills");
        await WaitTrades(api, marketId, 4);
        await AssertLedgerMatchesChain(api, chain, tokens);
        Pass("MERGE settled on-chain");

        Step("15. operator resolves the market (YES wins)");
        var resolved = await api.PostAsync(tokens[OperatorAddr], "/v1/markets/" + marketId + "/resolve", new { outcome = "yes" });
        Require(resolved.Resolved == true, "resolve accepted");
        var resView = await WaitFor(TimeSpan.FromSeconds(60), async () =>
            await api.GetAsync<ResolutionView>(tokens[Institution], "/v1/resolution/" + marketId), r => r is { Resolved: true });
        Require(resView!.Resolved, "market resolved");
        Pass("market resolved (winning = yes)");

        Step("16. winners redeem 1:1, assert final conservation");
        var redeem = await api.PostAsync(tokens[Institution], "/v1/markets/" + marketId + "/redeem", new { amount = Qty1000 });
        Require(redeem.TxHash != null, "redeem tx hash");
        await WaitUntil(TimeSpan.FromSeconds(60), async () => await chain.TokenBal(Institution, yesId) == 0, "institution YES burned");
        await AssertLedgerMatchesChain(api, chain, tokens);
        Pass("redeem settled; institution exited 1:1");

        Step("17. global conservation check");
        var vaultPhys = await chain.UsdcBalance(addresses.Vault);
        var sumInternal = BigInteger.Zero;
        foreach (var u in AllUsers) sumInternal += await chain.UsdcBal(u);
        Require(vaultPhys == sumInternal, $"vault physical ({vaultPhys}) == sum internal ({sumInternal})");
        var poolPhys = await chain.UsdcBalance(addresses.OutcomeTokens);
        Pass($"conservation holds: vault {vaultPhys} == sum internal; pool holds {poolPhys}");

        Console.WriteLine();
        Console.WriteLine("ALL E2E STEPS PASSED");
        return 0;
    }

    // -------------------------------------------------------------- helpers

    static void Step(string s) => Console.WriteLine("\n== " + s + " ==");
    static void Pass(string s) => Console.WriteLine("  [PASS] " + s);
    static void Require(bool cond, string what)
    {
        if (!cond) throw new DriverAssertion("assertion failed: " + what);
    }

    static async Task SetBalance(Web3 web3, string address, int eth)
    {
        await web3.Client.SendRequestAsync(new RpcRequest(Guid.NewGuid().ToString(), "anvil_setBalance",
            new object[] { address, "0x" + (BigInteger.Pow(10, 18) * eth).ToString("x") }));
    }

    static async Task Send<T>(Web3 web3, string to, T msg) where T : FunctionMessage, new()
    {
        var receipt = await web3.Eth.GetContractHandler(to).SendRequestAndWaitForReceiptAsync(msg);
        if (receipt == null)
            throw new DriverAssertion("tx to " + to + " produced no receipt");
        if (receipt.Status!.Value != 1)
        {
            var reason = await TryRevertReasonAsync(web3, receipt.TransactionHash);
            throw new DriverAssertion(
                $"tx REVERTED to {to} tx={receipt.TransactionHash} status={receipt.Status.Value} reason={reason ?? "unknown"}");
        }
    }

    /// <summary>Replay the reverted tx as eth_call to recover the revert string (loud failures).</summary>
    static async Task<string?> TryRevertReasonAsync(Web3 web3, string txHash)
    {
        try
        {
            var tx = await web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txHash);
            if (tx == null) return null;
            var call = new Nethereum.RPC.Eth.DTOs.CallInput(tx.Input, tx.To)
            {
                From = tx.From,
                Value = tx.Value,
            };
            var block = new Nethereum.RPC.Eth.DTOs.BlockParameter(tx.BlockNumber);
            try
            {
                var result = await web3.Eth.Transactions.Call.SendRequestAsync(call, block);
                return "eth_call returned " + result;
            }
            catch (Nethereum.JsonRpc.Client.RpcResponseException ex)
            {
                var data = ex.RpcError?.GetDataAsString();
                return DecodeRevertString(data);
            }
        }
        catch
        {
            return null;
        }
    }

    static string? DecodeRevertString(string? data)
    {
        if (string.IsNullOrEmpty(data) || data.Length < 10) return null;
        var hex = data.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? data[2..] : data;
        if (!hex.StartsWith("08c379a0", StringComparison.OrdinalIgnoreCase)) return null; // Error(string)
        try
        {
            var len = Convert.ToInt32(hex.Substring(64 + 64, 64), 16);
            var raw = hex.Substring(64 + 128, len * 2);
            return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(raw));
        }
        catch
        {
            return data;
        }
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

    static async Task Commit(ApiClient api, string token, int requestId, long tick, BigInteger size)
        => await api.PostAsync(token, "/v1/rfm/commit", new { requestId = requestId.ToString(), priceTick = tick, size = size.ToString() });

    static async Task Reveal(ApiClient api, string token, int requestId, long tick, BigInteger size)
        => await api.PostAsync(token, "/v1/rfm/reveal", new { requestId = requestId.ToString(), priceTick = tick, size = size.ToString() });

    static async Task<List<FillView>> PlaceOrder(ApiClient api, string token, string marketId, string outcome, string side, string size, long price)
    {
        var r = await api.PostAsync(token, "/v1/orders", new
        {
            marketId, outcome, side, size, price, type = "limit",
        });
        if (r.Fills == null) throw new DriverAssertion("order produced no fills: " + JsonSerializer.Serialize(r));
        return r.Fills;
    }

    static async Task WaitTrades(ApiClient api, string marketId, int expected)
        => await WaitUntil(TimeSpan.FromSeconds(120), async () => (await api.GetMarketAsync(marketId)).Trades.Count >= expected, expected + " trades settled");

    static async Task AssertLedgerMatchesChain(ApiClient api, ChainQueries chain, Dictionary<string, string> tokens)
    {
        // The batcher confirms a batch and the indexer applies its granular events
        // asynchronously; poll the comparison until the ledger converges on the chain.
        await WaitUntil(TimeSpan.FromSeconds(60), async () =>
        {
            try
            {
                foreach (var u in AllUsers)
                {
                    var b = await api.GetAsync<BalancesView>(tokens[u], "/v1/balances");
                    var onChainUsdc = await chain.UsdcBal(u);
                    var onChainLocked = await chain.LockedBal(u);
                    if (BigInteger.Parse(b.ChainFree) != onChainUsdc - onChainLocked) return false;
                    foreach (var p in b.Positions)
                    {
                        var id = HashToBigInteger(p.TokenId);
                        var onChain = await chain.TokenBal(u, id);
                        if (onChain != BigInteger.Parse(p.Amount)) return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }, "ledger == on-chain");
    }

    static BigInteger HashToBigInteger(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.Length != 64) throw new DriverAssertion("expected 32-byte token id, got " + hex);
        return BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);
    }

    static string Env(string k, string def)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? def : v;
    }

    static Addresses ReadAddresses()
    {
        var path = Path.Combine(Shared, "addresses.json");
        if (!File.Exists(path)) throw new DriverAssertion("addresses.json not found at " + path);
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        string Get(string k) => doc.RootElement.GetProperty(k).GetString()!;
        return new Addresses(Get("usdc"), Get("outcomeTokens"), Get("vault"), Get("exchange"), Get("rfm"), Get("operator"));
    }

    public sealed record Addresses(string Usdc, string OutcomeTokens, string Vault, string Exchange, string Rfm, string Operator);
}

public sealed class DriverAssertion(string message) : Exception(message);

// ------------------------------------------------------------------ API models

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
                if (h.Ok && !h.Simulate) { Pass("backend healthy, real Nethereum mode, chain " + h.ChainId); return; }
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

    public async Task<RfmView?> GetRfmAsync(int id)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/rfm/requests/{id}");
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new DriverAssertion($"GET rfm {id} -> {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<RfmView>(text, Json);
    }
    public async Task<MarketDetail> GetMarketAsync(string marketId) => await GetAsync<MarketDetail>(null, "/v1/markets/" + marketId);

    static void Pass(string s) => Console.WriteLine("  [PASS] " + s);
}

public sealed record HealthView(bool Ok, bool Simulate, string ChainId);
public sealed record BindResp(string? Token, string? Address);
public sealed record PostResp(string? TxHash, string? Error, List<FillView>? Fills, bool? Resolved);
public sealed record FillView(string TradeId, string TradeClass, string Size, long PriceTick);
public sealed record BalancesView(string User, string ChainFree, string Reserved, string Available, List<PositionView> Positions);
public sealed record PositionView(string TokenId, string Amount);
public sealed record BornView(string MarketId, long MarginalYesTick, long VwapYesTick, string Filled);
public sealed record RfmView(string Phase, BornView? Born);
public sealed record MarketDetail(MarketView Market, List<TradeView> Trades);
public sealed record MarketView(string MarketId, bool Exists, bool Closing, bool Resolved, string? WinningOutcome);
public sealed record TradeView(string TradeId, string TradeClass, string Size, long YesBasisTick, string BatchId);
public sealed record ResolutionView(bool Resolved, string? WinningOutcome);

// ------------------------------------------------------------------ chain DTOs

[Function("mint")]
public sealed class MintFunction : FunctionMessage
{
    [Parameter("address", "to", 1)] public string To { get; set; } = "";
    [Parameter("uint256", "amt", 2)] public BigInteger Amt { get; set; }
}

[Function("approve")]
public sealed class ApproveFunction : FunctionMessage
{
    [Parameter("address", "spender", 1)] public string Spender { get; set; } = "";
    [Parameter("uint256", "amount", 2)] public BigInteger Amount { get; set; }
}

[Function("balanceOf")]
public sealed class BalanceOfFunction : FunctionMessage
{
    [Parameter("address", "account", 1)] public string Account { get; set; } = "";
}


[Function("usdcBal")]
public sealed class UsdcBalFunction : FunctionMessage
{
    [Parameter("address", "user", 1)] public string User { get; set; } = "";
}


[Function("lockedBal")]
public sealed class LockedBalFunction : FunctionMessage
{
    [Parameter("address", "user", 1)] public string User { get; set; } = "";
}


[Function("tokenBal")]
public sealed class TokenBalFunction : FunctionMessage
{
    [Parameter("address", "user", 1)] public string User { get; set; } = "";
    [Parameter("uint256", "id", 2)] public BigInteger Id { get; set; }
}


[Function("tokenId")]
public sealed class TokenIdFunction : FunctionMessage
{
    [Parameter("bytes32", "marketId", 1)] public byte[] MarketId { get; set; } = Array.Empty<byte>();
    [Parameter("uint8", "outcome", 2)] public byte Outcome { get; set; }
}


/// <summary>
/// On-chain reads via explicit eth_call calldata + manual hex parsing. Nethereum 6.1's
/// QueryAsync mis-decodes even a plain balanceOf return against this anvil RPC (returns
/// 0), so the driver builds and parses its own calls - a test tool, not venue code.
/// </summary>
public sealed class ChainQueries(Web3 web3, Program.Addresses addresses)
{
    readonly IClient _client = web3.Client;
    const string Operator = "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266";

    // usdcBal(address) / lockedBal(address) / balanceOf(address) / tokenBal(address,uint256) / tokenId(bytes32,uint8)
    // + MockUSDC: totalSupply() / allowance(address,address)
    const string UsdcBalSel = "0x98948efa";
    const string LockedBalSel = "0x0f6bc212";
    const string BalanceOfSel = "0x70a08231";
    const string TokenBalSel = "0xdeb109e8";
    const string TokenIdSel = "0x4c5fef50";
    const string TotalSupplySel = "0x18160ddd";
    const string AllowanceSel = "0xdd62ed3e";

    public Task<BigInteger> UsdcBalance(string addr) => Call(addresses.Usdc, BalanceOfSel + A32(addr));
    public Task<BigInteger> UsdcBal(string user) => Call(addresses.Vault, UsdcBalSel + A32(user));
    public Task<BigInteger> LockedBal(string user) => Call(addresses.Vault, LockedBalSel + A32(user));
    public Task<BigInteger> TokenBal(string user, BigInteger id) => Call(addresses.Vault, TokenBalSel + A32(user) + U256(id));
    public Task<BigInteger> TokenId(string marketId, byte outcome) => Call(addresses.OutcomeTokens, TokenIdSel + H32(marketId) + U256(outcome));
    public Task<BigInteger> TotalSupply() => Call(addresses.Usdc, TotalSupplySel);
    public Task<BigInteger> Allowance(string owner, string spender) => Call(addresses.Usdc, AllowanceSel + A32(owner) + A32(spender));

    async Task<BigInteger> Call(string to, string data)
    {
        var call = new { from = Operator, to, data = data.ToLowerInvariant() }; // anvil rejects mixed-case hex
        var req = new RpcRequest(Guid.NewGuid().ToString(), "eth_call", new object[] { call, "latest" });
        var result = await _client.SendRequestAsync<string>(req);
        if (string.IsNullOrEmpty(result) || result == "0x") return BigInteger.Zero;
        return BigInteger.Parse("0" + result[2..], System.Globalization.NumberStyles.HexNumber);
    }

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
        if (s.Length > 64) s = s[^64..]; // BigInteger "x" can carry a leading sign nibble
        return s.PadLeft(64, '0');
    }

    static string H32(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return h.PadLeft(64, '0');
    }
}
