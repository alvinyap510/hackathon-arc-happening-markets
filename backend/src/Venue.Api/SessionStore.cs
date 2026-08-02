using System.Collections.Concurrent;

namespace Venue.Api;

/// <summary>In-memory SCA session store: session token → bound wallet address.</summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, string> _sessions = new();

    public string Create(string address)
    {
        var token = "sess_" + Guid.NewGuid().ToString("N");
        _sessions[token] = Venue.Domain.Addresses.Normalize(address);
        return token;
    }

    public string? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return _sessions.TryGetValue(token, out var address) ? address : null;
    }
}
