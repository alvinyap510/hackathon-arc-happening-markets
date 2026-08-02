import { useState } from "react";
import { useBook, useOpenOrders, useStore, useTrades } from "../lib/store";
import type { Market } from "../lib/types";
import { formatUsdc, tickToPct } from "../lib/format";
import BookPanel from "../components/BookPanel";
import OrderTicket from "../components/OrderTicket";
import { BornTag } from "../components/MarketCard";

export default function TradingView({ market, onBack }: { market: Market; onBack: () => void }) {
  const { api, balances, refreshBalances, refreshMarkets } = useStore();
  const book = useBook(market.marketId);
  const trades = useTrades(market.marketId);
  const { orders } = useOpenOrders();
  const [redeemBusy, setRedeemBusy] = useState(false);

  const myOrders = orders.filter((o) => o.marketId === market.marketId);
  const myPositions = (balances?.positions ?? []).filter((p) => p.marketId === market.marketId);
  const mid = market.midTick;

  const redeem = async () => {
    setRedeemBusy(true);
    try {
      await api.redeem(market.marketId);
      await Promise.all([refreshBalances(), refreshMarkets()]);
    } finally {
      setRedeemBusy(false);
    }
  };

  const winningPos =
    market.status === "RESOLVED"
      ? myPositions.find((p) => p.outcome === market.winningOutcome && p.size !== "0")
      : undefined;

  return (
    <div className="mx-auto max-w-6xl animate-rise">
      <button onClick={onBack} className="mb-4 text-xs font-semibold text-ink-300 hover:text-paper-100">
        ← All markets
      </button>

      <header className="mb-5 flex flex-wrap items-start justify-between gap-4">
        <div className="max-w-2xl">
          <div className="flex items-center gap-3">
            <h2 className="font-display text-2xl font-semibold leading-snug text-paper-100">{market.questionText}</h2>
            {market.bornFromRfm && <BornTag />}
          </div>
          <p className="mt-1 text-xs text-ink-400">
            Resolves via {market.resolutionSource}
            {market.birth && (
              <span className="ml-2 text-gold-300">
                born funded at marginal {tickToPct(market.birth.marginalTick)}, vwap {tickToPct(market.birth.vwapTick)}
              </span>
            )}
          </p>
        </div>
        {winningPos && (
          <button onClick={redeem} disabled={redeemBusy} className="btn-gold">
            {redeemBusy ? "Redeeming…" : `Redeem ${formatUsdc(winningPos.size)} USDC`}
          </button>
        )}
      </header>

      <div className="grid gap-4 lg:grid-cols-[1fr_320px]">
        <div className="space-y-4">
          <BookPanel book={book} />

          <div className="panel p-4">
            <div className="label-caps mb-2">Recent trades</div>
            {trades.length === 0 && <p className="text-xs text-ink-500">No trades yet.</p>}
            <div className="max-h-44 space-y-0.5 overflow-y-auto">
              {trades.map((t, i) => (
                <div key={i} className="flex items-center justify-between rounded px-2 py-1 text-xs hover:bg-ink-800">
                  <span className={`num font-semibold ${t.takerDirection.includes("BUY") ? "text-yes-400" : "text-no-400"}`}>
                    {tickToPct(t.tick)}
                  </span>
                  <span className="num text-paper-300">{formatUsdc(t.size, 0)}</span>
                  <span className="text-[10px] text-ink-500">{t.takerDirection.replace("_", " ")}</span>
                  <span className="num text-[10px] text-ink-500">{new Date(t.at).toLocaleTimeString()}</span>
                </div>
              ))}
            </div>
          </div>
        </div>

        <div className="space-y-4">
          <OrderTicket market={market} book={book} />

          <div className="panel p-4">
            <div className="label-caps mb-2">Open orders</div>
            {myOrders.length === 0 && <p className="text-xs text-ink-500">None resting.</p>}
            <div className="space-y-1">
              {myOrders.map((o) => (
                <div key={o.orderId} className="flex items-center justify-between rounded bg-ink-900 px-2 py-1.5 text-xs">
                  <span className="num text-paper-200">
                    {o.direction.replace("_", " ")} {formatUsdc(o.size, 0)} @ {o.tick}
                  </span>
                  <button onClick={() => void api.cancelOrder(o.orderId)} className="text-[11px] font-semibold text-no-400 hover:text-no-300">
                    Cancel
                  </button>
                </div>
              ))}
            </div>
          </div>

          <div className="panel p-4">
            <div className="label-caps mb-2">Positions</div>
            {myPositions.length === 0 && <p className="text-xs text-ink-500">No position in this market.</p>}
            <div className="space-y-1">
              {myPositions.map((p) => {
                const px = mid === null ? null : p.outcome === "YES" ? mid : 1000 - mid;
                const value = px === null ? null : (BigInt(p.size) * BigInt(px)) / 1000n;
                return (
                  <div key={p.outcome} className="flex items-center justify-between rounded bg-ink-900 px-2 py-1.5 text-xs">
                    <span className={`font-semibold ${p.outcome === "YES" ? "text-yes-400" : "text-no-400"}`}>{p.outcome}</span>
                    <span className="num text-paper-200">{formatUsdc(p.size, 0)}</span>
                    <span className="num text-[11px] text-ink-300">{value === null ? "—" : `≈ ${formatUsdc(value.toString())}`}</span>
                  </div>
                );
              })}
            </div>
            <p className="mt-2 text-[10px] text-ink-500">Market value at current mid. Settlement is 1:1 to the winner.</p>
          </div>
        </div>
      </div>
    </div>
  );
}
