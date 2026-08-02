import type { RfmPhase } from "../lib/types";

const STEPS: { id: string; label: string; sub: string }[] = [
  { id: "OPEN", label: "Open", sub: "request posted" },
  { id: "COMMIT", label: "Commit", sub: "sealed quotes + bonds" },
  { id: "REVEAL", label: "Reveal", sub: "quotes open" },
  { id: "BORN", label: "Born", sub: "funded + priced" },
];

function stageIndex(phase: RfmPhase, born: boolean): number {
  if (born) return 4;
  switch (phase) {
    case "OPEN":
      return 0;
    case "COMMIT":
      return 1;
    case "REVEAL":
      return 2;
    case "FINALIZED":
      return 3;
    default:
      return -1; // FAILED / CANCELLED render outside the stepper
  }
}

export default function PhaseStepper({ phase, born }: { phase: RfmPhase; born: boolean }) {
  const idx = stageIndex(phase, born);
  return (
    <ol className="flex items-center">
      {STEPS.map((s, i) => {
        const done = i < idx;
        const active = i === idx;
        const last = i === STEPS.length - 1;
        return (
          <li key={s.id} className={`flex items-center ${last ? "" : "flex-1"}`}>
            <div className="flex flex-col items-center text-center">
              <div
                className={`flex h-7 w-7 items-center justify-center rounded-full border text-[11px] font-bold transition-all ${
                  active
                    ? last
                      ? "prism-edge prism-edge-animated text-gold-200"
                      : "border-gold-400 bg-gold-900/50 text-gold-300"
                    : done
                      ? "border-gold-600 bg-gold-900/30 text-gold-500"
                      : "border-ink-600 bg-ink-900 text-ink-400"
                }`}
              >
                {done ? "✓" : i + 1}
              </div>
              <div className={`mt-1.5 text-[11px] font-semibold ${active ? "text-gold-300" : done ? "text-paper-300" : "text-ink-400"}`}>
                {s.label}
              </div>
              <div className="text-[9px] text-ink-500">{s.sub}</div>
            </div>
            {!last && <div className={`mx-2 mb-6 h-px flex-1 ${i < idx ? "bg-gold-600" : "bg-ink-700"}`} />}
          </li>
        );
      })}
    </ol>
  );
}
