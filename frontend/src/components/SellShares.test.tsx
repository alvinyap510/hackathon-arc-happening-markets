// SellShares UI-contract tests (plan v5 §Verification 6-7). Rounds 3-4 required the
// REAL rendered button contract: mount in a DOM, identify each control by label,
// assert its disabled state, CLICK Max, and assert the exact string onSize receives.
// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { act } from "react";
import { createRoot } from "react-dom/client";
import SellShares from "./SellShares";
import { parseUsdc } from "../lib/format";

function mount(available: bigint, reserved: bigint) {
  const captured: string[] = [];
  const host = document.createElement("div");
  document.body.appendChild(host);
  const root = createRoot(host);
  act(() => {
    root.render(<SellShares available={available} reserved={reserved} onSize={(v) => captured.push(v)} />);
  });
  const buttons = [...host.querySelectorAll("button")] as HTMLButtonElement[];
  const byLabel = (label: string) => buttons.find((b) => b.textContent === label)!;
  return { captured, byLabel, host };
}

describe("SellShares rendered button contract", () => {
  it("dust position (1 base unit): 25/50/75 disabled, Max ENABLED; clicking Max writes exactly 1 unit", () => {
    const { captured, byLabel } = mount(1n, 0n);
    for (const label of ["25%", "50%", "75%"]) expect(byLabel(label).disabled).toBe(true);
    const max = byLabel("Max");
    expect(max.disabled).toBe(false);
    act(() => max.click());
    expect(captured).toEqual(["0.000001"]);
    expect(parseUsdc(captured[0])).toBe("1"); // Max == exact available base units
  });

  it("fully-reserved holding (amount==reserved): every control disabled by label", () => {
    const { byLabel, host } = mount(0n, 100000000n);
    for (const label of ["25%", "50%", "75%", "Max"]) expect(byLabel(label).disabled).toBe(true);
    expect(host.textContent).toContain("(100 reserved)");
  });

  it("percent chips floor in base units; clicking each writes a lossless string", () => {
    const { captured, byLabel } = mount(1234567n, 0n);
    act(() => byLabel("25%").click()); // floor(1234567*25/100) = 308641
    act(() => byLabel("Max").click());
    expect(captured.map((v) => parseUsdc(v))).toEqual(["308641", "1234567"]);
  });

  it("shares + reserved labels render losslessly at 6 decimals (no grouping/truncation)", () => {
    const { host } = mount(1234567n, 1000000001n);
    expect(host.textContent).toContain("1.234567"); // not "1.23"
    expect(host.textContent).toContain("(1000.000001 reserved)"); // not "1,000"
  });
});
