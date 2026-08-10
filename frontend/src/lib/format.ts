// Formatting helpers. All money is 6-dec base units passed as strings; ticks are 0-1000.

const USDC_DECIMALS = 6;

/** 6-dec base units -> human USDC string, e.g. "12500000" -> "12.50". */
export function formatUsdc(base: string, maxFrac = 2): string {
  const neg = base.startsWith("-");
  const raw = neg ? base.slice(1) : base;
  const padded = raw.padStart(USDC_DECIMALS + 1, "0");
  const whole = padded.slice(0, -USDC_DECIMALS) || "0";
  const frac = padded.slice(-USDC_DECIMALS).replace(/0+$/, "").slice(0, maxFrac);
  const grouped = Number(whole).toLocaleString("en-US");
  return `${neg ? "-" : ""}${grouped}${frac ? `.${frac.padEnd(Math.min(frac.length, maxFrac), "0")}` : ""}`;
}

/** 6-dec base units -> an INPUT-safe human string: ungrouped, up to 6 decimals,
 *  lossless round trip — parseUsdc(formatUsdcInput(x)) === x for every x >= 0.
 *  (formatUsdc is display-only: it groups thousands and truncates decimals.) */
export function formatUsdcInput(base: string): string {
  const padded = base.padStart(USDC_DECIMALS + 1, "0");
  const whole = padded.slice(0, -USDC_DECIMALS) || "0";
  const frac = padded.slice(-USDC_DECIMALS).replace(/0+$/, "");
  return `${BigInt(whole).toString()}${frac ? `.${frac}` : ""}`;
}

/** Human USDC -> 6-dec base units string. Returns null on invalid input. */
export function parseUsdc(input: string): string | null {
  const m = input.trim().match(/^(\d+)(?:\.(\d{1,6}))?$/);
  if (!m) return null;
  const frac = (m[2] ?? "").padEnd(USDC_DECIMALS, "0");
  const base = (BigInt(m[1]) * 10n ** 6n + BigInt(frac || "0")).toString();
  return base;
}

/** Tick 0-1000 -> probability-style percent, e.g. 530 -> "53.0%". */
export function tickToPct(tick: number): string {
  return `${(tick / 10).toFixed(1)}%`;
}

/** Tick -> dollar price of one outcome token, e.g. 530 -> "$0.53". */
export function tickToPrice(tick: number): string {
  return `$${(tick / 1000).toFixed(2)}`;
}

/** The complement tick for the opposite side of a binary book. */
export function complementTick(tick: number): number {
  return 1000 - tick;
}

export function shortAddr(addr: string): string {
  if (addr.length <= 12) return addr;
  return `${addr.slice(0, 6)}…${addr.slice(-4)}`;
}

export function shortHash(hash: string): string {
  if (hash.length <= 14) return hash;
  return `${hash.slice(0, 10)}…${hash.slice(-4)}`;
}

export function timeRemaining(deadlineIso: string, nowMs: number): string {
  const ms = new Date(deadlineIso).getTime() - nowMs;
  if (ms <= 0) return "0s";
  const s = Math.floor(ms / 1000);
  const m = Math.floor(s / 60);
  const r = s % 60;
  return m > 0 ? `${m}m ${r}s` : `${r}s`;
}

export function formatDate(value: string): string {
  // Accept both ISO strings (mock) and unix-seconds strings (real backend closeTime).
  const d = /^\d+$/.test(value) ? new Date(Number(value) * 1000) : new Date(value);
  if (Number.isNaN(d.getTime())) return "TBD";
  return d.toLocaleDateString("en-US", {
    month: "short",
    day: "numeric",
    year: "numeric",
  });
}

export const ARC_EXPLORER = "https://testnet.arcscan.app";

export function txUrl(hash: string): string {
  return `${ARC_EXPLORER}/tx/${hash}`;
}

export function addressUrl(addr: string): string {
  return `${ARC_EXPLORER}/address/${addr}`;
}
