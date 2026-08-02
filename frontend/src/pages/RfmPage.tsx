import { useCallback, useEffect, useState } from "react";
import { useStore } from "../lib/store";
import type { RfmRequest } from "../lib/types";
import { formatUsdc } from "../lib/format";
import RfmForm from "../components/RfmForm";
import AuctionCard from "../components/AuctionCard";

const PHASE_TONE: Record<RfmRequest["phase"], string> = {
  OPEN: "text-steel-300",
  COMMIT: "text-gold-300",
  REVEAL: "text-gold-300",
  FINALIZED: "text-yes-300",
  FAILED: "text-no-400",
  CANCELLED: "text-ink-400",
};

export default function RfmPage({ onOpenMarket }: { onOpenMarket: (id: string) => void }) {
  const { api } = useStore();
  const [requests, setRequests] = useState<RfmRequest[]>([]);
  const [selected, setSelected] = useState<string | null>(null);

  const reload = useCallback(async () => {
    const list = await api.listRfmRequests();
    setRequests(list);
    setSelected((s) => s ?? list[0]?.requestId ?? null);
  }, [api]);

  useEffect(() => void reload(), [reload]);

  const onPosted = (r: RfmRequest) => {
    setRequests((rs) => [r, ...rs]);
    setSelected(r.requestId);
  };

  return (
    <div className="mx-auto max-w-6xl animate-rise">
      <header className="mb-6">
        <h2 className="font-display text-3xl font-semibold text-paper-100">Request a Market</h2>
        <p className="mt-1 max-w-2xl text-sm text-ink-300">
          Institutions post hedges; market makers answer with sealed, bonded quotes. The winning quotes lock
          collateral and the market is born funded and priced. This auction runs on chain.
        </p>
      </header>

      <div className="grid gap-6 lg:grid-cols-[340px_1fr]">
        <div className="space-y-4">
          <RfmForm onPosted={onPosted} />

          {requests.length > 0 && (
            <div className="panel p-4">
              <div className="label-caps mb-2">Requests</div>
              <div className="space-y-1.5">
                {requests.map((r) => (
                  <button
                    key={r.requestId}
                    onClick={() => setSelected(r.requestId)}
                    className={`w-full rounded-lg border px-3 py-2 text-left text-xs transition-colors ${
                      selected === r.requestId ? "border-gold-500/60 bg-gold-900/20" : "border-ink-700 bg-ink-900 hover:border-ink-500"
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <span className="font-semibold text-paper-200">{r.questionText.slice(0, 42)}{r.questionText.length > 42 ? "…" : ""}</span>
                      <span className={`num text-[10px] font-bold uppercase ${PHASE_TONE[r.phase]}`}>
                        {r.bornMarketId ? "BORN" : r.phase}
                      </span>
                    </div>
                    <div className="num mt-1 text-[10px] text-ink-400">
                      buy {r.side} · {formatUsdc(r.quantity, 0)} @ ≤ {r.maxPriceTick / 10}%
                    </div>
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>

        <div>
          {selected ? (
            <AuctionCard key={selected} requestId={selected} onOpenMarket={onOpenMarket} />
          ) : (
            <div className="panel p-8 text-center text-sm text-ink-400">
              Post a request to start a sealed auction.
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
