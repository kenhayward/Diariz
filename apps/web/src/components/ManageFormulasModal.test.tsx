import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listManagedFormulas: vi.fn(),
    setFormulaEnabled: vi.fn(),
    deleteFormula: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

vi.mock("./FormulaEditModal", () => ({
  default: ({
    formula,
    scope,
    onClose,
    onSaved,
  }: {
    formula?: { id: string } | null;
    scope?: string;
    onClose: () => void;
    onSaved: () => void;
  }) => (
    <div>
      <span>EDIT_MODAL:{formula ? formula.id : "new"}:{scope ?? "Personal"}</span>
      <button onClick={onClose}>close-editor</button>
      <button onClick={onSaved}>save-editor</button>
    </div>
  ),
}));

import { api } from "../lib/api";
import ManageFormulasModal from "./ManageFormulasModal";
import type { Formula } from "../lib/types";

const formula = (over: Partial<Formula> = {}): Formula => ({
  id: "f1",
  scope: "Platform",
  ownerUserId: null,
  name: "Org Formula",
  description: "A description",
  prompt: "p",
  context: 1,
  enabled: true,
  isBuiltIn: false,
  shared: false,
  ...over,
});

function renderModal() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  const result = render(
    <QueryClientProvider client={qc}>
      <ManageFormulasModal onClose={onClose} />
    </QueryClientProvider>,
  );
  return { onClose, qc, ...result };
}

describe("ManageFormulasModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.confirm = vi.fn(() => true);
  });

  it("lists managed formulas with scope badges", async () => {
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "p1", name: "Platform One", scope: "Platform" }),
      formula({ id: "d1", name: "Diariz One", scope: "Diariz", isBuiltIn: true, ownerUserId: null }),
    ]);
    renderModal();

    await screen.findByText("Platform One");
    expect(screen.getByText("Diariz One")).toBeTruthy();
    expect(screen.getByText("Platform")).toBeTruthy();
    expect(screen.getByText("Diariz")).toBeTruthy();
  });

  it("New formula opens the editor with Platform scope", async () => {
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    renderModal();

    await waitFor(() => expect(api.listManagedFormulas).toHaveBeenCalled());
    fireEvent.click(screen.getByRole("button", { name: /new formula/i }));
    expect(screen.getByText("EDIT_MODAL:new:Platform")).toBeTruthy();
  });

  it("Edit opens the editor for the clicked formula", async () => {
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "p1", name: "Platform One", scope: "Platform" }),
    ]);
    renderModal();

    await screen.findByText("Platform One");
    fireEvent.click(screen.getByRole("button", { name: /edit/i }));
    expect(screen.getByText("EDIT_MODAL:p1:Personal")).toBeTruthy();
  });

  it("the Enabled toggle calls setFormulaEnabled", async () => {
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "p1", name: "Platform One", scope: "Platform", enabled: true }),
    ]);
    (api.setFormulaEnabled as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderModal();

    await screen.findByText("Platform One");
    const toggle = screen.getByLabelText(/enabled for platform one/i) as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    fireEvent.click(toggle);
    await waitFor(() => expect(api.setFormulaEnabled).toHaveBeenCalledWith("p1", false));
  });

  it("hides Delete for a Diariz built-in formula and shows the built-in hint", async () => {
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "d1", name: "Diariz One", scope: "Diariz", isBuiltIn: true }),
    ]);
    renderModal();

    await screen.findByText("Diariz One");
    expect(screen.queryByRole("button", { name: /delete/i })).toBeNull();
    expect(screen.getByText(/built-in/i)).toBeTruthy();
  });

  it("shows Delete for a Platform formula and deletes after confirming", async () => {
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "p1", name: "Platform One", scope: "Platform" }),
    ]);
    (api.deleteFormula as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderModal();

    await screen.findByText("Platform One");
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    expect(window.confirm).toHaveBeenCalled();
    await waitFor(() => expect(api.deleteFormula).toHaveBeenCalledWith("p1"));
  });

  it("does not delete when the confirmation is dismissed", async () => {
    (window.confirm as ReturnType<typeof vi.fn>).mockReturnValue(false);
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "p1", name: "Platform One", scope: "Platform" }),
    ]);
    renderModal();

    await screen.findByText("Platform One");
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    expect(api.deleteFormula).not.toHaveBeenCalled();
  });

  it("does not close on a backdrop click, closes on Escape, and has a persistent footer Close button", async () => {
    (api.listManagedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    const { onClose } = renderModal();

    await waitFor(() => expect(api.listManagedFormulas).toHaveBeenCalled());
    fireEvent.click(screen.getByRole("dialog").parentElement as Element);
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: /^close$/i }));
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
