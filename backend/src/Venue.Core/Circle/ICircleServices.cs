namespace Venue.Circle;

public sealed record CircleSession(string Address, string SessionToken);

public sealed record BridgeStatus(string Id, string Status, string? SourceTxHash, string? ArcTxHash);

/// <summary>
/// The Circle plumbing seam (PLAN_BACKEND "Circle plumbing — library"): SCA lifecycle
/// (Circle Wallets, dev-controlled), CCTP bridge-in (initiate + attestation poll +
/// mint status) and Gas Station (gasless SCA txs — a property of the Circle side, not a
/// call). One interface so the load-bearing product boundaries are visible in one place.
/// A Mock implementation backs the local demo/tests; the Http implementation calls the
/// Circle REST API when credentials are configured.
/// </summary>
public interface ICircleServices
{
    /// <summary>False when running on the mock (no Circle credentials configured).</summary>
    bool IsReal { get; }

    /// <summary>Gas Station sponsors SCA transactions automatically (no per-tx call).</summary>
    bool GaslessSupported { get; }

    /// <summary>Create/bind a dev-controlled SCA session for a user reference (email/handle).</summary>
    Task<CircleSession> BindSessionAsync(string userRef, CancellationToken ct);

    /// <summary>Initiate a Base-Sepolia → Arc CCTP bridge of `amountUsdc` (6-dec) USDC.</summary>
    Task<string> InitiateBridgeAsync(string amountUsdc, CancellationToken ct);

    /// <summary>Poll a CCTP bridge transfer to completion (attestation → mint).</summary>
    Task<BridgeStatus> BridgeStatusAsync(string bridgeId, CancellationToken ct);
}
