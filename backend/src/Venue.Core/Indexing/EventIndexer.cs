using Venue.Chain;
using Venue.Domain;

namespace Venue.Indexing;

/// <summary>
/// Event indexer (PLAN_BACKEND §1): eth_getLogs range catch-up is the authoritative
/// feed; a WebSocket subscription is only a wake-up hint (not wired in the build). The
/// cursor is (blockNumber, blockHash, logIndex) — the block hash is checked on every
/// poll so a reorg forces a full replay. Recovery = replay from the deploy block; no
/// journal, no snapshot.
/// </summary>
public sealed class EventIndexer
{
    private const ulong ChunkSize = 5000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IChainGateway _gateway;
    private readonly Func<IReadOnlyList<VenueEvent>, Task> _apply;
    private readonly Func<Task>? _onReplayStart;
    private readonly ulong _startBlock;
    private ulong _cursorBlock;
    private string _cursorHash = "";

    public EventIndexer(IChainGateway gateway, Func<IReadOnlyList<VenueEvent>, Task> apply, Func<Task>? onReplayStart, ulong startBlock)
    {
        _gateway = gateway;
        _apply = apply;
        _onReplayStart = onReplayStart;
        _startBlock = startBlock;
        _cursorBlock = startBlock;
    }

    public ulong CursorBlock => _cursorBlock;

    /// <summary>Replay every event from the start block to the head, in order. The replay is a
    /// REBUILD: <paramref name="_onReplayStart"/> clears all derived state first so events are
    /// never applied on top of stale balances (restart + reorg paths).</summary>
    public async Task ReplayAsync(CancellationToken ct)
    {
        if (_onReplayStart != null) await _onReplayStart();
        var latest = await _gateway.LatestBlockAsync(ct);
        var from = _startBlock;
        while (from <= latest)
        {
            ct.ThrowIfCancellationRequested();
            var to = Math.Min(from + ChunkSize - 1, latest);
            var events = await _gateway.FetchLogsAsync(from, to, ct);
            if (events.Count > 0) await _apply(events);
            _cursorBlock = to;
            from = to + 1;
        }
        _cursorHash = await _gateway.GetBlockHashAsync(_cursorBlock, ct);
    }

    /// <summary>Fetch new logs since the cursor; replay from scratch if the cursor block reorged.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        if (_cursorHash.Length > 0)
        {
            var current = await _gateway.GetBlockHashAsync(_cursorBlock, ct);
            if (!string.IsNullOrEmpty(current) && current != _cursorHash)
            {
                _cursorHash = "";
                await ReplayAsync(ct);
                return;
            }
        }

        var latest = await _gateway.LatestBlockAsync(ct);
        if (latest <= _cursorBlock) return;
        var events = await _gateway.FetchLogsAsync(_cursorBlock + 1, latest, ct);
        if (events.Count > 0) await _apply(events);
        _cursorBlock = latest;
        _cursorHash = await _gateway.GetBlockHashAsync(latest, ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"indexer: poll failed: {ex.Message}");
            }
            await Task.Delay(PollInterval, ct);
        }
    }
}
