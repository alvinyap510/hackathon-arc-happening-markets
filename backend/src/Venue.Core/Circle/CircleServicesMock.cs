namespace Venue.Circle;

/// <summary>
/// In-memory Circle services for the local demo and tests: deterministic SCA address
/// per user reference, instant CCTP bridge completion, gasless reported. The real path
/// (Circle Wallets REST + CCTP attestation polling) is the Http implementation.
/// </summary>
public sealed class CircleServicesMock : ICircleServices
{
    private readonly Dictionary<string, string> _sessions = new();
    private long _bridgeSeq;

    public bool IsReal => false;
    public bool GaslessSupported => true;

    public Task<CircleSession> BindSessionAsync(string userRef, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(userRef, out var address))
        {
            address = DeriveAddress(userRef);
            _sessions[userRef] = address;
        }
        return Task.FromResult(new CircleSession(address, "mock-session-" + userRef.GetHashCode()));
    }

    public Task<string> InitiateBridgeAsync(string amountUsdc, CancellationToken ct)
        => Task.FromResult("bridge-" + ++_bridgeSeq);

    public Task<BridgeStatus> BridgeStatusAsync(string bridgeId, CancellationToken ct)
        => Task.FromResult(new BridgeStatus(bridgeId, "completed", "0x" + new string('a', 64), "0x" + new string('b', 64)));

    private static string DeriveAddress(string userRef)
    {
        // Deterministic demo address from a handle — NOT a real key, demo only.
        var bytes = System.Text.Encoding.UTF8.GetBytes(userRef);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return "0x" + Convert.ToHexStringLower(hash)[..40];
    }
}
