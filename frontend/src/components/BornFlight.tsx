// The hero gesture: when a market is born, a ghost card tears off the auction
// card and docks into the Markets nav item. Restrained, one-shot, no confetti.

import { useEffect, useRef, useState, type RefObject } from "react";
import { createPortal } from "react-dom";

export default function BornFlight({
  from,
  label,
  onDone,
}: {
  from: RefObject<HTMLDivElement | null>;
  label: string;
  onDone: () => void;
}) {
  const [style, setStyle] = useState<{ from: DOMRect; to: DOMRect } | null>(null);
  const ghost = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const src = from.current?.getBoundingClientRect();
    const nav = document.querySelector('[data-nav="markets"]')?.getBoundingClientRect();
    if (!src || !nav) {
      onDone();
      return;
    }
    setStyle({ from: src, to: nav });
    const t1 = setTimeout(() => {
      const g = ghost.current;
      if (!g) return;
      g.style.transition = "transform 0.9s cubic-bezier(0.22, 1, 0.36, 1), opacity 0.9s ease";
      const dx = nav.left + nav.width / 2 - (src.left + 110);
      const dy = nav.top + nav.height / 2 - (src.top + 40);
      g.style.transform = `translate(${dx}px, ${dy}px) scale(0.12)`;
      g.style.opacity = "0";
    }, 700);
    const t2 = setTimeout(onDone, 1800);
    return () => {
      clearTimeout(t1);
      clearTimeout(t2);
    };
  }, [from, onDone]);

  if (!style) return null;
  return createPortal(
    <div
      ref={ghost}
      className="prism-edge prism-edge-animated animate-born-glow pointer-events-none fixed z-50 flex h-20 w-[220px] items-center justify-center rounded-xl"
      style={{ left: style.from.left + 24, top: style.from.top + 24 }}
    >
      <span className="px-3 text-center text-[11px] font-semibold text-gold-200">{label}</span>
    </div>,
    document.body,
  );
}
