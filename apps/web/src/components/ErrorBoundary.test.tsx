import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ErrorBoundary from "./ErrorBoundary";

function Boom(): React.ReactElement {
  throw new Error("kaboom");
}

describe("ErrorBoundary", () => {
  it("shows a message (not a blank page) when a child throws, and logs the error", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    render(
      <ErrorBoundary resetKey="a" message="Broken" hint="try again">
        <Boom />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.getByText("Broken")).toBeTruthy();
    expect(screen.getByText("try again")).toBeTruthy();
    expect(screen.getByText("kaboom")).toBeTruthy(); // the real error is surfaced for diagnosis
    expect(spy).toHaveBeenCalled();
    spy.mockRestore();
  });

  it("renders children normally when they don't throw", () => {
    render(
      <ErrorBoundary resetKey="a" message="Broken">
        <span>hello</span>
      </ErrorBoundary>,
    );
    expect(screen.getByText("hello")).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("recovers when resetKey changes (navigating to another page)", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const { rerender } = render(
      <ErrorBoundary resetKey="a" message="Broken">
        <Boom />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("alert")).toBeTruthy();

    rerender(
      <ErrorBoundary resetKey="b" message="Broken">
        <span>recovered</span>
      </ErrorBoundary>,
    );
    expect(screen.getByText("recovered")).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
    spy.mockRestore();
  });
});
