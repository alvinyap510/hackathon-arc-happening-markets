import { useMemo, useState } from "react";
import { useStore } from "../lib/store";
import type { Book, BookLevel, Market, OrderDirection, OrderType, OutcomeSide } from "../lib/types";
import { formatUsdc, parseUsdc, tickToPct } from "../lib/format";

const DIRECTIONS: { id: OrderDirection; label: string; side: "BUY" | "SELL"; outcome: OutcomeSide }[] = [
  { id: "BUY_YES", label: "Buy YES", side: "BUY", outcome: "YES" },
  { id: "BUY_NO", label: "Buy NO", side: "BUY", outcome: "NO" },
  { id: "SELL_YES", label: "Sell YES", side: "SELL", outcome: "YES" },
  { id: "SELL_NO", label: "Sell NO", side: "SELL", outcome: "NO" },
];

/** Worst-case cost of sweeping `levels` for `size` units (buys: max cost; sells: min proceeds). */
function sweep(levels: BookLevel[], size: string): { cost: bigint; filled: bigint } {
  let remaining = BigInt(size);
  let cost = 0n;
  for (const l of levels) {
    const avail = BigInt(l.size);
    const take = remaining > avail ? avail : remaining;
    cost += (take * BigInt(l.price)) / 1000n;
    remaining -= take;
    if (remaining === 0n) break;
  }
  return { cost, filled: BigInt(size) - remaining };
}

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
  const touchLevels = book
    ? d.side === "BUY"
      ? d.outcome === "YES"
        ? book.yes.asks
        : book.no.asks
      : d.outcome === "YES"
        ? book.yes.bids
        : book.no.bids
    : [];
  const marketTick = touchLevels[0]?.price ?? null;

  const effTick = type === "MARKET" ? marketTick : Number(tick);
  const sizeBase = parseUsdc(size);

  // limit: single-price quote. market: worst-case sweep across levels (audit: best-touch understates).
  const quote = useMemo(() => {
    if (sizeBase === null || BigInt(sizeBase) <= 0n) return null;
    if (type === "LIMIT") {
      if (!effTick || effTick < 1 || effTick > 999) return null;
      return { cost: (BigInt(sizeBase) * BigInt(effTick)) / 1000n, filled: BigInt(sizeBase) };
    }
    return sweep(touchLevels, sizeBase);
  }, [type, effTick, sizeBase, touchLevels]);

  const place = async () => {
    setError(null);
    if (sizeBase === null || BigInt(sizeBase) <= 0n) return setError("Enter a size.");
    if (effTick === null || effTick < 1 || effTick > 999) return setError("No usable price.");
    setBusy(true);
    try {
      await api.placeOrder({
        marketId: market.marketId,
        outcome: d.outcome,
        side: d.side,
        price: effTick,
        size: sizeBase,
        type,
      });
      await refreshBalances();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Order rejected");
    } finally {
      setBusy(false);
    }
  };

  const partial = quote !== null && quote.filled < (sizeBase === null ? 0n : BigInt(sizeBase));

  return (
    <div className="panel p-4">
      <div className="grid grid-cols-4 gap-1 rounded-lg bg-ink-900 p-1">
        {DIRECTIONS.map((x) => (
          <button
            key={x.id}
            onClick={() => setDirection(x.id)}
            className={`rounded-md px-1 py-1.5 text-[11px] font-semibold transition-colors ${
              direction === x.id
                ? x.outcome === "YES"
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
        <span className="text-ink-400">
          {d.side === "BUY" ? (type === "MARKET" ? "Max cost (sweep)" : "Max cost") : type === "MARKET" ? "Min proceeds (sweep)" : "Proceeds"}
        </span>
        <span className="num text-paper-100">{quote === null ? "—" : `${formatUsdc(quote.cost.toString())} USDC`}</span>
      </div>
      {partial && (
        <p className="mt-1 text-[11px] text-gold-300">
          Book depth covers only {formatUsdc(quote.filled.toString(), 0)} tokens; the remainder rests.
        </p>
      )}

      {error && <p className="mt-2 text-xs text-no-400">{error}</p>}

      <button
        onClick={place}
        disabled={busy || market.status !== "LIVE"}
        className={`btn mt-3 w-full ${
          d.outcome === "YES" ? "bg-yes-500/90 text-ink-950 hover:bg-yes-400" : "bg-no-500/90 text-ink-950 hover:bg-no-400"
        }`}
      >
        {busy ? "Placing…" : d.label}
      </button>
    </div>
  );
}
