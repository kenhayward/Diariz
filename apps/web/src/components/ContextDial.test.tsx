import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import ContextDial, { contextBand, contextFraction } from "./ContextDial";

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
  it("shows used / total and the percentage inline (always visible)", () => {
    const { container } = render(<ContextDial model="openai/gpt-oss-20b" used={22541} total={131072} />);

    expect(screen.getByLabelText("Context 17% used")).toBeTruthy();
    // The inline label (not the hover tooltip) carries the numbers.
    expect(container.textContent).toContain("22,541 / 131,072 (17%)");
  });

  it("keeps the model name in the hover tooltip", () => {
    render(<ContextDial model="openai/gpt-oss-20b" used={22541} total={131072} />);
    expect(screen.getByRole("tooltip").textContent).toContain("openai/gpt-oss-20b");
  });

  // ---- Three bands ----
  //
  // This replaces a test that rendered at 95% and asserted the ring was red. That test passed under the
  // OLD rule (red from 90%) and passes under the new one (red from 75%), so keeping it and adding beside
  // it would have left a test that can no longer fail for the threshold it was written to protect.

  describe("contextBand", () => {
    it("is low below half", () => {
      expect(contextBand(0)).toBe("low");
      expect(contextBand(0.49)).toBe("low");
    });

    it("is medium from half up to three quarters", () => {
      expect(contextBand(0.5)).toBe("medium");
      expect(contextBand(0.74)).toBe("medium");
    });

    it("is high from three quarters up", () => {
      expect(contextBand(0.75)).toBe("high");
      expect(contextBand(1)).toBe("high");
    });

    /// Stated explicitly because "above 50%" is ambiguous in English and the boundary is where a colour
    /// change is most visible.
    it("puts each boundary in the upper band", () => {
      expect(contextBand(0.5)).toBe("medium");
      expect(contextBand(0.75)).toBe("high");
    });
  });

  describe("the ring segment", () => {
    const segment = (used: number) =>
      render(<ContextDial model="m" used={used} total={100} />).container.querySelector(
        "circle[stroke-dasharray]",
      )!.getAttribute("class");

    it("is blue at 30%", () => expect(segment(30)).toContain("stroke-blue-500"));
    it("is orange at 60%", () => expect(segment(60)).toContain("stroke-orange-500"));
    it("is red at 80%", () => expect(segment(80)).toContain("stroke-red-500"));
  });

  /// Colour must not be the only carrier of the state.
  it("keeps the percentage in text and in the aria-label whatever the band", () => {
    const { container } = render(<ContextDial model="m" used={80} total={100} />);

    expect(container.textContent).toContain("(80%)");
    expect(screen.getByLabelText("Context 80% used")).toBeTruthy();
  });
});
