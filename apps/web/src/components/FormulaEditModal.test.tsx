import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { createFormula: vi.fn(), updateFormula: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import FormulaEditModal from "./FormulaEditModal";
import type { Formula } from "../lib/types";

function renderModal(overrides: Partial<React.ComponentProps<typeof FormulaEditModal>> = {}) {
  const onClose = vi.fn();
  const onSaved = vi.fn();
  const result = render(<FormulaEditModal onClose={onClose} onSaved={onSaved} {...overrides} />);
  return { onClose, onSaved, ...result };
}

const existingFormula: Formula = {
  id: "f1",
  scope: "Personal",
  ownerUserId: "u1",
  name: "Existing",
  description: "Desc",
  prompt: "Old prompt",
  context: 3, // Transcript (1) + Notes (2)
  enabled: true,
  isBuiltIn: false,
};

describe("FormulaEditModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("renders the required fields for a new formula", () => {
    renderModal();
    expect(screen.getByLabelText(/^name$/i)).toBeTruthy();
    expect(screen.getByLabelText(/^description$/i)).toBeTruthy();
    expect(screen.getByLabelText(/^prompt$/i)).toBeTruthy();
    expect(screen.getByText(/markdown supported/i)).toBeTruthy();
    for (const label of [/transcript/i, /notes/i, /attachments/i, /summary/i, /minutes/i, /actions/i]) {
      expect(screen.getByLabelText(label)).toBeTruthy();
    }
  });

  it("disables Save until name and prompt are filled", () => {
    renderModal();
    const save = () => screen.getByRole("button", { name: /^save$/i }) as HTMLButtonElement;
    expect(save().disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "X" } });
    expect(save().disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/^prompt$/i), { target: { value: "Y" } });
    expect(save().disabled).toBe(false);
  });

  it("toggling context checkboxes builds the right bitmask and creates a Personal formula", async () => {
    (api.createFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
    const { onSaved, onClose } = renderModal();
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "My Formula" } });
    fireEvent.change(screen.getByLabelText(/^prompt$/i), { target: { value: "Summarize this" } });
    fireEvent.click(screen.getByLabelText(/transcript/i)); // bit 1
    fireEvent.click(screen.getByLabelText(/actions/i)); // bit 32
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.createFormula).toHaveBeenCalledWith({
        scope: "Personal",
        name: "My Formula",
        description: null,
        prompt: "Summarize this",
        context: 33,
      }),
    );
    expect(onSaved).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it("prefills fields (including context) from an existing formula and updates it", async () => {
    (api.updateFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
    const { onSaved, onClose } = renderModal({ formula: existingFormula });

    expect(screen.getByDisplayValue("Existing")).toBeTruthy();
    expect(screen.getByDisplayValue("Desc")).toBeTruthy();
    expect(screen.getByDisplayValue("Old prompt")).toBeTruthy();
    expect((screen.getByLabelText(/transcript/i) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText(/notes/i) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText(/attachments/i) as HTMLInputElement).checked).toBe(false);

    fireEvent.change(screen.getByDisplayValue("Existing"), { target: { value: "Renamed" } });
    fireEvent.click(screen.getByLabelText(/attachments/i)); // add bit 4 -> 3 + 4 = 7
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateFormula).toHaveBeenCalledWith("f1", {
        name: "Renamed",
        description: "Desc",
        prompt: "Old prompt",
        context: 7,
      }),
    );
    expect(onSaved).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it("closes on Escape", () => {
    const { onClose } = renderModal();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });

  it("closes on a backdrop click", () => {
    const { onClose, container } = renderModal();
    fireEvent.click(container.firstChild as Element);
    expect(onClose).toHaveBeenCalled();
  });

  it("shows an error and keeps the modal open when save fails", async () => {
    (api.createFormula as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("boom"));
    const { onClose } = renderModal();
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "X" } });
    fireEvent.change(screen.getByLabelText(/^prompt$/i), { target: { value: "Y" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    expect(await screen.findByText("Error: boom")).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
  });
});
