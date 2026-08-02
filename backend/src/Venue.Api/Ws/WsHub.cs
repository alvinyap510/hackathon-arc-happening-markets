using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Venue.Broadcasting;
using Venue.Chain;
using Venue.Domain;

namespace Venue.Api.Ws;

/// <summary>
/// WebSocket hub implementing IEventSink. Channels: book:&lt;mkt&gt;, trades:&lt;mkt&gt;,
/// rfm:&lt;reqId&gt;, user:&lt;addr&gt;. Per-channel (generation, seq, prevSeq); subscribe
/// ⇒ snapshot first; a generation bump or a seq gap makes the client resnapshot (via the
/// REST surface). Snapshot/data payloads are built lazily from the venue core so they can
/// never drift from ledger/engine state. One writer task per connection drains an outbound
/// queue; sink methods only enqueue (non-blocking).
/// </summary>
public sealed class WsHub : IEventSink
{
    private readonly VenueCore _core;
    private readonly object _sync = new();
    private readonly HashSet<Connection> _connections = new();
    private readonly Dictionary<string, long> _channelGen = new();
    private readonly Dictionary<string, long> _channelSeq = new();
    private long _generation = 1;

    public WsHub(VenueCore core)
    {
        _core = core;
    }

    public async Task AcceptAsync(WebSocket ws, CancellationToken ct)
    {
        var conn = new Connection(ws);
        lock (_sync) _connections.Add(conn);
        try
        {
            var reader = Task.Run(() => ReadLoopAsync(conn, ct), CancellationToken.None);
            await WriteLoopAsync(conn, ct);
            await reader;
        }
        finally
        {
            lock (_sync) _connections.Remove(conn);
            conn.Dispose();
        }
    }

    // ------------------------------------------------------------- sink (sync enqueue)

    public void BookChanged(string marketId) => Broadcast($"book:{Normalize(marketId)}", "book", () => BuildBookAsync(marketId));

    public void Fills(string marketId, IReadOnlyList<SettlementTrade> trades)
        => Broadcast($"trades:{Normalize(marketId)}", "trades", () => Task.FromResult<object>(trades.Select(t => new
        {
            tradeId = t.TradeId,
            marketId = t.MarketId,
            tradeClass = t.Class.ToString().ToLowerInvariant(),
            outcome = t.Outcome?.ToString().ToLowerInvariant(),
            partyA = t.PartyA,
            partyB = t.PartyB,
            outcomeTick = t.OutcomeTick,
            size = t.Size.ToString(),
        }).ToList()));

    public void OrderUpdated(string user, string orderId, string status)
        => Broadcast($"user:{Domain.Addresses.Normalize(user)}", "order", () => Task.FromResult<object>(new { orderId, status }));

    public void BalanceChanged(string user)
        => Broadcast($"user:{Domain.Addresses.Normalize(user)}", "balance", () => BuildBalancesAsync(user));

