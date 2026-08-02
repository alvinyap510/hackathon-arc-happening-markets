import type { Market } from "../lib/types";
import { formatDate, formatUsdc, tickToPct } from "../lib/format";

export function BornTag({ compact = false }: { compact?: boolean }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-gold-500/50 bg-gold-900/40 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.12em] text-gold-300">
      <span className="h-1 w-1 rounded-full bg-gold-300" />
      {compact ? "RFM" : "born from RFM"}
    </span>
  );
}

export default function MarketCard({
  market,
  hero = false,
  onOpen,
}: {
  market: Market;
  hero?: boolean;
  onOpen?: () => void;
}) {
  const yes = market.midTick;
  return (
    <button
      onClick={onOpen}
      className={`panel group relative w-full p-5 text-left transition-all hover:border-ink-500 ${
        hero ? "prism-edge prism-edge-animated animate-born-glow" : ""
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <h3 className="font-display text-lg font-semibold leading-snug text-paper-100 group-hover:text-gold-200">
          {market.questionText}
        </h3>
        {market.bornFromRfm && <BornTag />}
      </div>

      <div className="mt-4 flex items-end gap-6">
        <div>
          <div className="label-caps">Yes</div>
          <div className="num mt-0.5 text-2xl font-semibold text-yes-400">{yes === null ? "—" : tickToPct(yes)}</div>
        </div>
        <div>
          <div className="label-caps">No</div>
          <div className="num mt-0.5 text-2xl font-semibold text-no-400">
            {yes === null ? "—" : tickToPct(1000 - yes)}
          </div>
        </div>
        {market.birth && (
          <div className="ml-auto text-right">
            <div className="label-caps">Birth marks</div>
            <div className="num mt-0.5 text-xs text-gold-300">
              marginal {tickToPct(market.birth.marginalTick)} · vwap {tickToPct(market.birth.vwapTick)}
            </div>
            <div className="num text-[10px] text-ink-400">filled {formatUsdc(market.birth.filledQty)}</div>
          </div>
        )}
      </div>

      <div className="mt-4 flex items-center justify-between border-t border-ink-700/70 pt-3 text-[11px] text-ink-400">
        <span>
          {market.status === "RESOLVED" ? (
            <span className="font-semibold text-steel-300">Resolved {market.winningOutcome}</span>
          ) : (
            <>Closes {formatDate(market.closeTime)}</>
          )}
        </span>
        <span className="text-ink-500">{market.resolutionSource}</span>
      </div>
    </button>
  );
}
