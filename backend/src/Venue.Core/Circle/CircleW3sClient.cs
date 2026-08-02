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
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    /// <summary>Absolute URL helper - a leading-slash relative path on a BaseAddress would
    /// REPLACE the base path (dropping /v1), so URLs are built explicitly.</summary>
    private Uri U(string path) => new($"{_baseUrl}/{path}");

    /// <summary>Fetch + cache the entity public key (PEM).</summary>
    public async Task<string> GetEntityPublicKeyAsync(CancellationToken ct)
    {
        lock (_pkLock) { if (_entityPublicKeyPem != null) return _entityPublicKeyPem; }
        var resp = await _http.GetAsync(U("w3s/config/entity/publicKey"), ct);
        resp.EnsureSuccessStatusCode();
        var env = await ReadEnvelopeAsync<PublicKeyEnvelope>(resp, ct);
        var pem = env?.Data?.PublicKey;
        if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException("Circle did not return an entity public key");
        lock (_pkLock) { _entityPublicKeyPem = pem; }
        return pem;
    }

    /// <summary>Fresh RSA-OAEP(SHA-256) ciphertext of the entity secret, base64. The secret
    /// is Circle's 32-byte hex string - the DECODED bytes are what must be encrypted (verified
    /// against the live API).</summary>
    public async Task<string> EncryptEntitySecretAsync(CancellationToken ct)
    {
        var pem = await GetEntityPublicKeyAsync(ct);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var cipher = rsa.Encrypt(EntitySecretBytes(), RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(cipher);
    }

    private byte[] EntitySecretBytes()
    {
        var s = _entitySecret.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        var isHex = s.Length % 2 == 0 && s.All(Uri.IsHexDigit);
        return isHex ? Convert.FromHexString(s) : Encoding.UTF8.GetBytes(_entitySecret);
    }

    /// <summary>Create/bind ONE dev-controlled SCA per user ref (idempotencyKey = UUID;
    /// Circle dedupes by it, and the backend store caches the mapping). Verified live:
    /// the create route is /w3s/developer/wallets with blockchains = ["ARC-TESTNET"].</summary>
    public async Task<CircleWalletInfo> BindWalletAsync(string userRef, CancellationToken ct)
    {
        var cipher = await EncryptEntitySecretAsync(ct);
        var resp = await _http.PostAsJsonAsync(U("w3s/developer/wallets"), new
        {
            idempotencyKey = DeterministicUuid("bind-" + userRef),
            entitySecretCiphertext = cipher,
            blockchains = new[] { "ARC-TESTNET" },
            accountType = "SCA",
            walletSetId = _walletSetId,
        }, ct);
        var env = await ReadEnvelopeAsync<WalletsEnvelope>(resp, ct);
        var wallet = env?.Data?.Wallets?.FirstOrDefault();
        if (wallet == null || string.IsNullOrEmpty(wallet.Id) || string.IsNullOrEmpty(wallet.Address))
            throw new InvalidOperationException("Circle wallet create did not return an SCA");
        return new CircleWalletInfo(wallet.Id, wallet.Address);
    }

    /// <summary>Deterministic v5 UUID from a string (stable per user ref -> idempotent re-login).</summary>
    private static string DeterministicUuid(string seed)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // RFC 4122 variant
        var hex = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>Contract-execution transaction from an SCA. On Arc testnet a preconfigured
    /// Gas Station policy auto-sponsors SCA gas, so feeLevel LOW is sufficient (no GAS_LESS
    /// enum). Returns the Circle transaction id for status polling.</summary>
    public async Task<string> SubmitContractExecutionAsync(string walletId, string contractAddress,
        string abiFunctionSignature, IReadOnlyList<string> abiParameters, string idempotencyKey, CancellationToken ct)
    {
        var cipher = await EncryptEntitySecretAsync(ct);
        var resp = await _http.PostAsJsonAsync(U("w3s/developer/transactions/contractExecution"), new
        {
            idempotencyKey,
            entitySecretCiphertext = cipher,
            walletId,
            contractAddress,
            abiFunctionSignature,
            abiParameters = abiParameters.ToArray(),
            feeLevel = "LOW",
        }, ct);
        var env = await ReadEnvelopeAsync<TxEnvelope>(resp, ct);
        var id = env?.Data?.Id;
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("Circle contractExecution returned no transaction id");
        return id;
    }

    public async Task<CircleTxInfo> GetTransactionAsync(string txId, CancellationToken ct)
    {
        var resp = await _http.GetAsync(U($"w3s/transactions/{txId}"), ct);
        var env = await ReadEnvelopeAsync<TxEnvelope>(resp, ct);
        var d = env?.Data?.Transaction ?? env?.Data; // GET nests under data.transaction; create returns data.{id,state}
        return new CircleTxInfo(d?.Id ?? txId, d?.State ?? "UNKNOWN", d?.TransactionHash ?? d?.TxHash, d?.Error ?? d?.Reason);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

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
        return JsonSerializer.Deserialize<T>(text, JsonOpts);
    }

    private sealed class PublicKeyEnvelope { public PublicKeyData? Data { get; set; } }
    private sealed class PublicKeyData { public string? PublicKey { get; set; } }

    private sealed class WalletsEnvelope { public WalletsData? Data { get; set; } }
    private sealed class WalletsData { public List<CircleWalletData>? Wallets { get; set; } }
    private sealed class CircleWalletData { public string? Id { get; set; } public string? Address { get; set; } }

    private sealed class TxEnvelope { public TxData? Data { get; set; } }
    private sealed class TxData
    {
        public string? Id { get; set; }
        public string? State { get; set; }
        public string? TransactionHash { get; set; }
        public string? TxHash { get; set; } // GET shape uses `txHash`
        public string? Error { get; set; }
        public string? Reason { get; set; }
        public TxData? Transaction { get; set; } // GET shape nests under data.transaction
    }
}
