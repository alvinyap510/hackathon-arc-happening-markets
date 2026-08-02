using System.Numerics;
using Venue.Infrastructure;

namespace Venue.Rfm;

/// <summary>
/// RFM quote salts. A commit is sealed with a salt; the reveal MUST reproduce it or the
/// MM's 500 USDC bond is slashed. Server-generated salts are therefore DERIVED
/// deterministically from a server secret + (requestId, user), so a reveal after a backend
/// restart reconstructs the exact committed salt with no in-memory store. A client-supplied
/// salt still wins (the client holds it).
///
/// The secret MUST be stable across restarts — the service FAILS FAST at startup when
/// `Venue:SaltSecret` is unset rather than silently regenerating a secret that would make
/// every prior commit unrevealable after a restart.
/// </summary>
public sealed class SaltService
{
    private readonly byte[] _secret;

    public SaltService(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "Venue:SaltSecret is required (env Venue__SaltSecret): the RFM commit salt is derived from it and must survive restarts, or a restart would slash committed MM bonds.");
        _secret = System.Text.Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>Deterministic salt for (requestId, user) — identical before and after a restart.</summary>
    public BigInteger Derive(BigInteger requestId, string user)
    {
        var hash = Hash.KeccakHex(_secret, Hash.EncodeUint256(requestId), Hash.EncodeAddress(user));
        var bytes = Hash.HexToBytes(hash);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }
}
