import { useMemo, useState } from "react";
import { useStore } from "../lib/store";
import type { Book, BookLevel, Market, OrderType, OutcomeSide } from "../lib/types";
import { foldBook, touchPrice, sweepLevels } from "../lib/bookFold";
import { formatUsdc, parseUsdc, tickToPct } from "../lib/format";
import SellShares from "./SellShares";

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

/** Polymarket-fold ticket: Buy | Sell tabs x YES/NO outcome buttons with live touch
 *  prices from the ONE consolidated book. Sell operates only on held tokens
 *  (available = amount - reserved; the venue stays the reservation authority). */
export default function OrderTicket({ market, book }: { market: Market; book: Book | null }) {
  const { api, balances, refreshBalances } = useStore();
  const [tab, setTab] = useState<"BUY" | "SELL">("BUY");
  const [outcome, setOutcome] = useState<OutcomeSide>("YES");
  const [type, setType] = useState<OrderType>("LIMIT");
  const [pct, setPct] = useState("50.0");
  const [size, setSize] = useState("100");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fold = useMemo(() => (book ? foldBook(book) : null), [book]);
  const touch = (s: "BUY" | "SELL", o: OutcomeSide) => (fold ? touchPrice(fold, s, o) : null);

  // Sell tab: the user's position in the SELECTED outcome, reserved-aware. The Sell
  // TAB is reachable only with a gross holding in EITHER outcome (fresh accounts see
  // it disabled); a held-but-fully-reserved position still reaches the tab, where the
  // zero-available state blocks submit.
  const positions = (balances?.positions ?? []).filter((p) => p.marketId === market.marketId);
  const hasHolding = positions.some((p) => BigInt(p.size) > 0n);
  const position = positions.find((p) => p.outcome === outcome);
  const held = BigInt(position?.size ?? "0");
  const reserved = BigInt(position?.reserved ?? "0");
  const available = held > reserved ? held - reserved : 0n;

  // MARKET is emulated client-side as an aggressive sweep across the FULL canonical
  // opposite side (all four intents see cross-outcome makers — bookFold.sweepLevels).
  const marketLevels = fold ? sweepLevels(fold, tab, outcome) : [];
  const marketTick = marketLevels[0]?.price ?? null;

  // ticks are 0.1% steps, so a 1-decimal percent is lossless: tick = pct x 10
  const effTick = type === "MARKET" ? marketTick : Math.round(Number(pct) * 10);
  const sizeBase = parseUsdc(size);

  const quote = useMemo(() => {
    if (sizeBase === null || BigInt(sizeBase) <= 0n) return null;
    if (type === "LIMIT") {
      if (!effTick || effTick < 1 || effTick > 999) return null;
      return { cost: (BigInt(sizeBase) * BigInt(effTick)) / 1000n, filled: BigInt(sizeBase) };
    }
    return sweep(marketLevels, sizeBase);
  }, [type, effTick, sizeBase, marketLevels]);

  const sellBlocked = tab === "SELL" && available <= 0n;

  const place = async () => {
    setError(null);
    if (sizeBase === null || BigInt(sizeBase) <= 0n) return setError("Enter a size.");
    if (effTick === null || !Number.isFinite(effTick) || effTick < 1 || effTick > 999) return setError("No usable price.");
    setBusy(true);
    try {
      await api.placeOrder({ marketId: market.marketId, outcome, side: tab, price: effTick, size: sizeBase, type });
      await refreshBalances();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Order rejected");
    } finally {
      setBusy(false);
    }
  };

  const partial = quote !== null && quote.filled < (sizeBase === null ? 0n : BigInt(sizeBase));
  const actionLabel = `${tab === "BUY" ? "Buy" : "Sell"} ${outcome}`;

  return (
    <div className="panel p-4">
      {/* Buy | Sell tabs — Sell is reachable only with a gross holding in this market */}
      <div className="flex items-baseline gap-4 border-b border-ink-700/70 pb-2">
        {(["BUY", "SELL"] as const).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            disabled={t === "SELL" && !hasHolding}
            title={t === "SELL" && !hasHolding ? "No shares in this market" : undefined}
            className={`text-sm font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-40 ${
              tab === t ? "text-paper-100 underline decoration-gold-400 decoration-2 underline-offset-8" : "text-ink-400 hover:text-paper-200"
            }`}
          >
            {t === "BUY" ? "Buy" : "Sell"}
          </button>
        ))}
      </div>

      {/* Outcome buttons with live touch prices from the ONE book */}
      <div className="mt-3 grid grid-cols-2 gap-2">
        {(["YES", "NO"] as const).map((o) => {
          const p = touch(tab, o);
          const active = outcome === o;
          return (
            <button
              key={o}
              onClick={() => setOutcome(o)}
              className={`rounded-lg px-3 py-2.5 text-sm font-semibold transition-colors ${
                active
                  ? o === "YES"
                    ? "bg-yes-500/90 text-ink-950"
                    : "bg-no-500/90 text-ink-950"
                  : "bg-ink-900 text-ink-300 hover:text-paper-200"
              }`}
            >
              {o} <span className="num">{p === null ? "—" : tickToPct(p)}</span>
            </button>
          );
        })}
      </div>

      {/* Limit | Market (both tabs) */}
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

      {/* Sell tab: reserved-aware shares + lossless chips */}
      {tab === "SELL" && <SellShares available={available} reserved={reserved} onSize={setSize} />}

      <div className="mt-3 grid grid-cols-2 gap-2">
        <div>
          <label className="label-caps">Price (%)</label>
          {type === "LIMIT" ? (
            <input
              className="input num mt-1"
              value={pct}
              onChange={(e) => setPct(e.target.value.replace(/[^\d.]/g, "").slice(0, 5))}
            />
          ) : (
            <div className="panel-inset num mt-1 px-3 py-2 text-sm text-paper-200">
              {marketTick === null ? "no book" : tickToPct(marketTick)}
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
          {tab === "BUY"
            ? type === "MARKET"
              ? "Estimated max cost (sweep)"
              : "Max cost"
            : type === "MARKET"
              ? "Estimated min proceeds (sweep)"
              : "Proceeds"}
        </span>
        <span className="num text-paper-100">{quote === null ? "—" : `${formatUsdc(quote.cost.toString())} USDC`}</span>
      </div>
      {partial && (
        <p className="mt-1 text-[11px] text-gold-300">
          Book depth covers only {formatUsdc(quote.filled.toString(), 0)} tokens;{" "}
          {type === "MARKET" ? "the remainder is cancelled (sweep-and-kill)." : "the remainder rests."}
        </p>
      )}

      {sellBlocked && <p className="mt-2 text-xs text-ink-400">No {outcome} to sell.</p>}
      {error && <p className="mt-2 text-xs text-no-400">{error}</p>}

      <button
        onClick={place}
        disabled={busy || market.status !== "LIVE" || sellBlocked}
        className={`btn mt-3 w-full ${
          outcome === "YES" ? "bg-yes-500/90 text-ink-950 hover:bg-yes-400" : "bg-no-500/90 text-ink-950 hover:bg-no-400"
        }`}
      >
        {busy ? "Placing…" : actionLabel}
      </button>
    </div>
  );
}
