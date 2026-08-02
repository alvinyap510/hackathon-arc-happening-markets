import { useStore } from "../lib/store";
import MarketCard from "../components/MarketCard";
import TradingView from "./TradingView";

export default function MarketsPage({
  selected,
  onSelect,
}: {
  selected: string | null;
  onSelect: (id: string | null) => void;
}) {
  const { markets } = useStore();

  if (selected) {
    const m = markets.find((x) => x.marketId === selected);
    if (m) return <TradingView market={m} onBack={() => onSelect(null)} />;
  }

  const live = markets.filter((m) => m.status === "LIVE");
  const resolved = markets.filter((m) => m.status === "RESOLVED");

  return (
    <div className="mx-auto max-w-5xl animate-rise">
      <header className="mb-6">
        <h2 className="font-display text-3xl font-semibold text-paper-100">Markets</h2>
      </header>

      <div className="grid gap-4 md:grid-cols-2">
        {live.map((m) => (
          <MarketCard key={m.marketId} market={m} onOpen={() => onSelect(m.marketId)} />
        ))}
      </div>

      {resolved.length > 0 && (
        <>
          <h3 className="label-caps mb-3 mt-8">Resolved</h3>
          <div className="grid gap-4 md:grid-cols-2">
            {resolved.map((m) => (
              <MarketCard key={m.marketId} market={m} onOpen={() => onSelect(m.marketId)} />
            ))}
          </div>
        </>
      )}

      {markets.length === 0 && <p className="text-sm text-ink-400">Loading markets…</p>}
    </div>
  );
}
