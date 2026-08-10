import { formatUsdc, formatUsdcInput } from "../lib/format";

const CHIPS = [25, 50, 75] as const;

/** Reserved-aware shares row for the Sell tab: 25/50/75/Max chips computed in
 *  base-unit BigInt and written to the size input via the LOSSLESS input formatter
 *  (parseUsdc round-trips every base unit — display formatUsdc would truncate).
 *  Zero-result chips are disabled (dust); Max is the exact available amount. */
export default function SellShares({
  available,
  reserved,
  onSize,
}: {
  available: bigint;
  reserved: bigint;
  onSize: (v: string) => void;
}) {
  const chip = (p: number) => (available * BigInt(p)) / 100n;
  return (
    <div className="mt-3 flex items-center justify-between text-xs">
      <span className="text-ink-400">
        Shares <span className="num text-paper-200">{formatUsdc(available.toString())}</span>
        {reserved > 0n && <span className="num text-ink-500"> ({formatUsdc(reserved.toString())} reserved)</span>}
      </span>
      <span className="flex gap-1">
        {CHIPS.map((p) => (
          <button
            key={p}
            onClick={() => onSize(formatUsdcInput(chip(p).toString()))}
            disabled={chip(p) <= 0n}
            className="rounded bg-ink-900 px-1.5 py-0.5 text-[10px] font-semibold text-ink-300 hover:text-paper-200 disabled:cursor-not-allowed disabled:opacity-40"
          >
            {p}%
          </button>
        ))}
        <button
          onClick={() => onSize(formatUsdcInput(available.toString()))}
          disabled={available <= 0n}
          className="rounded bg-ink-900 px-1.5 py-0.5 text-[10px] font-semibold text-ink-300 hover:text-paper-200 disabled:cursor-not-allowed disabled:opacity-40"
        >
          Max
        </button>
      </span>
    </div>
  );
}
