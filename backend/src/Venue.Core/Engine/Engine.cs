using System.Numerics;
using Venue.Domain;
using Venue.Settlement;
using VaultLedger = Venue.Ledger.Ledger;

namespace Venue.Engine;

public sealed record PlaceResult(
    Order Order,
    IReadOnlyList<MatchedTrade> Fills,
    OrderStatus TerminalStatus); // the order's status after placement (Resting/Filled/Partial)

public sealed record CancelResult(bool Cancelled, string OrderId, BigInteger ReleasedAmount, string ReleasedAsset);

/// <summary>
/// The trading engine: one YES-basis price-time book per market plus the order
/// lifecycle. Reservations are asset-scoped (USDC for BUY, tokenId for SELL) and
/// live in the Ledger. A fill releases its proportional reservation only when the
/// settlement batch confirms; a cancel (or killed market remainder) releases
/// immediately. The insolvency sweep cancels now-underwater resting orders after an
/// adverse balance event (UX courtesy — never the safety mechanism, which is
/// on-chain settleBatch).
/// </summary>
public sealed class Engine
{
    private readonly VaultLedger _ledger;
    private readonly Dictionary<string, Market> _markets;
    private readonly Dictionary<string, OrderBook> _books = new();
    private readonly Dictionary<string, Order> _orders = new();
    private readonly Dictionary<string, Order> _byClientId = new();

    public Engine(VaultLedger ledger, Dictionary<string, Market> markets)
    {
        _ledger = ledger;
        _markets = markets;
    }

    public Order? GetOrder(string orderId) => _orders.TryGetValue(orderId, out var o) ? o : null;

    public IReadOnlyList<Order> RestingOrders() => _orders.Values.Where(o => o.Status == OrderStatus.Resting).ToList();

    public IReadOnlyList<Order> OrdersFor(string user, OrderStatus? status = null)
        => _orders.Values.Where(o => o.User == user && (status == null || o.Status == status)).OrderByDescending(o => o.CreatedAtUnixSec).ToList();

    /// <summary>Restart: discard all volatile orders, books and client-id map (reservations are
    /// cleared separately in the ledger). Balances are rebuilt from events on replay.</summary>
    public void ResetForRestart()
    {
        _books.Clear();
        _orders.Clear();
        _byClientId.Clear();
    }

    private OrderBook Book(string marketId)
    {
        if (!_books.TryGetValue(marketId, out var b)) { b = new OrderBook(); _books[marketId] = b; }
        return b;
    }

    /// <summary>Admit an order: validate, reserve, cross the book, then rest or kill.</summary>
    public PlaceResult Place(OrderRequest req, Market market)
    {
        if (req.Size <= 0) throw new ArgumentException("size must be positive");
        if (req.Type == OrderType.Limit && (req.Price < Prices.MinTick || req.Price > Prices.MaxTick))
            throw new ArgumentOutOfRangeException(nameof(req.Price), "limit price must be 1..999 ticks");

        var (asset, amount) = Order.ReserveFor(market.MarketId, req.Outcome, req.Side, req.Size, req.Price);
        if (_ledger.Available(req.User, asset) < amount)
            return Reject(req, market, asset, amount, "insufficient_available");
        if (req.ClientOrderId != null && _byClientId.ContainsKey(req.ClientOrderId))
            return Reject(req, market, asset, amount, "duplicate_client_order_id");

        _ledger.Reserve(req.User, asset, amount);
        var order = BuildOrder(req, market, asset, amount);
        _orders[order.OrderId] = order;
        if (req.ClientOrderId != null) _byClientId[req.ClientOrderId] = order;

        var fills = Matcher.Match(order, Book(market.MarketId), market);
        return FinalizePlacement(order, market, fills);
    }

