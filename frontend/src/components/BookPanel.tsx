import type { Book, BookLevel } from "../lib/types";
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

function BookSide({ label, side }: { label: string; side: Book["yes"] }) {
  const mid =
    side.bids.length > 0 && side.asks.length > 0
      ? Math.round((side.bids[0].price + side.asks[0].price) / 2)
      : null;
  return (
    <div className="panel-inset p-3">
      <div className="mb-2 flex items-baseline justify-between">
        <span className="text-xs font-semibold text-paper-200">{label}</span>
        <span className="num text-[11px] text-ink-400">{mid === null ? "no book" : `mid ${tickToPct(mid)}`}</span>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Ladder title="Bids" levels={side.bids} tone="yes" />
        <Ladder title="Asks" levels={side.asks} tone="no" />
      </div>
    </div>
  );
}

export default function BookPanel({ book }: { book: Book | null }) {
  if (!book) return <div className="panel p-4 text-sm text-ink-400">Loading book…</div>;
  return (
    <div className="grid gap-3 md:grid-cols-2">
      <BookSide label="YES" side={book.yes} />
      <BookSide label="NO" side={book.no} />
    </div>
  );
}
