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
  const [ready, setReady] = useState(false);
  const ghost = useRef<HTMLDivElement>(null);
  const done = useRef(onDone);
  done.current = onDone;

  useEffect(() => {
    setReady(true);
    const t1 = setTimeout(() => {
      const src = from.current?.getBoundingClientRect();
      const nav = document.querySelector('[data-nav="markets"]')?.getBoundingClientRect();
      const g = ghost.current;
      if (!src || !nav || !g) return;
      g.style.transition = "transform 0.9s cubic-bezier(0.22, 1, 0.36, 1), opacity 0.9s ease";
      const dx = nav.left + nav.width / 2 - (src.left + 110);
      const dy = nav.top + nav.height / 2 - (src.top + 40);
      g.style.transform = `translate(${dx}px, ${dy}px) scale(0.12)`;
      g.style.opacity = "0";
    }, 700);
    const t2 = setTimeout(() => done.current(), 1800);
    return () => {
      clearTimeout(t1);
      clearTimeout(t2);
    };
    // one-shot animation: run exactly once on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (!ready) return null;
  const src = from.current?.getBoundingClientRect();
  if (!src) return null;
  return createPortal(
    <div
      ref={ghost}
      className="prism-edge prism-edge-animated animate-born-glow pointer-events-none fixed z-50 flex h-20 w-[220px] items-center justify-center rounded-xl"
      style={{ left: src.left + 24, top: src.top + 24 }}
    >
      <span className="px-3 text-center text-[11px] font-semibold text-gold-200">{label}</span>
    </div>,
    document.body,
  );
}
