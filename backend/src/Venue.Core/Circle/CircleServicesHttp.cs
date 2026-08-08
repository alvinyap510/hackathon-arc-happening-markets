using System.Net.Http.Json;

namespace Venue.Circle;

/// <summary>
/// Real Circle REST client (Wallets SCA lifecycle + CCTP bridge). Activated only when a
/// Circle API key is configured; otherwise the host falls back to the Mock. The exact
/// endpoint shapes were authored from the developers.circle.com documentation surface
/// captured in the STACK pad; treat as best-effort until diffed against the live API on
/// Arc testnet. Never logs credentials.
/// </summary>
public sealed class CircleServicesHttp : ICircleServices
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _entitySecretCiphertext;
    private readonly string _baseUrl;

    public CircleServicesHttp(string apiKey, string entitySecretCiphertext, string baseUrl = "https://api.circle.com/v1")
    {
        _apiKey = apiKey;
        _entitySecretCiphertext = entitySecretCiphertext;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public bool IsReal => true;
    public bool GaslessSupported => true;

    public async Task<CircleSession> BindSessionAsync(string userRef, CancellationToken ct)
    {
        // Circle Wallets: create a dev-controlled wallet for the user, then fetch a
        // signer (entity-secret-based) so the backend can submit transactions gaslessly.
        var resp = await _http.PostAsJsonAsync($"{_baseUrl}/wallets", new
        {
            idempotencyKey = $"bind-{userRef}",
            entitySecretCiphertext = _entitySecretCiphertext,
            blockchain = "ARC",
            accountType = "SCA",
        }, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CircleWalletResponse>(ct);
        return new CircleSession(body?.Wallet?.Address ?? "", "session-" + (body?.Wallet?.Id ?? "0"));
    }

    public async Task<string> InitiateBridgeAsync(string amountUsdc, CancellationToken ct)
    {
        // App Kit / CCTP: create a cross-chain transfer (Base Sepolia -> Arc).
        var resp = await _http.PostAsJsonAsync($"{_baseUrl}/w3s/bridge/transactions", new
        {
            amount = amountUsdc,
            source = new { chain = "base-sepolia", token = "USDC" },
            destination = new { chain = "arc" },
        }, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CircleBridgeResponse>(ct);
        return body?.Id ?? "";
    }

    public async Task<BridgeStatus> BridgeStatusAsync(string bridgeId, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"{_baseUrl}/w3s/bridge/transactions/{bridgeId}", ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CircleBridgeResponse>(ct);
        return new BridgeStatus(bridgeId, body?.Status ?? "unknown", body?.SourceTxHash, body?.ArcTxHash);
    }

    private sealed class CircleWalletResponse
    {
        public CircleWallet? Wallet { get; set; }
    }

    private sealed class CircleWallet
    {
        public string? Id { get; set; }
        public string? Address { get; set; }
    }

    private sealed class CircleBridgeResponse
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
        public string? SourceTxHash { get; set; }
        public string? ArcTxHash { get; set; }
    }
}