    private PlaceResult FinalizePlacement(Order order, Market market, List<MatchedTrade> fills)
    {
        var book = Book(market.MarketId);
        if (order.Remaining.IsZero)
        {
            order.Status = OrderStatus.Filled;
        }
        else if (order.IsMarket)
        {
            // fill-and-kill: release the killed remainder's reservation immediately.
            var killed = ProportionalReservation(order, order.Remaining);
            _ledger.ReleaseReservation(order.User, order.ReservedAsset, killed);
            order.ReleaseReservation(killed);
            order.Status = OrderStatus.Partial;
        }
        else
        {
            book.Add(order);
            order.Status = OrderStatus.Resting;
        }
        return new PlaceResult(order, fills, order.Status);
    }

    private static PlaceResult Reject(OrderRequest req, Market market, string asset, BigInteger amount, string reason)
    {
        var order = new Order
        {
            OrderId = "rejected:" + Guid.NewGuid().ToString("N"),
            User = req.User,
            MarketId = market.MarketId,
            Outcome = req.Outcome,
            Side = req.Side,
            Size = req.Size,
            Remaining = req.Size,
            Price = req.Price,
            Type = req.Type,
            Status = OrderStatus.Rejected,
            BookSide = Order.StoredSideFor(req.Side, req.Outcome),
            BookPrice = Order.StoredPriceFor(req.Outcome, req.Price),
            ReservedAsset = asset,
            ReservedAmount = amount,
            CreatedAtUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpirationUnixSec = req.ExpirationUnixSec,
        };
        return new PlaceResult(order, Array.Empty<MatchedTrade>(), OrderStatus.Rejected);
    }

    private Order BuildOrder(OrderRequest req, Market market, string asset, BigInteger amount)
    {
        var bookPrice = req.Type == OrderType.Market
            ? (req.Side == OrderSide.Buy ? Order.MarketBuyBookPrice : Order.MarketSellBookPrice)
            : Order.StoredPriceFor(req.Outcome, req.Price);
        var orderId = req.ClientOrderId != null ? "o_" + req.ClientOrderId : "o_" + Guid.NewGuid().ToString("N");
        return new Order
        {
            OrderId = orderId,
            User = req.User,
            MarketId = market.MarketId,
            Outcome = req.Outcome,
            Side = req.Side,
            Size = req.Size,
            Remaining = req.Size,
            Price = req.Price,
            Type = req.Type,
            Status = OrderStatus.New,
            BookSide = Order.StoredSideFor(req.Side, req.Outcome),
            BookPrice = bookPrice,
            ReservedAsset = asset,
            ReservedAmount = amount,
            CreatedAtUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpirationUnixSec = req.ExpirationUnixSec,
        };
    }

    /// <summary>Cancel a resting order; releases the un-released reservation.</summary>
    public CancelResult Cancel(string orderId)
    {
        if (!_orders.TryGetValue(orderId, out var order)) return new CancelResult(false, orderId, BigInteger.Zero, Assets.Usdc);
        if (order.Status != OrderStatus.Resting && order.Status != OrderStatus.New)
            return new CancelResult(false, orderId, BigInteger.Zero, Assets.Usdc);

        if (order.Status == OrderStatus.Resting) Book(order.MarketId).Remove(order);
        var release = order.ReservedAmount - order.ReleasedReservation;
        if (release > 0)
        {
            _ledger.ReleaseReservation(order.User, order.ReservedAsset, release);
            order.ReleaseReservation(release);
        }
        order.Status = OrderStatus.Cancelled;
        _orders.Remove(orderId);
        RemoveClientId(order);
        return new CancelResult(true, orderId, release, order.ReservedAsset);
    }

    private void RemoveClientId(Order order)
    {
        var key = _byClientId.FirstOrDefault(kv => ReferenceEquals(kv.Value, order)).Key;
        if (key != null) _byClientId.Remove(key);
    }

