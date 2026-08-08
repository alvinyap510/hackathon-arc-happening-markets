using System.Numerics;
using Venue.Chain;

namespace Venue.Api;

/// <summary>Parsed host configuration (contracts, chain, demo users, circle, RFM windows).</summary>
public sealed class AppConfig
{
    public required ChainConfig Chain { get; init; }
    public required bool Simulate { get; init; }
    /// <summary>Wallet provider: "circle" | "nethereum" | "simulated". Default circle when
    /// running against a real chain (Simulate=false), simulated otherwise.</summary>
    public required string WalletProvider { get; init; }
    public required string OperatorPrivateKey { get; init; }
    public required Dictionary<string, string> DemoUserKeys { get; init; }   // address -> private key (dev-controlled SCAs)
    public required string? CircleApiKey { get; init; }
    public required string? CircleEntitySecretCiphertext { get; init; }
    public required string? CircleEntitySecret { get; init; }
    public required string? CircleWalletSetId { get; init; }
    public required string CircleBaseUrl { get; init; }
    public required string CircleWalletStorePath { get; init; }
    public required int CommitSeconds { get; init; }
    public required int RevealSeconds { get; init; }
    public required string SaltSecret { get; init; }
    public required string Version { get; init; }
    public required string MetadataStorePath { get; init; }
    public required IReadOnlyList<SeedMarket> SeedMarkets { get; init; }
    public required bool SeedMarketsEnabled { get; init; }

    // Public-exposure safety (Caddy HTTPS in front).
    public required string[] CorsAllowedOrigins { get; init; }
    public required int FaucetRatePerMinute { get; init; }
    public required int SessionRatePerMinute { get; init; }
    public required int GlobalRatePerMinute { get; init; }

    /// <summary>Indexer poll interval in ms. Public RPCs rate-limit eth_getLogs, so the
    /// default is RPC-friendly (2s); anvil/e2e profiles can lower it.</summary>
    public required int IndexerPollIntervalMs { get; init; }

    public static AppConfig Load(IConfiguration cfg)
    {
        var chain = cfg.GetSection("Venue:Chain");
        var simulate = cfg.GetValue<bool?>("Venue:Simulate") ?? true;
        var operatorKey = cfg["Venue:OperatorPrivateKey"] ?? "";
        var demoUsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in cfg.GetSection("Venue:DemoUsers").GetChildren())
        {
            var addr = Domain.Addresses.Normalize(section.Key);
            var key = section.Value;
            if (!string.IsNullOrWhiteSpace(key)) demoUsers[addr] = key;
        }

        return new AppConfig
        {
            Chain = new ChainConfig(
                RpcUrl: cfg["Venue:Chain:RpcUrl"] ?? "https://rpc.devnet.arc.io",
                ChainId: ParseBig(cfg["Venue:Chain:ChainId"], 5042002),
                StartBlock: ParseUlong(cfg["Venue:Chain:StartBlock"], 0),
                Vault: cfg["Venue:Chain:Vault"] ?? "0x0000000000000000000000000000000000000001",
                OutcomeTokens: cfg["Venue:Chain:OutcomeTokens"] ?? "0x0000000000000000000000000000000000000002",
                Exchange: cfg["Venue:Chain:Exchange"] ?? "0x0000000000000000000000000000000000000003",
                Rfm: cfg["Venue:Chain:Rfm"] ?? "0x0000000000000000000000000000000000000004",
                Usdc: cfg["Venue:Chain:Usdc"] ?? "0x3600000000000000000000000000000000000000",
                OperatorAddress: Domain.Addresses.Normalize(cfg["Venue:Chain:OperatorAddress"] ?? "0x0000000000000000000000000000000000000005")),
            Simulate = simulate,
            WalletProvider = (cfg["Venue:WalletProvider"] ?? (simulate ? "simulated" : "circle")).ToLowerInvariant(),
            OperatorPrivateKey = operatorKey,
            DemoUserKeys = demoUsers,
            CircleApiKey = cfg["Venue:Circle:ApiKey"],
            CircleEntitySecretCiphertext = cfg["Venue:Circle:EntitySecretCiphertext"],
            CircleEntitySecret = cfg["Venue:Circle:EntitySecret"],
            CircleWalletSetId = cfg["Venue:Circle:WalletSetId"],
            CircleBaseUrl = cfg["Venue:Circle:BaseUrl"] ?? "https://api.circle.com/v1",
            CircleWalletStorePath = cfg["Venue:CircleWalletStorePath"] ?? "data/circle-wallets.json",
            CommitSeconds = cfg.GetValue<int?>("Venue:RfmWindows:CommitSeconds") ?? 120,
            RevealSeconds = cfg.GetValue<int?>("Venue:RfmWindows:RevealSeconds") ?? 60,
            SaltSecret = cfg["Venue:SaltSecret"] ?? "",
            Version = cfg["Venue:Version"] ?? "unknown",
            MetadataStorePath = cfg["Venue:MetadataStorePath"] ?? "data/market-metadata.json",
            SeedMarketsEnabled = cfg.GetValue<bool?>("Venue:SeedMarketsEnabled") ?? true,
            CorsAllowedOrigins = (cfg["Venue:Cors:AllowedOrigins"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            FaucetRatePerMinute = cfg.GetValue<int?>("Venue:RateLimit:FaucetPerMinute") ?? 5,
            SessionRatePerMinute = cfg.GetValue<int?>("Venue:RateLimit:SessionPerMinute") ?? 10,
            GlobalRatePerMinute = cfg.GetValue<int?>("Venue:RateLimit:GlobalPerMinute") ?? 600,
            IndexerPollIntervalMs = cfg.GetValue<int?>("Venue:Indexer:PollIntervalMs") ?? 2000,
            SeedMarkets = new[]
            {
                new SeedMarket("Will Bitcoin close above $120,000 on 2026-08-15?", "Coinbase index price", 1786838340),
                new SeedMarket("Will the Fed cut rates at the September 2026 FOMC?", "OperatorFiat (Fed announcement)", 1789603140),
            },
        };
    }

    private static BigInteger ParseBig(string? v, BigInteger fallback)
        => BigInteger.TryParse(v, out var r) ? r : fallback;

    private static ulong ParseUlong(string? v, ulong fallback)
        => ulong.TryParse(v, out var r) ? r : fallback;
}

/// <summary>A market seeded on-chain at startup (SEAM 2): operator createMarket + G1 metadata.</summary>
public sealed record SeedMarket(string QuestionText, string ResolutionSource, long CloseTime);
