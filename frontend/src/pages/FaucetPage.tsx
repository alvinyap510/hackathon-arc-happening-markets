import { useState } from "react";
import { useStore } from "../lib/store";
import { formatUsdc, parseUsdc, shortHash } from "../lib/format";

const MINT_AMOUNT = "10000000000"; // 10,000 USDC, 6-dec

export default function FaucetPage() {
  const { api, balances, wallet, refreshBalances } = useStore();
  const [busy, setBusy] = useState<string | null>(null);
  const [lastTx, setLastTx] = useState<string | null>(null);
  const [depAmt, setDepAmt] = useState("5000");
  const [wdrAmt, setWdrAmt] = useState("1000");
  const [error, setError] = useState<string | null>(null);

  const run = async (label: string, fn: () => Promise<{ hash: string }>) => {
    setBusy(label);
    setError(null);
    try {
      const tx = await fn();
      setLastTx(tx.hash);
      await refreshBalances();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Transaction failed");
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="mx-auto max-w-2xl animate-rise">
      <header className="mb-6">
        <h2 className="font-display text-3xl font-semibold text-paper-100">Faucet</h2>
        <p className="mt-1 text-sm text-ink-300">Mint demo USDC, deposit, trade.</p>
      </header>

      <div className="space-y-4">
        <div className="panel p-5">
          <div className="flex items-center justify-between">
            <div>
              <div className="label-caps">Wallet</div>
              <div className="num mt-1 text-2xl font-semibold text-paper-100">
                {wallet === null ? "—" : formatUsdc(wallet)} <span className="text-sm text-ink-400">USDC</span>
              </div>
            </div>
            <button onClick={() => void run("mint", () => api.faucet(MINT_AMOUNT))} disabled={busy !== null} className="btn-gold">
              {busy === "mint" ? "Minting…" : "Mint 10,000 USDC"}
            </button>
          </div>
        </div>

        <div className="grid gap-4 md:grid-cols-2">
          <div className="panel p-5">
            <div className="label-caps">Deposit</div>
            <div className="mt-2 flex gap-2">
              <input className="input num" value={depAmt} onChange={(e) => setDepAmt(e.target.value)} />
              <button
                onClick={() => {
                  const base = parseUsdc(depAmt);
                  if (base) void run("dep", () => api.deposit(base));
                  else setError("Enter a valid amount.");
                }}
                disabled={busy !== null}
                className="btn-ghost"
              >
                {busy === "dep" ? "…" : "Deposit"}
              </button>
            </div>
          </div>
          <div className="panel p-5">
            <div className="label-caps">Withdraw</div>
            <div className="mt-2 flex gap-2">
              <input className="input num" value={wdrAmt} onChange={(e) => setWdrAmt(e.target.value)} />
              <button
                onClick={() => {
                  const base = parseUsdc(wdrAmt);
                  if (base) void run("wdr", () => api.withdraw(base));
                  else setError("Enter a valid amount.");
                }}
                disabled={busy !== null}
                className="btn-ghost"
              >
                {busy === "wdr" ? "…" : "Withdraw"}
              </button>
            </div>
          </div>
        </div>

        <div className="panel p-5">
          <div className="label-caps">Venue balance</div>
          <div className="num mt-2 grid grid-cols-3 gap-3 text-center">
            <div>
              <div className="text-lg font-semibold text-paper-100">{balances ? formatUsdc(balances.available) : "0"}</div>
              <div className="text-[10px] uppercase tracking-wide text-ink-400">available</div>
            </div>
            <div>
              <div className="text-lg font-semibold text-paper-300">{balances ? formatUsdc(balances.reserved) : "0"}</div>
              <div className="text-[10px] uppercase tracking-wide text-ink-400">reserved</div>
            </div>
            <div>
              <div className="text-lg font-semibold text-paper-300">{balances ? formatUsdc(balances.chainFree) : "0"}</div>
              <div className="text-[10px] uppercase tracking-wide text-ink-400">on chain</div>
            </div>
          </div>
        </div>

        {lastTx && (
          <div className="panel-inset num flex items-center justify-between px-4 py-2 text-xs">
            <span className="text-ink-300">Last transaction</span>
            <a
              href={`https://testnet.arcscan.app/tx/${lastTx}`}
              target="_blank"
              rel="noreferrer"
              className="font-semibold text-steel-300 hover:text-steel-400"
            >
              {shortHash(lastTx)} ↗
            </a>
          </div>
        )}
        {error && <p className="text-xs text-no-400">{error}</p>}
      </div>
    </div>
  );
}
