import { formatUsdcInput } from "../lib/format";

const CHIPS = [25, 50, 75] as const;

/** The chip action model, exported for direct unit-testing of the button contract:
 *  each entry is (label, base-unit value, the exact string written to the size input). */
export function chipActions(available: bigint): { label: string; value: bigint; write: string }[] {
  const chip = (p: number) => (available * BigInt(p)) / 100n;
  return [
    ...CHIPS.map((p) => ({ label: `${p}%`, value: chip(p), write: formatUsdcInput(chip(p).toString()) })),
    { label: "Max", value: available, write: formatUsdcInput(available.toString()) },
  ];
}

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
  const actions = chipActions(available);
  return (
    <div className="mt-3 flex items-center justify-between text-xs">
      <span className="text-ink-400">
        Shares <span className="num text-paper-200">{formatUsdcInput(available.toString())}</span>
        {reserved > 0n && <span className="num text-ink-500"> ({formatUsdcInput(reserved.toString())} reserved)</span>}
      </span>
      <span className="flex gap-1">
        {actions.map((a) => (
          <button
            key={a.label}
            onClick={() => onSize(a.write)}
            disabled={a.value <= 0n}
            className="rounded bg-ink-900 px-1.5 py-0.5 text-[10px] font-semibold text-ink-300 hover:text-paper-200 disabled:cursor-not-allowed disabled:opacity-40"
          >
            {a.label}
          </button>
        ))}
      </span>
    </div>
  );
}
