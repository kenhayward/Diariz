import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { listFormulas: vi.fn(), deleteFormula: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

vi.mock("./FormulaEditModal", () => ({
  default: ({
    formula,
    onClose,
    onSaved,
  }: {
    formula?: { id: string } | null;
    onClose: () => void;
    onSaved: () => void;
  }) => (
    <div>
      <span>EDIT_MODAL:{formula ? formula.id : "new"}</span>
      <button onClick={onClose}>close-editor</button>
      <button onClick={onSaved}>save-editor</button>
    </div>
  ),
}));

import { api } from "../lib/api";
import FormulasSection from "./FormulasSection";
import type { Formula } from "../lib/types";

const formula = (over: Partial<Formula> = {}): Formula => ({
  id: "f1",
  scope: "Personal",
  ownerUserId: "u1",
  name: "My Formula",
  description: "A description",
  prompt: "p",
  context: 1,
  enabled: true,
  isBuiltIn: false,
  ...over,
});

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <FormulasSection />
    </QueryClientProvider>,
  );
}

describe("FormulasSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.confirm = vi.fn(() => true);
  });

  it("lists only the user's Personal formulas", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "p1", name: "Personal One", scope: "Personal" }),
      formula({ id: "d1", name: "Diariz One", scope: "Diariz" }),
      formula({ id: "pl1", name: "Platform One", scope: "Platform" }),
    ]);
    renderSection();
    await screen.findByText("Personal One");
    expect(screen.queryByText("Diariz One")).toBeNull();
    expect(screen.queryByText("Platform One")).toBeNull();
  });

  it("shows an empty state when there are no Personal formulas", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([formula({ scope: "Diariz" })]);
    renderSection();
    expect(await screen.findByText(/no formulas/i)).toBeTruthy();
  });

  it("opens the editor for a new formula", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    renderSection();
    await screen.findByText(/no formulas/i);
    fireEvent.click(screen.getByRole("button", { name: /new formula/i }));
    expect(screen.getByText("EDIT_MODAL:new")).toBeTruthy();
  });

  it("opens the editor for editing an existing formula", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([formula({ id: "p1", name: "Personal One" })]);
    renderSection();
    await screen.findByText("Personal One");
    fireEvent.click(screen.getByRole("button", { name: /edit/i }));
    expect(screen.getByText("EDIT_MODAL:p1")).toBeTruthy();
  });

  it("deletes a formula after the user confirms", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([formula({ id: "p1", name: "Personal One" })]);
    (api.deleteFormula as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderSection();
    await screen.findByText("Personal One");
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    expect(window.confirm).toHaveBeenCalled();
    await waitFor(() => expect(api.deleteFormula).toHaveBeenCalledWith("p1"));
  });

  it("does not delete when the confirmation is dismissed", async () => {
    (window.confirm as ReturnType<typeof vi.fn>).mockReturnValue(false);
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([formula({ id: "p1", name: "Personal One" })]);
    renderSection();
    await screen.findByText("Personal One");
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    expect(api.deleteFormula).not.toHaveBeenCalled();
  });
});
