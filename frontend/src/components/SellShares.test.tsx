// SellShares UI-contract tests (plan v5 §Verification 6-7). The buttons render from
// chipActions() — the component's OWN action model — so asserting on it tests the real
// enable/disable predicate (value <= 0n) and the exact string each button writes.
// The rendered markup is additionally checked so the model and DOM cannot diverge.
import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import SellShares, { chipActions } from "./SellShares";
import { parseUsdc } from "../lib/format";

const render = (available: bigint, reserved: bigint) =>
  renderToStaticMarkup(<SellShares available={available} reserved={reserved} onSize={() => {}} />);

describe("SellShares contract", () => {
  it("dust position (1 base unit): 25/50/75 disabled, Max ENABLED and writes exactly 1 unit", () => {
    const a = chipActions(1n);
    expect(a.map((x) => x.label)).toEqual(["25%", "50%", "75%", "Max"]);
    expect(a.slice(0, 3).every((x) => x.value <= 0n)).toBe(true); // disabled predicate
    const max = a[3];
    expect(max.value).toBe(1n); // enabled
    expect(max.write).toBe("0.000001");
    expect(parseUsdc(max.write)).toBe("1"); // Max == exact available base units
    // DOM agrees: exactly the three percent chips carry disabled
    expect((render(1n, 0n).match(/disabled=""/g) ?? []).length).toBe(3);
  });

  it("fully-reserved holding (amount==reserved): every action disabled", () => {
    const a = chipActions(0n);
    expect(a.every((x) => x.value <= 0n)).toBe(true);
    const html = render(0n, 100000000n);
    expect((html.match(/disabled=""/g) ?? []).length).toBe(4); // 25/50/75 + Max
    expect(html).toContain("(100 reserved)");
  });

  it("percent chips floor in base units and round-trip losslessly", () => {
    const a = chipActions(1234567n);
    expect(a[0].value).toBe(308641n); // floor(1234567*25/100)
    for (const x of a) expect(parseUsdc(x.write)).toBe(x.value.toString());
  });

  it("shares + reserved labels render losslessly at 6 decimals (no grouping/truncation)", () => {
    const html = render(1234567n, 1000000001n);
    expect(html).toContain("1.234567"); // not "1.23"
    expect(html).toContain("(1000.000001 reserved)"); // not "1,000"
  });
});
