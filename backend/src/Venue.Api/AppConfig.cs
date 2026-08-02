using System.Numerics;
using Venue.Chain;

namespace Venue.Api;

/// <summary>Parsed host configuration (contracts, chain, demo users, circle, RFM windows).</summary>
public sealed class AppConfig
{
    public required ChainConfig Chain { get; init; }
    public required bool Simulate { get; init; }
    public required string OperatorPrivateKey { get; init; }
    public required Dictionary<string, string> DemoUserKeys { get; init; }   // address -> private key (dev-controlled SCAs)
    public required string? CircleApiKey { get; init; }
    public required string? CircleEntitySecretCiphertext { get; init; }
    public required string CircleBaseUrl { get; init; }
    public required int CommitSeconds { get; init; }
    public required int RevealSeconds { get; init; }
    public required string SaltSecret { get; init; }
    public required string MetadataStorePath { get; init; }

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
            OperatorPrivateKey = operatorKey,
            DemoUserKeys = demoUsers,
            CircleApiKey = cfg["Venue:Circle:ApiKey"],
            CircleEntitySecretCiphertext = cfg["Venue:Circle:EntitySecretCiphertext"],
            CircleBaseUrl = cfg["Venue:Circle:BaseUrl"] ?? "https://api.circle.com/v1",
            CommitSeconds = cfg.GetValue<int?>("Venue:RfmWindows:CommitSeconds") ?? 120,
            RevealSeconds = cfg.GetValue<int?>("Venue:RfmWindows:RevealSeconds") ?? 60,
            SaltSecret = cfg["Venue:SaltSecret"] ?? "",
            MetadataStorePath = cfg["Venue:MetadataStorePath"] ?? "data/market-metadata.json",
        };
    }

    private static BigInteger ParseBig(string? v, BigInteger fallback)
        => BigInteger.TryParse(v, out var r) ? r : fallback;

    private static ulong ParseUlong(string? v, ulong fallback)
        => ulong.TryParse(v, out var r) ? r : fallback;
}
