import { useState, type FormEvent } from "react";
import { useStore } from "../lib/store";
import type { NewRfmRequest, OutcomeSide, RfmRequest } from "../lib/types";
import { formatUsdc, parseUsdc } from "../lib/format";
import TxLink from "./TxLink";

const BOND = "500000000"; // 500 USDC, symmetric with the MM bond

export default function RfmForm({ onPosted }: { onPosted: (r: RfmRequest) => void }) {
  const { api, balances } = useStore();
  const [q, setQ] = useState("");
  const [src, setSrc] = useState("");
  const [close, setClose] = useState("2026-12-31");
  const [side, setSide] = useState<OutcomeSide>("YES");
  const [qty, setQty] = useState("1000");
  const [minMatch, setMinMatch] = useState("500");
  const [maxPrice, setMaxPrice] = useState("620");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [postedTx, setPostedTx] = useState<string | null>(null);

  const qtyBase = parseUsdc(qty);
  const tick = Number(maxPrice);
  const escrow = qtyBase !== null && tick > 0 ? (BigInt(qtyBase) * BigInt(tick)) / 1000n : null;
  const total = escrow !== null ? escrow + BigInt(BOND) : null;
  const enough = total !== null && balances !== null && total <= BigInt(balances.available);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    if (q.trim().length < 10) return setError("Give the market a clear question.");
    if (!src.trim()) return setError("Name the resolution source.");
    if (qtyBase === null || BigInt(qtyBase) <= 0n) return setError("Enter a quantity.");
    const minBase = parseUsdc(minMatch);
    if (minBase === null || BigInt(minBase) <= 0n || BigInt(minBase) > BigInt(qtyBase))
      return setError("Min match must be between 1 and the quantity.");
    if (!(tick > 0 && tick < 1000)) return setError("Max price must be a tick between 1 and 999.");
    setBusy(true);
    try {
      const req: NewRfmRequest = {
        questionText: q.trim(),
        resolutionSource: src.trim(),
        closeTime: new Date(close).toISOString(),
        side,
        quantity: qtyBase,
        minMatch: minBase,
        maxPriceTick: tick,
      };
      const posted = await api.postRfmRequest(req);
      setPostedTx(posted.txHash ?? null);
      onPosted(posted);
      setQ("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Request rejected");
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} className="panel p-5">
      <h3 className="font-display text-lg font-semibold text-paper-100">Request a market</h3>
      <p className="mt-1 text-xs text-ink-400">
        Post a hedge. Market makers answer with sealed, bonded quotes. Commit 2 min · reveal 1 min (demo preset).
      </p>

      <label className="label-caps mt-4 block">Event question</label>
      <input className="input mt-1" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Will the ECB cut rates before March 2027?" />

      <div className="mt-3 grid grid-cols-2 gap-2">
        <div>
          <label className="label-caps">Resolution source</label>
          <input className="input mt-1" value={src} onChange={(e) => setSrc(e.target.value)} placeholder="ECB statement" />
        </div>
        <div>
          <label className="label-caps">Close date</label>
          <input type="date" className="input mt-1" value={close} onChange={(e) => setClose(e.target.value)} />
        </div>
      </div>

      <div className="mt-3">
        <label className="label-caps">Hedge side (the outcome you buy)</label>
        <div className="mt-1 grid grid-cols-2 gap-1 rounded-lg bg-ink-900 p-1">
          {(["YES", "NO"] as OutcomeSide[]).map((s) => (
            <button
              type="button"
              key={s}
              onClick={() => setSide(s)}
              className={`rounded-md px-2 py-1.5 text-xs font-semibold ${
                side === s
                  ? s === "YES"
                    ? "bg-yes-500/20 text-yes-300"
                    : "bg-no-500/20 text-no-300"
                  : "text-ink-400 hover:text-paper-200"
              }`}
            >
              Buy {s}
            </button>
          ))}
        </div>
      </div>

      <div className="mt-3 grid grid-cols-3 gap-2">
        <div>
          <label className="label-caps">Quantity</label>
          <input className="input num mt-1" value={qty} onChange={(e) => setQty(e.target.value)} />
        </div>
        <div>
          <label className="label-caps">Min match</label>
          <input className="input num mt-1" value={minMatch} onChange={(e) => setMinMatch(e.target.value)} />
        </div>
        <div>
          <label className="label-caps">Max price (tick)</label>
          <input className="input num mt-1" value={maxPrice} onChange={(e) => setMaxPrice(e.target.value.replace(/\D/g, "").slice(0, 3))} />
        </div>
      </div>

      <div className="mt-4 space-y-1 rounded-lg border border-gold-600/40 bg-gold-900/20 p-3 text-xs">
        <div className="flex justify-between text-paper-300">
          <span>Escrow locked at post</span>
          <span className="num text-gold-300">{escrow === null ? "—" : `${formatUsdc(escrow.toString())} USDC`}</span>
        </div>
        <div className="flex justify-between text-paper-300">
          <span>Your bond (slashes if you grief)</span>
          <span className="num text-gold-300">500 USDC</span>
        </div>
        <div className="flex justify-between border-t border-gold-600/30 pt-1 font-semibold text-paper-100">
          <span>Total committed</span>
          <span className="num">{total === null ? "—" : `${formatUsdc(total.toString())} USDC`}</span>
        </div>
        <div className="flex justify-between text-[11px] text-ink-400">
          <span>Available venue balance</span>
          <span className="num">{balances ? formatUsdc(balances.available) : "0"} USDC</span>
        </div>
      </div>

      {!enough && total !== null && (
        <p className="mt-2 text-[11px] text-no-400">Balance too low. Fund via the Faucet tab, then deposit.</p>
      )}
      {error && <p className="mt-2 text-xs text-no-400">{error}</p>}

      <button type="submit" disabled={busy || !enough} className="btn-gold mt-4 w-full">
        {busy ? "Posting on chain…" : "Post request · lock escrow + bond"}
      </button>

      {postedTx && (
        <div className="mt-2 flex items-center justify-center gap-1.5 text-[11px] text-ink-300">
          posted on chain <TxLink hash={postedTx} />
        </div>
      )}
    </form>
  );
}
