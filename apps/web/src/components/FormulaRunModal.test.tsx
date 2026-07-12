import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listFormulas: vi.fn(),
    runFormula: vi.fn(),
  },
  apiErrorMessage: (e: unknown, fallback: string) => {
    const resp = (e as { response?: { data?: string } })?.response?.data;
    return resp ?? fallback ?? String(e);
  },
}));

import { api } from "../lib/api";
import FormulaRunModal from "./FormulaRunModal";
import type { Formula } from "../lib/types";

const formula = (over: Partial<Formula> = {}): Formula => ({
  id: "f1",
  scope: "Personal",
  ownerUserId: "u1",
  name: "Action Items",
  description: "Extract action items",
  prompt: "…",
  context: 1,
  enabled: true,
  isBuiltIn: false,
  ...over,
});

function renderModal(overrides: Partial<React.ComponentProps<typeof FormulaRunModal>> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  const onRun = vi.fn();
  const onError = vi.fn();
  render(
    <QueryClientProvider client={qc}>
      <FormulaRunModal recordingId="rec-1" onClose={onClose} onRun={onRun} onError={onError} {...overrides} />
    </QueryClientProvider>,
  );
  return { onClose, onRun, onError };
}

describe("FormulaRunModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("lists formulas grouped under their scope heading", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "f1", name: "Diariz Summary", scope: "Diariz" }),
      formula({ id: "f2", name: "Team Recap", scope: "Platform" }),
      formula({ id: "f3", name: "My Formula", scope: "Personal" }),
    ]);
    renderModal();
    await screen.findByText("Diariz Summary");
    expect(screen.getByText("Diariz")).toBeTruthy();
    expect(screen.getByText("Platform")).toBeTruthy();
    expect(screen.getByText("Personal")).toBeTruthy();
    expect(screen.getByText("Team Recap")).toBeTruthy();
    expect(screen.getByText("My Formula")).toBeTruthy();
  });

  it("filters the list as the user types", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      formula({ id: "f1", name: "Action Items" }),
      formula({ id: "f2", name: "Meeting Recap" }),
    ]);
    renderModal();
    await screen.findByText("Action Items");
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "recap" } });
    expect(screen.queryByText("Action Items")).toBeNull();
    expect(screen.getByText("Meeting Recap")).toBeTruthy();
  });

  it("runs the picked formula and notifies the parent on success", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([formula({ id: "f1", name: "Action Items" })]);
    const result = { id: "res-1", recordingId: "rec-1", name: "Action Items", createdByUserId: "u1", createdAt: "now", updatedAt: "now" };
    (api.runFormula as ReturnType<typeof vi.fn>).mockResolvedValue(result);
    const { onRun, onClose } = renderModal();
    await screen.findByText("Action Items");
    fireEvent.click(screen.getByText("Action Items"));
    await waitFor(() => expect(api.runFormula).toHaveBeenCalledWith("rec-1", "f1"));
    await waitFor(() => expect(onRun).toHaveBeenCalledWith(result));
    expect(onClose).toHaveBeenCalled();
  });

  it("shows a run error inline and keeps the modal open so the user can retry", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([formula({ id: "f1", name: "Action Items" })]);
    (api.runFormula as ReturnType<typeof vi.fn>).mockRejectedValue({ response: { data: "Formulas need an AI endpoint" } });
    const { onClose } = renderModal();
    await screen.findByText("Action Items");
    fireEvent.click(screen.getByText("Action Items"));
    // The error is rendered inline inside the modal (not just delegated to the parent banner, which sits
    // under the modal backdrop and would be invisible).
    expect(await screen.findByText("Formulas need an AI endpoint")).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("closes on Escape", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    const { onClose } = renderModal();
    await screen.findByText(/manage formulas/i);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });

  it("shows the Manage formulas link", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    renderModal();
    expect(await screen.findByText(/manage formulas/i)).toBeTruthy();
  });
});
