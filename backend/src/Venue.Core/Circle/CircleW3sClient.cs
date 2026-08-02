using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Venue.Circle;

public sealed record CircleWalletInfo(string Id, string Address);
public sealed record CircleTxInfo(string Id, string State, string? TransactionHash, string? Error);

/// <summary>
/// Minimal Circle Programmable Wallets (w3s) REST client for dev-controlled SCAs on Arc.
/// Entity-secret ciphertext is generated FRESH per sensitive call = RSA-OAEP(SHA-256) of
/// the entity secret bytes with Circle's entity public key (GET /w3s/config/entity/publicKey),
/// base64. Idempotent wallet creation per user ref; gasless contract-execution transactions
/// via Gas Station (feeLevel GAS_LESS). Plain HttpClient, no Circle SDK. Never logs secrets.
/// </summary>
public sealed class CircleW3sClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _entitySecret;
    private readonly string _walletSetId;
    private readonly object _pkLock = new();
    private string? _entityPublicKeyPem;

    public CircleW3sClient(string apiKey, string entitySecret, string walletSetId, string baseUrl)
    {
        _apiKey = apiKey;
        _entitySecret = entitySecret;
        _walletSetId = walletSetId;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    /// <summary>Fetch + cache the entity public key (PEM).</summary>
    public async Task<string> GetEntityPublicKeyAsync(CancellationToken ct)
    {
        lock (_pkLock) { if (_entityPublicKeyPem != null) return _entityPublicKeyPem; }
        var resp = await _http.GetAsync("/w3s/config/entity/publicKey", ct);
        resp.EnsureSuccessStatusCode();
        var env = await ReadEnvelopeAsync<PublicKeyEnvelope>(resp, ct);
        var pem = env?.Data?.PublicKey;
        if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException("Circle did not return an entity public key");
        lock (_pkLock) { _entityPublicKeyPem = pem; }
        return pem;
    }

    /// <summary>Fresh RSA-OAEP(SHA-256) ciphertext of the entity secret, base64.</summary>
    public async Task<string> EncryptEntitySecretAsync(CancellationToken ct)
    {
        var pem = await GetEntityPublicKeyAsync(ct);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var cipher = rsa.Encrypt(Encoding.UTF8.GetBytes(_entitySecret), RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Create/bind ONE dev-controlled SCA per user ref (idempotencyKey = bind-&lt;ref&gt;
    /// so Circle dedupes re-login; the backend store caches the mapping).</summary>
    public async Task<CircleWalletInfo> BindWalletAsync(string userRef, CancellationToken ct)
    {
        var cipher = await EncryptEntitySecretAsync(ct);
        var resp = await _http.PostAsJsonAsync("/w3s/wallets", new
        {
            idempotencyKey = "bind-" + userRef,
            entitySecretCiphertext = cipher,
            blockchain = "ARC",
            accountType = "SCA",
            walletSetId = _walletSetId,
        }, ct);
        var env = await ReadEnvelopeAsync<WalletsEnvelope>(resp, ct);
        var wallet = env?.Data?.FirstOrDefault()?.Wallet;
        if (wallet == null || string.IsNullOrEmpty(wallet.Id) || string.IsNullOrEmpty(wallet.Address))
            throw new InvalidOperationException("Circle wallet create did not return an SCA");
        return new CircleWalletInfo(wallet.Id, wallet.Address);
    }

    /// <summary>Gasless contract-execution transaction (Gas Station: feeLevel GAS_LESS).
    /// Returns the Circle transaction id for status polling.</summary>
    public async Task<string> SubmitContractExecutionAsync(string walletId, string contractAddress,
        string abiFunctionSignature, IReadOnlyList<string> abiParameters, string idempotencyKey, CancellationToken ct)
    {
        var cipher = await EncryptEntitySecretAsync(ct);
        var resp = await _http.PostAsJsonAsync("/w3s/transactions", new
        {
            idempotencyKey,
            entitySecretCiphertext = cipher,
            walletId,
            contractAddress,
            abiFunctionSignature,
            abiParameters = abiParameters.ToArray(),
            feeLevel = "GAS_LESS",
        }, ct);
        var env = await ReadEnvelopeAsync<TxEnvelope>(resp, ct);
        var id = env?.Data?.Id;
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("Circle contractExecution returned no transaction id");
        return id;
    }

    public async Task<CircleTxInfo> GetTransactionAsync(string txId, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"/w3s/transactions/{txId}", ct);
        var env = await ReadEnvelopeAsync<TxEnvelope>(resp, ct);
        var d = env?.Data;
        return new CircleTxInfo(d?.Id ?? txId, d?.State ?? "UNKNOWN", d?.TransactionHash, d?.Error ?? d?.Reason);
    }

    private static async Task<T?> ReadEnvelopeAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : class
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
        if (code != 0)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : text;
            throw new InvalidOperationException($"Circle API error {code}: {msg}");
        }
        return JsonSerializer.Deserialize<T>(text);
    }

    private sealed class PublicKeyEnvelope { public PublicKeyData? Data { get; set; } }
    private sealed class PublicKeyData { public string? PublicKey { get; set; } }

    private sealed class WalletsEnvelope { public List<WalletItem>? Data { get; set; } }
    private sealed class WalletItem { public CircleWalletData? Wallet { get; set; } }
    private sealed class CircleWalletData { public string? Id { get; set; } public string? Address { get; set; } }

    private sealed class TxEnvelope { public TxData? Data { get; set; } }
    private sealed class TxData
    {
        public string? Id { get; set; }
        public string? State { get; set; }
        public string? TransactionHash { get; set; }
        public string? Error { get; set; }
        public string? Reason { get; set; }
    }
}