    public void SettlementOutcome(string marketId, string batchId, TxStatus status, string? error, IReadOnlyList<string> tradeIds)
    {
        var channel = $"trades:{Normalize(marketId)}";
        var payload = new
        {
            batchId,
            status = status.ToString().ToLowerInvariant(),
            error,
            tradeIds,
            settledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        Broadcast(channel, "settlement", () => Task.FromResult<object>(payload));
        if (status == TxStatus.Reverted)
            Broadcast(channel, "rejection", () => Task.FromResult<object>(payload));
    }

    public void RfmChanged(BigInteger requestId)
        => Broadcast($"rfm:{requestId}", "rfm", () => BuildRfmAsync(requestId));

    public void MarketBorn(string marketId)
        => Broadcast($"book:{Normalize(marketId)}", "market_born", () => BuildBookAsync(marketId));

    public void GenerationBump()
    {
        lock (_sync)
        {
            _generation++;
            foreach (var key in _channelGen.Keys.ToList()) _channelGen[key] = _generation;
        }
        foreach (var conn in SnapshotAllConnections())
            foreach (var channel in conn.SnapshotSubscriptions())
                conn.Enqueue(BuildSnapshotMessage(channel));
    }

    // --------------------------------------------------------------- broadcasting

    private void Broadcast(string channel, string type, Func<Task<object>> build)
    {
        var (gen, seq, prevSeq) = NextSeq(channel);
        var frame = new Func<Task<object>>(async () => new
        {
            channel,
            type,
            generation = gen,
            seq,
            prevSeq,
            data = await build(),
        });
        foreach (var conn in SnapshotAllConnections())
            if (conn.IsSubscribed(channel)) conn.Enqueue(frame);
    }

    private (long Gen, long Seq, long PrevSeq) NextSeq(string channel)
    {
        lock (_sync)
        {
            if (!_channelGen.TryGetValue(channel, out var gen)) gen = _generation;
            _channelGen[channel] = gen;
            _channelSeq.TryGetValue(channel, out var seq);
            var next = seq + 1;
            _channelSeq[channel] = next;
            return (gen, next, seq);
        }
    }

    private Func<Task<object>> BuildSnapshotMessage(string channel)
    {
        var (gen, seq, prevSeq) = NextSeq(channel);
        return async () => new
        {
            channel,
            type = "snapshot",
            generation = gen,
            seq,
            prevSeq,
            data = await BuildChannelDataAsync(channel),
        };
    }

    private async Task<object> BuildChannelDataAsync(string channel)
    {
        if (channel.StartsWith("book:", StringComparison.Ordinal)) return await BuildBookAsync(channel[5..]);
        if (channel.StartsWith("rfm:", StringComparison.Ordinal)) return await BuildRfmAsync(BigInteger.Parse(channel[4..]));
        if (channel.StartsWith("user:", StringComparison.Ordinal)) return await BuildBalancesAsync(channel[5..]);
        if (channel.StartsWith("trades:", StringComparison.Ordinal)) return await BuildTradesAsync(channel[7..]);
        return new { };
    }

    private async Task<object> BuildBookAsync(string marketId)
    {
        try
        {
            var snap = await _core.GetBookAsync(marketId);
            return new
            {
                marketId = snap.MarketId,
                generation = snap.Generation,
                yes = new { bids = Levels(snap.YesBids), asks = Levels(snap.YesAsks) },
                no = new { bids = Levels(snap.NoBids), asks = Levels(snap.NoAsks) },
            };
        }
        catch (KeyNotFoundException)
        {
            return new { marketId, generation = 0, yes = new { bids = Array.Empty<object>(), asks = Array.Empty<object>() }, no = new { bids = Array.Empty<object>(), asks = Array.Empty<object>() } };
        }
    }

    private static object[] Levels(IReadOnlyList<BookLevel> levels)
        => levels.Select(l => new { price = l.Price, size = l.Size.ToString() }).ToArray();

    private async Task<object> BuildBalancesAsync(string user)
    {
        var b = await _core.GetBalancesAsync(user);
        return new
        {
            address = b.User,
            chainFree = b.ChainFree.ToString(),
            reserved = b.Reserved.ToString(),
            available = b.Available.ToString(),
            positions = b.Positions.Select(p => new { tokenId = p.TokenId, marketId = p.MarketId, outcome = p.Outcome.ToString().ToLowerInvariant(), amount = p.Amount.ToString() }).ToList(),
        };
    }

    private async Task<object> BuildTradesAsync(string marketId)
    {
        try
        {
            var market = await _core.GetMarketAsync(marketId);
            return market.Trades.Select(t => new { tradeId = t.TradeId, tradeClass = t.Class.ToString().ToLowerInvariant(), size = t.Size.ToString(), yesBasisTick = t.YesBasisTick, batchId = t.BatchId, at = t.UnixSec }).ToList();
        }
        catch (KeyNotFoundException)
        {
            return Array.Empty<object>();
        }
    }

    private async Task<object> BuildRfmAsync(BigInteger requestId)
    {
        var r = await _core.GetRfmRequestAsync(requestId);
        if (r == null) return new { requestId = requestId.ToString(), phase = "unknown" };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() < 0 ? BigInteger.Zero : new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return new
        {
            requestId = r.RequestId.ToString(),
            market = r.Market,
            side = r.Side.ToString().ToLowerInvariant(),
            quantity = r.Quantity.ToString(),
            maxPriceTick = r.MaxPriceTick.ToString(),
            minMatch = r.MinMatch.ToString(),
            commitDeadline = r.CommitDeadline.ToString(),
            revealDeadline = r.RevealDeadline.ToString(),
            escrowAmount = r.EscrowAmount.ToString(),
            minQuoteSize = r.MinQuoteSize.ToString(),
            commitCount = r.CommitCount.ToString(),
            phase = r.PhaseAt(now).ToString().ToLowerInvariant(),
            born = r.MarketId == null ? null : new { marketId = r.MarketId, marginalYesTick = r.BornMarginalYesTick, vwapYesTick = r.BornVwapYesTick, filled = r.BornFilledQuantity?.ToString() },
            reveals = r.Reveals.Select(v => new { mm = v.Mm, tick = v.Tick.ToString(), size = v.Size.ToString(), inRange = v.InRange }).ToList(),
        };
    }

    // -------------------------------------------------------------- connection

    private List<Connection> SnapshotAllConnections()
    {
        lock (_sync) return _connections.ToList();
    }

    private async Task ReadLoopAsync(Connection conn, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var ms = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("op", out var op) && op.GetString() == "subscribe"
                    && doc.RootElement.TryGetProperty("channel", out var ch))
                {
                    var channel = ch.GetString();
                    if (channel == null) continue;
                    conn.Subscribe(channel);
                    conn.Enqueue(BuildSnapshotMessage(channel));
                }
                else if (doc.RootElement.TryGetProperty("op", out var op2) && op2.GetString() == "unsubscribe"
                         && doc.RootElement.TryGetProperty("channel", out var ch2))
                {
                    conn.Unsubscribe(ch2.GetString() ?? "");
                }
            }
            catch (JsonException)
            {
                // non-JSON frame — ignore
            }
        }
    }

    private async Task WriteLoopAsync(Connection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Func<Task<object>>? frame = null;
            try
            {
                frame = await conn.DequeueAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (frame == null) break;

            object payload;
            try
            {
                payload = await frame();
            }
            catch (Exception)
            {
                continue;
            }
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await conn.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            catch (WebSocketException)
            {
                break;
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class Connection : IDisposable
    {
        public WebSocket Socket { get; }
        private readonly ConcurrentQueue<Func<Task<object>>> _outbound = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _sync = new();
        private readonly HashSet<string> _channels = new();

        public Connection(WebSocket socket) => Socket = socket;

        public bool IsSubscribed(string channel)
        {
            lock (_sync) return _channels.Contains(channel);
        }

        public void Subscribe(string channel)
        {
            lock (_sync) _channels.Add(channel);
        }

        public void Unsubscribe(string channel)
        {
            lock (_sync) _channels.Remove(channel);
        }

        public List<string> SnapshotSubscriptions()
        {
            lock (_sync) return _channels.ToList();
        }

        public void Enqueue(Func<Task<object>> frame)
        {
            _outbound.Enqueue(frame);
            _signal.Release();
        }

        public async Task<Func<Task<object>>?> DequeueAsync(CancellationToken ct)
        {
            // Loop on spurious wakes: Enqueue releases the semaphore even when the
            // writer took the item via the lock-free TryDequeue path, so the count
            // drifts ahead of the queue. Returning null here made WriteLoopAsync
            // break and zombie the connection after any burst of back-to-back frames.
            while (true)
            {
                if (_outbound.TryDequeue(out var frame)) return frame;
                await _signal.WaitAsync(ct);
            }
        }

        public void Dispose()
        {
            _signal.Dispose();
        }
    }

    private static string Normalize(string id) => id;
}
