using System.Numerics;
using Venue.Domain;

namespace Venue.Settlement;

/// <summary>
/// Classifies a matched maker/taker pair and encodes it as the on-chain
/// CTFExchangeLite.Trade tagged union (PLAN_CONTRACTS §2). Mirrors the production
/// deriveMatchType: BUY×BUY = MINT, SELL×SELL = MERGE, else TRANSFER (same outcome).
/// </summary>
public static class TradeBuilder
{
    public static TradeClass Classify(Order maker, Order taker)
    {
        if (maker.Side == OrderSide.Buy && taker.Side == OrderSide.Buy) return TradeClass.Mint;
        if (maker.Side == OrderSide.Sell && taker.Side == OrderSide.Sell) return TradeClass.Merge;
        return TradeClass.Transfer;
    }

    public static SettlementTrade Build(Order maker, Order taker, BigInteger size, long yesBasisTick, string tradeId)
    {
        var cls = Classify(maker, taker);
        switch (cls)
        {
            case TradeClass.Mint:
            case TradeClass.Merge:
                var yesOrder = maker.Outcome == Outcome.Yes ? maker : taker;
                var noOrder = maker.Outcome == Outcome.No ? maker : taker;
                return new SettlementTrade(tradeId, maker.MarketId, cls, null, yesOrder.User, noOrder.User, yesBasisTick, size);

            default: // TRANSFER — same outcome by construction
                var seller = maker.Side == OrderSide.Sell ? maker : taker;
                var buyer = maker.Side == OrderSide.Buy ? maker : taker;
                var outcomeTick = maker.Outcome == Outcome.Yes ? yesBasisTick : Prices.Complement(yesBasisTick);
                return new SettlementTrade(tradeId, maker.MarketId, cls, maker.Outcome, seller.User, buyer.User, outcomeTick, size);
        }
    }
}
