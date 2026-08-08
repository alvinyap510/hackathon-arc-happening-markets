// Market card imagery, per ASSET_CONTRACT_MARKET_IMAGES.md: assets live at
// /markets/<category>.svg (pre-generated); the category is inferred from
// question keywords; "generic" is the catch-all. The card hides the <img> on
// error, so a missing file degrades to the gradient placeholder.

export type MarketCategory = "crypto" | "macro" | "sports" | "politics" | "generic";

const RULES: [RegExp, MarketCategory][] = [
  [/bitcoin|btc|ethereum|eth\b|crypto|solana|defi/i, "crypto"],
  [/fed|rate|cpi|fomc|ecb|inflation|treasury|payroll|jobs report/i, "macro"],
  [/world cup|fifa|nba|match|super bowl|olympic|league/i, "sports"],
  [/election|president|senate|parliament|prime minister/i, "politics"],
];

export function marketCategory(questionText: string): MarketCategory {
  for (const [re, cat] of RULES) if (re.test(questionText)) return cat;
  return "generic";
}

export function marketImageSrc(questionText: string, ext: "png" | "svg" = "png"): string {
  // raster preferred (richer); svg is the vector fallback; the card degrades
  // to its gradient placeholder if both are absent
  return `/markets/${marketCategory(questionText)}.${ext}`;
}
