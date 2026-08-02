using System.Text.Json;

namespace Venue.Domain;

/// <summary>Off-chain market metadata (question text, resolution source, close time) keyed
/// by the deterministic marketHash - the bytes32 `market` preimage the RFM contract commits
/// on RequestPosted. The chain carries NO metadata; it is served from this store by hash.</summary>
public sealed record MarketMetadata(string? QuestionText, string? ResolutionSource, long? CloseTime);

/// <summary>
/// Restart-durable store for RFM market metadata (INTEGRATION_CONTRACT G1): a single
/// JSON file written atomically on every mutation, loaded at construction. Keyed by the
/// deterministic marketHash, so a born market's text survives process restarts.
/// </summary>
public sealed class MarketMetadataStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, MarketMetadata> _byHash = new(StringComparer.OrdinalIgnoreCase);

    public MarketMetadataStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, MarketMetadata>>(json);
                if (loaded != null) _byHash = new Dictionary<string, MarketMetadata>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // A corrupt/partial store must never brick the venue: start empty and overwrite on next save.
            _byHash = new Dictionary<string, MarketMetadata>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(string marketHash, string? questionText, string? resolutionSource, long? closeTime)
    {
        if (string.IsNullOrWhiteSpace(marketHash)) return;
        lock (_lock)
        {
            _byHash[Infrastructure.Hash.NormalizeBytes32(marketHash)] = new MarketMetadata(questionText, resolutionSource, closeTime);
            PersistLocked();
        }
    }

    public MarketMetadata? Get(string marketHash)
    {
        if (string.IsNullOrWhiteSpace(marketHash)) return null;
        lock (_lock)
        {
            return _byHash.TryGetValue(Infrastructure.Hash.NormalizeBytes32(marketHash), out var meta) ? meta : null;
        }
    }

    private void PersistLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byHash));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; the in-memory copy is authoritative for the run.
        }
    }
}
