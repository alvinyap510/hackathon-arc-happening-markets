import { useState } from "react";
import type { Market } from "../lib/types";
import { formatDate, formatUsdc, tickToPct } from "../lib/format";
import { marketCategory, marketImageSrc } from "../lib/marketImage";

export function BornTag({ compact = false }: { compact?: boolean }) {
  return (
    <span className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap rounded-full border border-gold-500/50 bg-gold-900/40 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.12em] text-gold-300">
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
  const [imgStage, setImgStage] = useState<"png" | "svg" | "none">("png");
  const cat = marketCategory(market.questionText);
  return (
    <button
      onClick={onOpen}
      className={`panel group relative w-full overflow-hidden text-left transition-all hover:border-ink-500 ${
        hero ? "prism-edge prism-edge-animated animate-born-glow" : ""
      }`}
    >
      {/* image slot: /markets/<category>.svg, gradient + mono initial until the file exists */}
      <div className="relative aspect-[16/8] w-full bg-gradient-to-br from-ink-800 via-ink-900 to-ink-950">
        <span className="absolute inset-0 flex items-center justify-center font-display text-5xl font-semibold uppercase text-ink-700">
          {cat[0]}
        </span>
        {imgStage !== "none" && (
          <img
            src={marketImageSrc(market.questionText, imgStage)}
            alt=""
            loading="lazy"
            onError={() => setImgStage((s) => (s === "png" ? "svg" : "none"))}
            className="absolute inset-0 h-full w-full object-cover"
          />
        )}
        {market.bornFromRfm && (
          <div className="absolute right-2.5 top-2.5">
            <BornTag />
          </div>
        )}
      </div>

      <div className="p-5 pt-4">
        <h3 className="font-display text-lg font-semibold leading-snug text-paper-100 group-hover:text-gold-200">
          {market.questionText}
        </h3>

        <div className="mt-3 flex items-end gap-6">
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

        {yes !== null && (
          <div className="mt-3 h-1 overflow-hidden rounded-full bg-no-500/25">
            <div className="h-full rounded-full bg-yes-400/80" style={{ width: `${yes / 10}%` }} />
          </div>
        )}

        <div className="mt-3 flex items-center justify-between border-t border-ink-700/70 pt-3 text-[11px] text-ink-400">
          <span>
            {market.status === "RESOLVED" ? (
              <span className="font-semibold text-steel-300">Resolved {market.winningOutcome}</span>
            ) : (
              <>Closes {formatDate(market.closeTime)}</>
            )}
          </span>
          <span className="text-ink-500">{market.resolutionSource}</span>
        </div>
      </div>
    </button>
  );
}
