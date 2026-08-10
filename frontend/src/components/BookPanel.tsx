import type { Book, BookLevel } from "../lib/types";
import { foldBook } from "../lib/bookFold";
import { formatUsdc, tickToPct } from "../lib/format";

function Ladder({ title, levels, tone }: { title: string; levels: BookLevel[]; tone: "yes" | "no" }) {
  const max = levels.reduce((a, l) => (BigInt(l.size) > a ? BigInt(l.size) : a), 1n);
  return (
    <div>
      <div className="label-caps mb-1.5">{title}</div>
      <div className="space-y-0.5">
        {levels.length === 0 && <div className="py-1 text-[11px] text-ink-500">empty</div>}
        {levels.map((l) => (
          <div key={l.price} className="relative flex justify-between rounded px-2 py-1 text-xs">
            <div
              className={`absolute inset-y-0 right-0 rounded ${tone === "yes" ? "bg-yes-900/60" : "bg-no-900/60"}`}
              style={{ width: `${Number((BigInt(l.size) * 100n) / max)}%` }}
            />
            <span className={`num relative ${tone === "yes" ? "text-yes-300" : "text-no-300"}`}>{tickToPct(l.price)}</span>
            <span className="num relative text-paper-300">{formatUsdc(l.size, 0)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/** ONE consolidated YES-basis ladder — the venue keeps a single book per market;
 *  bids = BUY YES + SELL NO, asks = SELL YES + BUY NO (complemented once). */
export default function BookPanel({ book }: { book: Book | null }) {
  if (!book) return <div className="panel p-4 text-sm text-ink-400">Loading book…</div>;
  const fold = foldBook(book);
  const state =
    fold.mid !== null ? `mid ${tickToPct(fold.mid)}` : fold.bids.length + fold.asks.length > 0 ? "one-sided" : "no book";
  return (
    <div className="panel-inset p-3">
      <div className="mb-2 flex items-baseline justify-between">
        <span className="text-xs font-semibold text-paper-200">Order book</span>
        <span className="num text-[11px] text-ink-400">{state}</span>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Ladder title="Bids" levels={fold.bids} tone="yes" />
        <Ladder title="Asks" levels={fold.asks} tone="no" />
      </div>
    </div>
  );
}
