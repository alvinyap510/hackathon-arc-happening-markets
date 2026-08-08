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
    /// <summary>Max blocks fetched per eth_getLogs call - a sane span for public RPCs that
    /// enforce a maximum range (2000 is comfortably inside Arc's public-RPC limit; catch-up
    /// across successive polls covers larger gaps).</summary>
    private const ulong MaxBlockSpan = 2000;

    private const int MaxBackoffMs = 30_000;
    private const int InitialBackoffMs = 1_000;

    private readonly IChainGateway _gateway;
    private readonly Func<IReadOnlyList<VenueEvent>, Task> _apply;
    private readonly Func<Task>? _onReplayStart;
    private readonly ulong _startBlock;
    private readonly int _pollIntervalMs;
    private ulong _cursorBlock;
    private string _cursorHash = "";

    public EventIndexer(IChainGateway gateway, Func<IReadOnlyList<VenueEvent>, Task> apply, Func<Task>? onReplayStart, ulong startBlock, int pollIntervalMs = 2000)
    {
        _gateway = gateway;
        _apply = apply;
        _onReplayStart = onReplayStart;
        _startBlock = startBlock;
        _pollIntervalMs = pollIntervalMs;
        _cursorBlock = startBlock;
    }

    public ulong CursorBlock => _cursorBlock;

    /// <summary>
    /// Replay every event from the start block to the head, in order. The replay is a
    /// REBUILD: <paramref name="_onReplayStart"/> clears all derived state first so events are
    /// never applied on top of stale balances (restart + reorg paths).
    /// Paced at the poll interval and retried with exponential backoff per span, so a cold
    /// replay of a large gap lives within public-RPC eth_getLogs rate limits instead of
    /// firing hundreds of calls in a tight burst (the startup-crash defect: a 429 here used
    /// to propagate out of VenueCore.StartAsync and kill the process).
    /// </summary>
    public async Task ReplayAsync(CancellationToken ct)
    {
        if (_onReplayStart != null) await _onReplayStart();
        var from = _startBlock;
        var backoffMs = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Re-fetch the head each iteration so the replay follows the chain forward.
                var latest = await _gateway.LatestBlockAsync(ct);
                var to = Math.Min(from + MaxBlockSpan - 1, latest);
                var events = await _gateway.FetchLogsAsync(from, to, ct);
                backoffMs = 0;
                if (events.Count > 0) await _apply(events);
                _cursorBlock = to;
                if (to >= latest) break;
                from = to + 1;
                await Task.Delay(_pollIntervalMs, ct); // pace: one span per poll interval (proven RPC-safe)
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Retry the SAME span (from does not advance) after an increasing backoff.
                if (backoffMs == 0) backoffMs = InitialBackoffMs;
                else backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                Console.WriteLine($"indexer: replay failed at {from}: {ex.Message}; retrying in {backoffMs}ms");
                try
                {
                    await Task.Delay(backoffMs, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
        // Best-effort cursor hash: if it fails, the next poll re-checks from scratch.
        try
        {
            _cursorHash = await _gateway.GetBlockHashAsync(_cursorBlock, ct);
        }
        catch
        {
            _cursorHash = "";
        }
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
        // Cap the per-call span: if the chain is far ahead, catch up over successive polls.
        var to = Math.Min(latest, _cursorBlock + MaxBlockSpan);
        var events = await _gateway.FetchLogsAsync(_cursorBlock + 1, to, ct);
        if (events.Count > 0) await _apply(events);
        _cursorBlock = to;
        _cursorHash = await _gateway.GetBlockHashAsync(to, ct);
    }

    /// <summary>
    /// Poll loop with exponential backoff on RPC failure (public RPCs rate-limit
    /// eth_getLogs with -32011). On a failure we do ONE eth_getLogs per backoff interval,
    /// growing 1s -> 2s -> ... capped at 30s, and reset instantly on success - so the
    /// indexer lives within the rate limit and recovers when the RPC frees up.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoffMs = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
                backoffMs = 0;
                await Task.Delay(_pollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (backoffMs == 0) backoffMs = InitialBackoffMs;
                else backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                Console.WriteLine($"indexer: poll failed: {ex.Message}; retrying in {backoffMs}ms");
                try
                {
                    await Task.Delay(backoffMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
