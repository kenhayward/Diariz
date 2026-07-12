import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import FormulasToolbar from "./FormulasToolbar";

function setup(selectedId: string | null) {
  const handlers = {
    onRun: vi.fn(),
    onOpen: vi.fn(),
    onDownload: vi.fn(),
    onEmail: vi.fn(),
    onDelete: vi.fn(),
  };
  render(<FormulasToolbar selectedId={selectedId} {...handlers} />);
  return handlers;
}

const disabled = (name: RegExp) => (screen.getByRole("button", { name }) as HTMLButtonElement).disabled;

describe("FormulasToolbar", () => {
  it("always enables Run formula, even with no selection", () => {
    setup(null);
    expect(disabled(/run formula/i)).toBe(false);
  });

  it("disables Open/Download/Email/Delete when nothing is selected", () => {
    setup(null);
    expect(disabled(/^open$/i)).toBe(true);
    expect(disabled(/download/i)).toBe(true);
    expect(disabled(/email/i)).toBe(true);
    expect(disabled(/delete/i)).toBe(true);
  });

  it("enables Open/Download/Email/Delete when exactly one result is selected", () => {
    setup("res-1");
    expect(disabled(/^open$/i)).toBe(false);
    expect(disabled(/download/i)).toBe(false);
    expect(disabled(/email/i)).toBe(false);
    expect(disabled(/delete/i)).toBe(false);
  });

  it("invokes the matching handler for each button", () => {
    const handlers = setup("res-1");
    fireEvent.click(screen.getByRole("button", { name: /run formula/i }));
    expect(handlers.onRun).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /^open$/i }));
    expect(handlers.onOpen).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /download/i }));
    expect(handlers.onDownload).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /email/i }));
    expect(handlers.onEmail).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    expect(handlers.onDelete).toHaveBeenCalled();
  });
});
