import { useEffect, useRef, useState, type ReactNode } from "react";
import { useStore } from "../lib/store";
import { formatUsdc, shortAddr } from "../lib/format";

export type Tab = "markets" | "rfm" | "faucet";

const TABS: { id: Tab; label: string; hint: string }[] = [
  { id: "markets", label: "Markets", hint: "Trade the live books" },
  { id: "rfm", label: "Request a Market", hint: "Originate via sealed auction" },
  { id: "faucet", label: "Faucet", hint: "Fund your wallet" },
];

export default function Layout({
  tab,
  onTab,
  children,
}: {
  tab: Tab;
  onTab: (t: Tab) => void;
  children: ReactNode;
}) {
  const { session, balances, bornPulse, wallet } = useStore();
  const [ping, setPing] = useState(false);
  const first = useRef(true);

  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    setPing(true);
    const t = setTimeout(() => setPing(false), 3600);
    return () => clearTimeout(t);
  }, [bornPulse]);

  return (
    <div className="flex min-h-screen">
      <aside className="flex w-60 shrink-0 flex-col border-r border-ink-700 bg-ink-950/60 px-5 py-6">
        <div className="flex items-center gap-2.5 px-1">
          <img src="/brand/happening-mark-square-padded-transparent.png" alt="" className="h-8 w-8" />
          <div>
            <div className="font-display text-lg font-semibold leading-none text-paper-100">Happening</div>
            <div className="mt-1 text-[10px] font-semibold uppercase tracking-[0.18em] text-gold-400">
              Request for Market
            </div>
          </div>
        </div>

        <nav className="mt-10 flex flex-col gap-1">
          {TABS.map((t) => (
            <button
              key={t.id}
              data-nav={t.id}
              onClick={() => onTab(t.id)}
              className={`group relative rounded-lg px-3 py-2.5 text-left transition-colors ${
                tab === t.id ? "bg-ink-800 text-paper-100" : "text-ink-300 hover:bg-ink-850 hover:text-paper-200"
              }`}
            >
              <span className="flex items-center gap-2 text-sm font-semibold">
                {t.label}
                {t.id === "markets" && ping && (
                  <span className="relative flex h-2 w-2">
                    <span className="absolute h-2 w-2 rounded-full bg-gold-400 animate-badge-ping" />
                    <span className="h-2 w-2 rounded-full bg-gold-400" />
                  </span>
                )}
              </span>
              <span className="mt-0.5 block text-[11px] text-ink-400 group-hover:text-ink-300">{t.hint}</span>
              {tab === t.id && <span className="absolute inset-y-2 left-0 w-0.5 rounded bg-gold-400" />}
            </button>
          ))}
        </nav>

        <div className="mt-auto space-y-3">
          <div className="panel-inset p-3">
            <div className="label-caps">Wallet</div>
            <div className="num mt-1 text-xs text-paper-200">{session ? shortAddr(session.address) : "—"}</div>
            <div className="mt-2 space-y-1 text-[11px]">
              <div className="flex justify-between text-ink-300">
                <span>Venue available</span>
                <span className="num text-paper-200">{balances ? formatUsdc(balances.available) : "0"}</span>
              </div>
              <div className="flex justify-between text-ink-300">
                <span>Reserved</span>
                <span className="num text-paper-300">{balances ? formatUsdc(balances.reserved) : "0"}</span>
              </div>
              {wallet !== null && (
                <div className="flex justify-between text-ink-300">
                  <span>Wallet USDC</span>
                  <span className="num text-paper-300">{formatUsdc(wallet)}</span>
                </div>
              )}
            </div>
          </div>
          <p className="px-1 text-[10px] leading-relaxed text-ink-500">
            Deposits, withdrawals, RFM escrow and redemptions are on chain from your wallet. Trades settle
            via the operator batch.
          </p>
        </div>
      </aside>

      <main className="min-w-0 flex-1 px-8 py-6">{children}</main>
    </div>
  );
}