    /// <summary>
    /// A settlement batch confirmed on chain. Releases the proportional reservation of
    /// each involved maker/taker order (the physical movement already reached chainFree
    /// through the granular events). Orders whose reservation is fully released are
    /// dropped from the live map.
    /// </summary>
    public void OnBatchConfirmed(IEnumerable<MatchedTrade> confirmed)
    {
        foreach (var m in confirmed)
        {
            ReleaseConfirmedReservation(m.MakerOrderId, m.Size);
            ReleaseConfirmedReservation(m.TakerOrderId, m.Size);
        }
        foreach (var order in _orders.Values.Where(o => o.ReleasedReservation >= o.ReservedAmount).ToList())
        {
            if (order.Status == OrderStatus.Resting) Book(order.MarketId).Remove(order);
            order.Status = OrderStatus.Settled;
            _orders.Remove(order.OrderId);
            RemoveClientId(order);
        }
    }

    public void ReleaseConfirmedReservation(string orderId, BigInteger filledSize)
    {
        if (!_orders.TryGetValue(orderId, out var o)) return;
        if (o.Size.IsZero) return;
        var amount = ProportionalReservation(o, filledSize);
        _ledger.ReleaseReservation(o.User, o.ReservedAsset, amount);
        o.ReleaseReservation(amount);
    }

    private static BigInteger ProportionalReservation(Order o, BigInteger portion)
        => o.ReservedAmount * portion / o.Size;

    /// <summary>
    /// Adverse-event sweep: after a balance-reducing event, cancel resting orders whose
    /// remaining commitment now exceeds available. Pure UX courtesy; safety is on-chain.
    /// </summary>
    public IReadOnlyList<Order> InsolvencySweep()
        => InsolvencySweep(user: null);

    public IReadOnlyList<Order> InsolvencySweep(string? user)
    {
        var doomed = new List<Order>();
        foreach (var order in RestingOrders().Where(o => user == null || o.User == user))
        {
            var commitment = ProportionalReservation(order, order.Remaining);
            var available = _ledger.Available(order.User, order.ReservedAsset);
            if (available < commitment)
            {
                Cancel(order.OrderId);
                doomed.Add(order);
            }
        }
        return doomed;
    }

    /// <summary>Project the YES and NO books from the single YES-basis book.</summary>
    public BookSnapshot BookSnapshot(string marketId)
    {
        if (!_markets.TryGetValue(marketId, out var market)) throw new KeyNotFoundException(marketId);
        var book = Book(marketId);
        var yesBids = new List<BookLevel>();
        var yesAsks = new List<BookLevel>();
        var noBids = new List<BookLevel>();
        var noAsks = new List<BookLevel>();
        foreach (var o in book.Iterate(BookSide.Bid))
        {
            if (o.Outcome == Outcome.Yes) AddLevel(yesBids, o.BookPrice, o.Remaining);
            else AddLevel(noAsks, Prices.Complement(o.BookPrice), o.Remaining); // SELL NO -> NO ask
        }
        foreach (var o in book.Iterate(BookSide.Ask))
        {
            if (o.Outcome == Outcome.Yes) AddLevel(yesAsks, o.BookPrice, o.Remaining);
            else AddLevel(noBids, Prices.Complement(o.BookPrice), o.Remaining); // BUY NO -> NO bid
        }
        return new BookSnapshot(marketId, market.BookGeneration,
            yesBids.OrderByDescending(l => l.Price).ToList(), yesAsks.OrderBy(l => l.Price).ToList(),
            noBids.OrderByDescending(l => l.Price).ToList(), noAsks.OrderBy(l => l.Price).ToList());
    }

    private static void AddLevel(List<BookLevel> levels, long price, BigInteger size)
    {
        var idx = levels.FindIndex(l => l.Price == price);
        if (idx >= 0) levels[idx] = levels[idx] with { Size = levels[idx].Size + size };
        else levels.Add(new BookLevel(price, size));
    }

    /// <summary>Bump every market's book generation (restart / birth) so WS clients resnapshot.</summary>
    public void BumpAllGenerations()
    {
        foreach (var m in _markets.Values) m.BookGeneration++;
    }
}
