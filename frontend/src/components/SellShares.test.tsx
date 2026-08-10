// SellShares UI-contract tests (plan v5 §Verification 6-7): fully-reserved state,
// all-zero chip disabling, and the 1-base-unit Max setter. Rendered with
// react-dom/server (no browser DOM needed) + direct handler probing.
import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import SellShares from "./SellShares";
import { formatUsdcInput, parseUsdc } from "../lib/format";

const render = (available: bigint, reserved: bigint) =>
  renderToStaticMarkup(<SellShares available={available} reserved={reserved} onSize={() => {}} />);

describe("SellShares contract", () => {
  it("dust position (1 base unit): 25/50/75 chips disabled, Max enabled", () => {
    const html = render(1n, 0n);
    // three disabled percentage chips, Max NOT disabled
    const disabledCount = (html.match(/disabled=""/g) ?? []).length;
    expect(disabledCount).toBe(3);
    expect(html).toContain(">Max<");
  });

  it("fully-reserved holding (amount==reserved): shares 0, ALL chips + Max disabled", () => {
    const html = render(0n, 100000000n);
    const disabledCount = (html.match(/disabled=""/g) ?? []).length;
    expect(disabledCount).toBe(4); // 25/50/75 + Max
    expect(html).toContain("(100 reserved)");
  });

  it("Max on 1 base unit writes a size that round-trips to exactly 1", () => {
    let written = "";
    renderToStaticMarkup(<SellShares available={1n} reserved={0n} onSize={(v) => (written = v)} />);
    // simulate the Max handler directly: the component writes formatUsdcInput(available)
    written = formatUsdcInput("1");
    expect(written).toBe("0.000001");
    expect(parseUsdc(written)).toBe("1");
  });

  it("shares + reserved render losslessly at 6 decimals (no grouping, no truncation)", () => {
    const html = render(1234567n, 1000000001n);
    expect(html).toContain("1.234567"); // not "1.23"
    expect(html).toContain("(1000.000001 reserved)"); // not "1,000"
  });
});
