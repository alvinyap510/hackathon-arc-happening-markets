// Lossless round-trip contract for the chip/Max size path (HCR1-1):
// parseUsdc(formatUsdcInput(x)) === x for every non-negative base-unit value.
import { describe, expect, it } from "vitest";
import { centsToTick, formatUsdcInput, parseUsdc, tickToCents } from "./format";

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

describe("centsToTick / tickToCents (max-price cents input)", () => {
  it("parses whole and decimal cents to ticks", () => {
    expect(centsToTick("62")).toBe(620);
    expect(centsToTick("62.0")).toBe(620);
    expect(centsToTick("62.5")).toBe(625);
    expect(centsToTick("62.")).toBe(620);
    expect(centsToTick("0.1")).toBe(1);
    expect(centsToTick("99.9")).toBe(999);
  });
  it("rejects invalid or out-of-range input", () => {
    expect(centsToTick("")).toBeNull();
    expect(centsToTick("0")).toBeNull();      // tick 0 = free option, disallowed
    expect(centsToTick("0.0")).toBeNull();
    expect(centsToTick("100")).toBeNull();    // three integer digits
    expect(centsToTick("62.55")).toBeNull();  // two decimals
    expect(centsToTick("abc")).toBeNull();
    expect(centsToTick(".5")).toBeNull();
  });
  it("round-trips display formatting", () => {
    expect(tickToCents(620)).toBe("62.0¢");
    expect(tickToCents(centsToTick("53.5")!)).toBe("53.5¢");
  });
});
