import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import ContextDial, { contextFraction } from "./ContextDial";

describe("contextFraction", () => {
  it("computes the fraction used", () => {
    expect(contextFraction(22541, 131072)).toBeCloseTo(0.172, 2);
  });
  it("clamps above 1 and treats a zero/unknown total as 0", () => {
    expect(contextFraction(200, 100)).toBe(1);
    expect(contextFraction(10, 0)).toBe(0);
  });
});

describe("ContextDial", () => {
  it("labels the percentage and shows model + used/total in the tooltip", () => {
    render(<ContextDial model="openai/gpt-oss-20b" used={22541} total={131072} />);

    expect(screen.getByLabelText("Context 17% used")).toBeTruthy();
    const tip = screen.getByRole("tooltip").textContent ?? "";
    expect(tip).toContain("openai/gpt-oss-20b");
    expect(tip).toContain("22,541 / 131,072 tokens (17%)");
  });

  it("turns the ring red near the limit", () => {
    const { container } = render(<ContextDial model="m" used={95} total={100} />);
    expect(container.querySelector(".stroke-red-500")).toBeTruthy();
  });
});
