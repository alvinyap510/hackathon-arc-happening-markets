using System.Numerics;
using Venue.Domain;
using Venue.Infrastructure;
using Venue.Settlement;

namespace Venue.Engine;

/// <summary>One off-chain match: the encoded on-chain trade plus the involved orders.</summary>
public sealed record MatchedTrade(SettlementTrade Trade, string MakerOrderId, string TakerOrderId, Order Maker, Order Taker, BigInteger Size);

/// <summary>
/// Pure in-memory matcher: a taker walks the resting book best-price-then-FIFO and
/// every crossing becomes an encoded SettlementTrade. Classification (MINT/MERGE/
/// TRANSFER) is carried on the trade for the settlement batcher. The taker's own
/// Remainder is reduced; the caller decides whether the leftover rests or dies.
/// </summary>
public static class Matcher
{
    /// <summary>Does a taker order at (side, price) cross a resting order at that price?</summary>
    public static bool Crossing(BookSide takerSide, long takerPrice, long restingPrice)
        => takerSide == BookSide.Bid ? restingPrice <= takerPrice : restingPrice >= takerPrice;

    public static List<MatchedTrade> Match(Order taker, OrderBook book, Market market)
    {
        var fills = new List<MatchedTrade>();
        var restingSide = taker.BookSide == BookSide.Bid ? BookSide.Ask : BookSide.Bid;
        while (taker.Remaining > 0)
        {
            var maker = book.Best(restingSide);
            if (maker == null) break;
            if (!Crossing(taker.BookSide, taker.BookPrice, maker.BookPrice)) break;

            var size = taker.Remaining < maker.Remaining ? taker.Remaining : maker.Remaining;
            var seq = market.NextFillSeq();
            var tradeId = Hash.TradeId(market.MarketId, maker.OrderId, taker.OrderId, seq);
            var trade = TradeBuilder.Build(maker, taker, size, maker.BookPrice, tradeId);

            maker.Remaining -= size;
            taker.Remaining -= size;
            fills.Add(new MatchedTrade(trade, maker.OrderId, taker.OrderId, maker, taker, size));
            if (maker.Remaining.IsZero)
            {
                maker.Status = OrderStatus.Filled;
                book.Remove(maker);
            }
        }
        return fills;
    }
}
