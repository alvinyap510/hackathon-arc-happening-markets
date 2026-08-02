import { useMemo, useState } from "react";
import { useStore } from "../lib/store";
import type { Book, Market, OrderDirection, OrderType } from "../lib/types";
import { formatUsdc, parseUsdc, tickToPct } from "../lib/format";

const DIRECTIONS: { id: OrderDirection; label: string; buy: boolean; yes: boolean }[] = [
  { id: "BUY_YES", label: "Buy YES", buy: true, yes: true },
  { id: "BUY_NO", label: "Buy NO", buy: true, yes: false },
  { id: "SELL_YES", label: "Sell YES", buy: false, yes: true },
  { id: "SELL_NO", label: "Sell NO", buy: false, yes: false },
];

export default function OrderTicket({ market, book }: { market: Market; book: Book | null }) {
  const { api, refreshBalances } = useStore();
  const [direction, setDirection] = useState<OrderDirection>("BUY_YES");
  const [type, setType] = useState<OrderType>("LIMIT");
  const [tick, setTick] = useState("500");
  const [size, setSize] = useState("100");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const d = DIRECTIONS.find((x) => x.id === direction)!;

  // MARKET is emulated client-side as an aggressive limit at the far touch (review R3)
  const marketTick = useMemo(() => {
    if (!book) return null;
    const { yes, no } = book;
    switch (direction) {
      case "BUY_YES":
        return yes.asks[0]?.tick ?? null;
      case "SELL_YES":
        return yes.bids[0]?.tick ?? null;
      case "BUY_NO":
        return no.asks[0]?.tick ?? null;
      case "SELL_NO":
        return no.bids[0]?.tick ?? null;
    }
  }, [book, direction]);

  const effTick = type === "MARKET" ? marketTick : Number(tick);
  const sizeBase = parseUsdc(size);
  const cost = sizeBase !== null && effTick ? (BigInt(sizeBase) * BigInt(effTick)) / 1000n : null;

  const place = async () => {
    setError(null);
    if (sizeBase === null || BigInt(sizeBase) <= 0n) return setError("Enter a size.");
    if (effTick === null || effTick < 1 || effTick > 999) return setError("No usable price.");
    setBusy(true);
    try {
      await api.placeOrder({ marketId: market.marketId, direction, type, tick: effTick, size: sizeBase });
      await refreshBalances();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Order rejected");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="panel p-4">
      <div className="grid grid-cols-4 gap-1 rounded-lg bg-ink-900 p-1">
        {DIRECTIONS.map((x) => (
          <button
            key={x.id}
            onClick={() => setDirection(x.id)}
            className={`rounded-md px-1 py-1.5 text-[11px] font-semibold transition-colors ${
              direction === x.id
                ? x.yes
                  ? "bg-yes-500/20 text-yes-300"
                  : "bg-no-500/20 text-no-300"
                : "text-ink-300 hover:text-paper-200"
            }`}
          >
            {x.label}
          </button>
        ))}
      </div>

      <div className="mt-3 grid grid-cols-2 gap-1 rounded-lg bg-ink-900 p-1">
        {(["LIMIT", "MARKET"] as OrderType[]).map((t) => (
          <button
            key={t}
            onClick={() => setType(t)}
            className={`rounded-md px-2 py-1 text-[11px] font-semibold ${
              type === t ? "bg-ink-700 text-paper-100" : "text-ink-400 hover:text-paper-200"
            }`}
          >
            {t === "LIMIT" ? "Limit" : "Market"}
          </button>
        ))}
      </div>

      <div className="mt-3 grid grid-cols-2 gap-2">
        <div>
          <label className="label-caps">Price (tick)</label>
          {type === "LIMIT" ? (
            <input className="input num mt-1" value={tick} onChange={(e) => setTick(e.target.value.replace(/\D/g, "").slice(0, 4))} />
          ) : (
            <div className="panel-inset num mt-1 px-3 py-2 text-sm text-paper-200">
              {marketTick === null ? "no book" : `${marketTick} (${tickToPct(marketTick)})`}
            </div>
          )}
        </div>
        <div>
          <label className="label-caps">Size (tokens)</label>
          <input className="input num mt-1" value={size} onChange={(e) => setSize(e.target.value)} placeholder="100" />
        </div>
      </div>

      <div className="mt-3 flex items-center justify-between border-t border-ink-700/70 pt-3 text-xs">
        <span className="text-ink-400">{d.buy ? "Max cost" : "Proceeds"}</span>
        <span className="num text-paper-100">{cost === null ? "—" : `${formatUsdc(cost.toString())} USDC`}</span>
      </div>

      {error && <p className="mt-2 text-xs text-no-400">{error}</p>}

      <button
        onClick={place}
        disabled={busy || market.status !== "LIVE"}
        className={`btn mt-3 w-full ${
          d.yes ? "bg-yes-500/90 text-ink-950 hover:bg-yes-400" : "bg-no-500/90 text-ink-950 hover:bg-no-400"
        }`}
      >
        {busy ? "Placing…" : d.label}
      </button>
    </div>
  );
}
