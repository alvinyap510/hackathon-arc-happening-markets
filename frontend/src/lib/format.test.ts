// Lossless round-trip contract for the chip/Max size path (HCR1-1):
// parseUsdc(formatUsdcInput(x)) === x for every non-negative base-unit value.
import { describe, expect, it } from "vitest";
import { formatUsdcInput, parseUsdc } from "./format";

describe("formatUsdcInput round trip", () => {
  const cases = [
    "1", // 1 base unit (dust Max)
    "1234567", // non-cent-aligned partial chip
    "1000000000", // 1,000 tokens — display formatUsdc would group and fail parseUsdc
    "0",
    "999999", // sub-1 token
    "123456789012", // large
  ];
  for (const base of cases) {
    it(`round-trips ${base}`, () => {
      expect(parseUsdc(formatUsdcInput(base))).toBe(base);
    });
  }

  it("chip math stays in base units: 25% of 1,234,567 = 308,641 round-trips", () => {
    const chip = ((BigInt("1234567") * 25n) / 100n).toString();
    expect(chip).toBe("308641");
    expect(parseUsdc(formatUsdcInput(chip))).toBe(chip);
  });
});
