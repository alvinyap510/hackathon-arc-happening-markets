using System.Numerics;
using Venue.Domain;

namespace Venue.Engine;

/// <summary>
/// Price-time FIFO order book in canonical YES basis. Bids (best = highest) and
/// asks (best = lowest) are integer-tick levels; within a level orders are FIFO.
/// The 4-direction trading surface is normalized at intake (Order.StoredPriceFor /
/// StoredSideFor), so one book serves BUY/SELL × YES/NO.
/// </summary>
public sealed class OrderBook
{
    private static readonly IComparer<long> Desc = Comparer<long>.Create((a, b) => b.CompareTo(a));
    private readonly SortedDictionary<long, Queue<Order>> _bids = new(Desc);
    private readonly SortedDictionary<long, Queue<Order>> _asks = new();

    public int RestingCount { get; private set; }

    public void Add(Order order)
    {
        var levels = order.BookSide == BookSide.Bid ? _bids : _asks;
        if (!levels.TryGetValue(order.BookPrice, out var q)) { q = new Queue<Order>(); levels[order.BookPrice] = q; }
        q.Enqueue(order);
        RestingCount++;
    }

    /// <summary>Remove a specific resting order (FIFO level scan; levels are small).</summary>
    public bool Remove(Order order)
    {
        var levels = order.BookSide == BookSide.Bid ? _bids : _asks;
        if (!levels.TryGetValue(order.BookPrice, out var q)) return false;
        var removed = q.Count > 0 && q.Peek() == order
            ? q.Dequeue() != null
            : RemoveById(q, order.OrderId);
        if (removed) RestingCount--;
        if (q.Count == 0) levels.Remove(order.BookPrice);
        return removed;
    }

    private static bool RemoveById(Queue<Order> q, string orderId)
    {
        var buf = new List<Order>();
        var found = false;
        while (q.Count > 0)
        {
            var o = q.Dequeue();
            if (!found && o.OrderId == orderId) { found = true; continue; }
            buf.Add(o);
        }
        foreach (var o in buf) q.Enqueue(o);
        return found;
    }

    /// <summary>Best-priced resting order on a side, or null.</summary>
    public Order? Best(BookSide side)
    {
        var levels = side == BookSide.Bid ? _bids : _asks;
        if (levels.Count == 0) return null;
        var kv = levels.First();
        return kv.Value.Count > 0 ? kv.Value.Peek() : null;
    }

    /// <summary>Iterate a side best-price-first (each level FIFO).</summary>
    public IEnumerable<Order> Iterate(BookSide side)
    {
        var levels = side == BookSide.Bid ? _bids : _asks;
        foreach (var kv in levels)
            foreach (var o in kv.Value)
                yield return o;
    }

    /// <summary>Aggregate size per price level (book projection).</summary>
    public IEnumerable<BookLevel> Levels(BookSide side)
    {
        var levels = side == BookSide.Bid ? _bids : _asks;
        foreach (var kv in levels)
            yield return new BookLevel(kv.Key, kv.Value.Aggregate(BigInteger.Zero, (acc, o) => acc + o.Remaining));
    }
}
