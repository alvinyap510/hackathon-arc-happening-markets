import { useEffect } from "react";

export interface ToastData {
  message: string;
  href?: string;
  linkLabel?: string;
}

/** Bottom-right self-dismissing toast (~8s) with an optional external link. */
export default function Toast({ toast, onDismiss }: { toast: ToastData | null; onDismiss: () => void }) {
  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(onDismiss, 8000);
    return () => clearTimeout(t);
  }, [toast, onDismiss]);
  if (!toast) return null;
  return (
    <div className="fixed bottom-5 right-5 z-50 animate-rise">
      <div className="panel flex items-center gap-3 border-yes-500/40 px-4 py-3 shadow-xl shadow-black/50">
        <span className="h-2 w-2 shrink-0 rounded-full bg-yes-400" />
        <span className="text-sm text-paper-100">{toast.message}</span>
        {toast.href && (
          <a
            href={toast.href}
            target="_blank"
            rel="noreferrer"
            className="num text-sm font-semibold text-gold-300 hover:text-gold-200"
          >
            {toast.linkLabel ?? "view tx"} ↗
          </a>
        )}
        <button onClick={onDismiss} className="ml-1 text-ink-400 hover:text-paper-200" aria-label="Dismiss">
          ×
        </button>
      </div>
    </div>
  );
}
