import { useRef, useState } from "react";
import { useRfm } from "../lib/store";
import { useNow } from "../lib/useNow";
import { formatUsdc, shortAddr, tickToPct, timeRemaining } from "../lib/format";
import PhaseStepper from "./PhaseStepper";
import BornFlight from "./BornFlight";

function Countdown({ label, deadline, now }: { label: string; deadline: string; now: number }) {
  return (
    <div className="text-right">
      <div className="label-caps">{label}</div>
      <div className="num text-xl font-semibold text-gold-300">{timeRemaining(deadline, now)}</div>
    </div>
  );
}

export default function AuctionCard({
  requestId,
  onOpenMarket,
}: {
  requestId: string;
  onOpenMarket: (id: string) => void;
}) {
  const { request, reveals, final, bornMarketId } = useRfm(requestId);
  const now = useNow();
  const cardRef = useRef<HTMLDivElement>(null);
  const [flightDone, setFlightDone] = useState(false);

  if (!request) return <div className="panel p-6 text-sm text-ink-400">Loading auction…</div>;

  const born = bornMarketId !== null;
  const terminalBad = request.phase === "FAILED" || request.phase === "CANCELLED";

  return (
    <div
      ref={cardRef}
      className={`panel relative overflow-hidden p-6 transition-all ${
        born ? "prism-edge prism-edge-animated animate-born-glow" : ""
      }`}
    >
      {born && !flightDone && (
        <BornFlight from={cardRef} label="Market born · funded + priced" onDone={() => setFlightDone(true)} />
      )}

      <header className="flex flex-wrap items-start justify-between gap-4">
        <div className="max-w-xl">
          <div className="flex items-center gap-2">
            <span
              className={`rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-[0.12em] ${
                request.side === "YES" ? "bg-yes-500/15 text-yes-300" : "bg-no-500/15 text-no-300"
              }`}
            >
              institution buying {request.side}
            </span>
            <span className="text-[10px] uppercase tracking-[0.12em] text-ink-500">{request.requestId}</span>
          </div>
          <h3 className="mt-2 font-display text-xl font-semibold leading-snug text-paper-100">{request.questionText}</h3>
          <div className="num mt-2 flex flex-wrap gap-x-5 gap-y-1 text-xs text-ink-300">
            <span>
              qty <b className="text-paper-200">{formatUsdc(request.quantity, 0)}</b>
            </span>
            <span>
              min match <b className="text-paper-200">{formatUsdc(request.minMatch, 0)}</b>
            </span>
            <span>
              max price <b className="text-paper-200">{tickToPct(request.maxPriceTick)}</b>
            </span>
            <span>
              escrow <b className="text-gold-300">{formatUsdc(request.escrow, 0)}</b>
            </span>
            <span>
              bond <b className="text-gold-300">{formatUsdc(request.bond, 0)}</b>
            </span>
          </div>
        </div>
        {request.phase === "COMMIT" && <Countdown label="Commit closes in" deadline={request.commitDeadline} now={now} />}
        {request.phase === "REVEAL" && <Countdown label="Reveal closes in" deadline={request.revealDeadline} now={now} />}
      </header>

      <div className="mt-5 border-t border-ink-700/70 pt-5">
        <PhaseStepper phase={request.phase} born={born} />
      </div>

      {/* COMMIT: sealed quotes arrive; counts only, never hashes */}
      {request.phase === "COMMIT" && (
        <div className="mt-6 animate-rise">
          <div className="flex items-center gap-3">
            <div className="flex gap-1.5">
              {[...Array(Math.max(request.commitCount, 3))].map((_, i) => (
                <div
                  key={i}
                  className={`flex h-11 w-9 items-center justify-center rounded-md border text-sm transition-all ${
                    i < request.commitCount
                      ? "border-gold-500/60 bg-gold-900/40 text-gold-300 animate-rise"
                      : "border-dashed border-ink-600 text-ink-600"
                  }`}
                >
                  {i < request.commitCount ? "◼" : "·"}
                </div>
              ))}
            </div>
            <div>
              <div className="text-sm font-semibold text-paper-100">
                {request.commitCount} sealed quote{request.commitCount === 1 ? "" : "s"} committed
              </div>
              <div className="num text-xs text-gold-300">{request.commitCount * 500} USDC in bonds escrowed</div>
              <div className="text-[11px] text-ink-500">Quotes are hash commitments. Nobody sees a price until reveal.</div>
            </div>
          </div>
        </div>
      )}

      {/* REVEAL: quotes open one by one */}
      {request.phase === "REVEAL" && (
        <div className="mt-6 space-y-1.5 animate-rise">
          {reveals.map((r, i) => (
            <div
              key={i}
              className={`flex items-center justify-between rounded-lg border px-3 py-2 text-xs animate-rise ${
                r.valid ? "border-ink-600 bg-ink-900" : "border-no-500/50 bg-no-900/30"
              }`}
            >
              <span className="num text-ink-300">{shortAddr(r.mm)}</span>
              <span className="num font-semibold text-paper-100">{tickToPct(r.priceTick)}</span>
              <span className="num text-paper-300">{formatUsdc(r.size, 0)}</span>
              {r.valid ? (
                <span className="text-[10px] font-semibold uppercase tracking-wide text-yes-400">in range</span>
              ) : (
                <span className="text-[10px] font-semibold uppercase tracking-wide text-no-400">out of range · bond at risk</span>
              )}
            </div>
          ))}
          <p className="pt-1 text-[11px] text-ink-500">
            {request.commitCount - reveals.length} commitment{request.commitCount - reveals.length === 1 ? "" : "s"} still sealed.
            Unrevealed bonds slash at the deadline.
          </p>
        </div>
      )}

      {/* FINALIZED: pay-as-bid result. Retained through the born state so the
          slash line stays readable while the market opens (audit polish). */}
      {final && (
        <div className="mt-6 animate-rise">
          <div className="grid grid-cols-3 gap-3 text-center">
            <div className="panel-inset p-3">
              <div className="label-caps">Filled</div>
              <div className="num mt-1 text-xl font-semibold text-paper-100">{formatUsdc(final.filledQty, 0)}</div>
            </div>
            <div className="panel-inset p-3">
              <div className="label-caps">Marginal</div>
              <div className="num mt-1 text-xl font-semibold text-gold-300">{tickToPct(final.marginalTick)}</div>
            </div>
            <div className="panel-inset p-3">
              <div className="label-caps">VWAP</div>
              <div className="num mt-1 text-xl font-semibold text-gold-300">{tickToPct(final.vwapTick)}</div>
            </div>
          </div>
          <div className="mt-3 rounded-lg border border-no-500/40 bg-no-900/30 px-3 py-2 text-xs">
            <span className="font-semibold text-no-300">{final.slashCount} bond{final.slashCount === 1 ? "" : "s"} forfeited</span>
            <span className="text-ink-300">
              {" · "}
              {final.slashed.map((s) => shortAddr(s.mm)).join(", ")} · {formatUsdc((BigInt(final.slashCount) * 500_000_000n).toString(), 0)} USDC → institution
            </span>
          </div>
          {!born && (
            <p className="mt-3 text-center text-xs text-ink-400">Pay-as-bid: each maker fills at its own quote. Locking collateral…</p>
          )}
        </div>
      )}

      {terminalBad && (
        <div className="mt-6 rounded-lg border border-ink-600 bg-ink-900 px-4 py-3 text-sm text-ink-300 animate-rise">
          {request.phase === "FAILED"
            ? "Auction failed: revealed quotes did not clear the minimum match. Escrow and bonds returned."
            : "Request cancelled before any commitment. Escrow and bond returned."}
        </div>
      )}

      {/* BORN: the hero state */}
      {born && final && (
        <div className="mt-6 text-center">
          <div className="animate-rise">
            <div className="text-[11px] font-bold uppercase tracking-[0.22em] text-gold-300">Market born</div>
            <h4 className="font-display mt-2 text-3xl font-semibold text-paper-100">
              Funded at <span className="prism-text">{tickToPct(final.vwapTick)}</span>
            </h4>
            <p className="num mt-2 text-xs text-ink-300">collateral locked on chain · the book opens at the auction marks</p>
          </div>
          {flightDone && (
            <div className="mt-5 animate-rise">
              <p className="text-xs text-ink-300">
                The book is open and quoting live. The institution holds its hedge; the crowd provides exit liquidity.
              </p>
              <button onClick={() => onOpenMarket(bornMarketId)} className="btn-gold mt-3">
                Trade this market
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
