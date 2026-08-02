using System.Text.Json;

namespace Venue.Circle;

/// <summary>
/// Restart-durable mapping between a user ref (email) and its dev-controlled Circle SCA
/// (walletId + address). One SCA per user; re-login returns the same wallet. A JSON file,
/// atomically rewritten, loaded at construction - survives restarts.
/// </summary>
public sealed class CircleWalletStore
{
    private sealed record Entry(string Ref, string WalletId, string Address);

    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, Entry> _byRef = new();
    private readonly Dictionary<string, Entry> _byAddress = new(StringComparer.OrdinalIgnoreCase);

    public CircleWalletStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
                foreach (var e in entries)
                {
                    _entries.Add(e);
                    _byRef[e.Ref] = e;
                    _byAddress[Domain.Addresses.Normalize(e.Address)] = e;
                }
            }
        }
        catch
        {
            // Corrupt/partial store must not brick the venue: start empty, overwrite on next save.
        }
    }

    public void Save(string userRef, string walletId, string address)
    {
        var normalized = Domain.Addresses.Normalize(address);
        lock (_lock)
        {
            _byRef.Remove(userRef, out var prior);
            if (prior != null)
            {
                _byAddress.Remove(Domain.Addresses.Normalize(prior.Address));
                _entries.Remove(prior);
            }
            var entry = new Entry(userRef, walletId, normalized);
            _entries.Add(entry);
            _byRef[userRef] = entry;
            _byAddress[normalized] = entry;
            PersistLocked();
        }
    }

    public CircleWalletInfo? ByRef(string userRef)
    {
        lock (_lock)
        {
            return _byRef.TryGetValue(userRef, out var e) ? new CircleWalletInfo(e.WalletId, e.Address) : null;
        }
    }

    public CircleWalletInfo? ByAddress(string address)
    {
        lock (_lock)
        {
            return _byAddress.TryGetValue(Domain.Addresses.Normalize(address), out var e) ? new CircleWalletInfo(e.WalletId, e.Address) : null;
        }
    }

    private void PersistLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Persistence best-effort; the in-memory copy is authoritative for the run.
        }
    }
}
