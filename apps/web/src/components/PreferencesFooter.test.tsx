import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { useState } from "react";
import { PreferencesFooterProvider, PreferencesFooterBar, usePreferencesFooter } from "./PreferencesFooter";

/// A stand-in for a tab that opts into the footer. `onSave` is a fresh closure on every render on
/// purpose - that is the exact shape that would loop a naive registration effect.
function RegisteringTab({ onSave, status = "unsaved", busy = false, error = null }: {
  onSave: () => void;
  status?: "idle" | "unsaved" | "saved";
  busy?: boolean;
  error?: string | null;
}) {
  usePreferencesFooter({ dirty: status === "unsaved", busy, status, error, onSave: () => onSave() });
  return <div>TAB</div>;
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <PreferencesFooterProvider>
      {children}
      <PreferencesFooterBar onClose={() => {}} />
    </PreferencesFooterProvider>
  );
}

describe("PreferencesFooter", () => {
  it("shows Close alone when no tab has registered", () => {
    render(<Shell><div>PLAIN_TAB</div></Shell>);
    expect(screen.getByRole("button", { name: /close/i })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();
  });

  it("shows Save changes and the status line once a tab registers", () => {
    render(<Shell><RegisteringTab onSave={() => {}} /></Shell>);
    expect(screen.getByRole("button", { name: /save changes/i })).toBeTruthy();
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
  });

  it("calls the registering tab's handler, using the latest closure rather than the first", () => {
    const first = vi.fn();
    const second = vi.fn();
    function Swapper() {
      const [fn, setFn] = useState(() => first);
      return (
        <>
          <button type="button" onClick={() => setFn(() => second)}>swap</button>
          <RegisteringTab onSave={fn} />
        </>
      );
    }
    render(<Shell><Swapper /></Shell>);
    fireEvent.click(screen.getByRole("button", { name: "swap" }));
    fireEvent.click(screen.getByRole("button", { name: /save changes/i }));
    expect(second).toHaveBeenCalled();
    expect(first).not.toHaveBeenCalled();
  });

  it("deregisters when the tab unmounts, restoring the plain footer", () => {
    function Toggle() {
      const [on, setOn] = useState(true);
      return (
        <>
          <button type="button" onClick={() => setOn(false)}>hide</button>
          {on && <RegisteringTab onSave={() => {}} />}
        </>
      );
    }
    render(<Shell><Toggle /></Shell>);
    expect(screen.getByRole("button", { name: /save changes/i })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "hide" }));
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();
  });

  it("renders Saved, and an error in place of the status", () => {
    const { rerender } = render(<Shell><RegisteringTab onSave={() => {}} status="saved" /></Shell>);
    expect(screen.getByText("Saved")).toBeTruthy();

    rerender(<Shell><RegisteringTab onSave={() => {}} status="idle" error="Could not save." /></Shell>);
    const err = screen.getByText("Could not save.");
    expect(err.className).toContain("text-red");
  });

  it("disables Save while busy", () => {
    render(<Shell><RegisteringTab onSave={() => {}} busy /></Shell>);
    expect((screen.getByRole("button", { name: /saving/i }) as HTMLButtonElement).disabled).toBe(true);
  });
});
