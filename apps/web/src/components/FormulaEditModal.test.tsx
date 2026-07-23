import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { fromPrompt } from "../lib/formulaTemplate";

vi.mock("../lib/api", () => ({
  api: {
    createFormula: vi.fn(),
    updateFormula: vi.fn(),
    listWorkflowSignals: vi.fn(),
    getProfile: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import FormulaEditModal from "./FormulaEditModal";
import type { Formula } from "../lib/types";

function renderModal(overrides: Partial<React.ComponentProps<typeof FormulaEditModal>> = {}) {
  const onClose = vi.fn();
  const onSaved = vi.fn();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(
    <QueryClientProvider client={qc}>
      <FormulaEditModal onClose={onClose} onSaved={onSaved} {...overrides} />
    </QueryClientProvider>,
  );
  return { onClose, onSaved, ...result };
}

const existingFormula: Formula = {
  id: "f1",
  scope: "Personal",
  ownerUserId: "u1",
  name: "Existing",
  description: "Desc",
  content: fromPrompt("Old prompt"),
  context: 3, // Transcript (1) + Notes (2)
  enabled: true,
  isBuiltIn: false,
  shared: false,
  signals: [],
};


/// Build a template through the real block editor: add a section, title it, add a prompt block, write it.
/// A formula IS a template now, so this is what authoring one actually looks like.
function authorTemplate(sectionTitle: string, promptText: string) {
  fireEvent.click(screen.getByRole("button", { name: /add section/i }));
  fireEvent.change(screen.getByPlaceholderText(/section title/i), { target: { value: sectionTitle } });
  fireEvent.click(screen.getByRole("button", { name: /section actions/i }));
  fireEvent.click(screen.getByRole("menuitem", { name: /add model prompt/i }));
  fireEvent.change(screen.getByLabelText(/^prompt$/i), { target: { value: promptText } });
}

/// What `authorTemplate` produces, as the payload the API should receive.
const authored = (sectionTitle: string, promptText: string) => ({
  sections: [
    {
      level: 1,
      title: sectionTitle,
      blocks: [{ kind: "prompt", text: promptText, breakAfter: "paragraph" }],
    },
  ],
});

describe("FormulaEditModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Hidden by default: no active signals and webhooks off. Individual tests override as needed.
    (api.listWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ webhooksEnabled: false });
  });

  it("renders the required fields for a new formula", () => {
    renderModal();
    expect(screen.getByLabelText(/^name$/i)).toBeTruthy();
    expect(screen.getByLabelText(/^description$/i)).toBeTruthy();
    // A formula is authored as a template, so the block editor is the body.
    expect(screen.getByRole("button", { name: /add section/i })).toBeTruthy();
    for (const label of [/transcript/i, /notes/i, /summary/i, /minutes/i, /^actions$/i]) {
      expect(screen.getByLabelText(label)).toBeTruthy();
    }
    // Attachments is intentionally not surfaced yet (its context flag is a no-op).
    expect(screen.queryByLabelText(/attachments/i)).toBeNull();
  });

  // An empty template generates nothing, and a half-built one (a section with no title, a prompt with no text)
  // would be rejected by the server - so Save stays disabled until the template is actually valid.
  it("disables Save until the formula has a name and a usable template", () => {
    renderModal();
    const save = () => screen.getByRole("button", { name: /^save$/i }) as HTMLButtonElement;
    expect(save().disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "X" } });
    expect(save().disabled).toBe(true); // named, but it would produce nothing

    fireEvent.click(screen.getByRole("button", { name: /add section/i }));
    expect(save().disabled).toBe(true); // a section with no title is invalid

    fireEvent.change(screen.getByPlaceholderText(/section title/i), { target: { value: "Summary" } });
    expect(save().disabled).toBe(false);
  });

  it("toggling context checkboxes builds the right bitmask and creates a Personal formula", async () => {
    (api.createFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
    const { onSaved, onClose } = renderModal();
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "My Formula" } });
    authorTemplate("Summary", "Summarize this");
    fireEvent.click(screen.getByLabelText(/transcript/i)); // bit 1
    fireEvent.click(screen.getByLabelText(/^actions$/i)); // bit 32
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.createFormula).toHaveBeenCalledWith({
        scope: "Personal",
        name: "My Formula",
        description: null,
        content: authored("Summary", "Summarize this"),
        context: 33,
        shared: false,
        signals: [],
      }),
    );
    expect(onSaved).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it("creating with scope=\"Platform\" calls createFormula with scope Platform and titles the modal accordingly", async () => {
    (api.createFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
    const { onSaved, onClose } = renderModal({ scope: "Platform" });

    expect(screen.getByRole("heading", { name: /new platform formula/i })).toBeTruthy();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Org Wide" } });
    authorTemplate("Body", "Do the thing");
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.createFormula).toHaveBeenCalledWith({
        scope: "Platform",
        name: "Org Wide",
        description: null,
        content: authored("Body", "Do the thing"),
        context: 0,
        shared: false,
        signals: [],
      }),
    );
    expect(onSaved).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  // A structured formula is now first-class: it opens in the editor and can be edited, rather than being shown
  // read-only because the editor couldn't represent it.
  it("opens a structured formula in the editor rather than refusing it", () => {
    const structured = {
      ...existingFormula,
      content: {
        sections: [
          { level: 1, title: "Decisions", blocks: [{ kind: "prompt" as const, text: "List them." }] },
        ],
      },
    };
    renderModal({ formula: structured });

    expect((screen.getByDisplayValue("Decisions") as HTMLInputElement).disabled).toBe(false);
    expect(screen.getByDisplayValue("List them.")).toBeTruthy();
    expect((screen.getByRole("button", { name: /^save$/i }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("prefills fields (including context) from an existing formula and updates it", async () => {
    (api.updateFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
    const { onSaved, onClose } = renderModal({ formula: existingFormula });

    expect(screen.getByDisplayValue("Existing")).toBeTruthy();
    expect(screen.getByDisplayValue("Desc")).toBeTruthy();
    // A formula that was just a prompt opens as a headless section holding that prompt - editable, not read-only.
    expect(screen.getByDisplayValue("Old prompt")).toBeTruthy();
    expect((screen.getByLabelText(/transcript/i) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText(/notes/i) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText(/summary/i) as HTMLInputElement).checked).toBe(false);

    fireEvent.change(screen.getByDisplayValue("Existing"), { target: { value: "Renamed" } });
    fireEvent.click(screen.getByLabelText(/summary/i)); // add bit 8 -> 3 + 8 = 11
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateFormula).toHaveBeenCalledWith("f1", {
        name: "Renamed",
        description: "Desc",
        content: {
          sections: [
            { level: 0, title: "", blocks: [{ kind: "prompt", text: "Old prompt", breakAfter: "paragraph" }] },
          ],
        },
        context: 11,
        shared: false,
        signals: [],
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

  it("does not close on a backdrop click", () => {
    const { onClose, container } = renderModal();
    fireEvent.click(container.firstChild as Element);
    expect(onClose).not.toHaveBeenCalled();
  });

  it("renders the Share checkbox for a Personal create but not for Platform", () => {
    const { unmount } = renderModal();
    expect(screen.getByLabelText(/share this formula/i)).toBeTruthy();
    unmount();

    renderModal({ scope: "Platform" });
    expect(screen.queryByLabelText(/share this formula/i)).toBeNull();
  });

  it("toggling Share sends shared: true in the create payload", async () => {
    (api.createFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
    renderModal();
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Shared One" } });
    authorTemplate("Body", "Do it");
    fireEvent.click(screen.getByLabelText(/share this formula/i));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.createFormula).toHaveBeenCalledWith(
        expect.objectContaining({ scope: "Personal", name: "Shared One", shared: true }),
      ),
    );
  });

  it("shows an error and keeps the modal open when save fails", async () => {
    (api.createFormula as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("boom"));
    const { onClose } = renderModal();
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "X" } });
    authorTemplate("Body", "Y");
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    expect(await screen.findByText("Error: boom")).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
  });

  describe("workflow signal picker", () => {
    const signal = {
      id: "sig1",
      key: "digest",
      label: "Send weekly digest",
      description: "Notify the team",
      isActive: true,
    };

    it("shows the active signal and attaches it to the create payload when ticked", async () => {
      (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ webhooksEnabled: true });
      (api.listWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([signal]);
      (api.createFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
      renderModal();

      expect(await screen.findByText(/when this finishes, trigger/i)).toBeTruthy();
      fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "With Signal" } });
      authorTemplate("Body", "Do it");
      fireEvent.click(screen.getByLabelText(/send weekly digest/i));
      fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

      await waitFor(() =>
        expect(api.createFormula).toHaveBeenCalledWith(expect.objectContaining({ signals: ["sig1"] })),
      );
    });

    it("hides the section when webhooks are disabled even if active signals exist", async () => {
      (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ webhooksEnabled: false });
      (api.listWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([signal]);
      renderModal();

      await waitFor(() => expect(api.listWorkflowSignals).toHaveBeenCalled());
      expect(screen.queryByText(/when this finishes, trigger/i)).toBeNull();
      expect(screen.queryByText(/send weekly digest/i)).toBeNull();
    });

    it("hides the section when there are no active signals", async () => {
      (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ webhooksEnabled: true });
      (api.listWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([{ ...signal, isActive: false }]);
      renderModal();

      await waitFor(() => expect(api.listWorkflowSignals).toHaveBeenCalled());
      expect(screen.queryByText(/when this finishes, trigger/i)).toBeNull();
    });

    it("pre-populates ticked signals when editing an existing formula and sends them back on save", async () => {
      (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ webhooksEnabled: true });
      (api.listWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([signal]);
      (api.updateFormula as ReturnType<typeof vi.fn>).mockResolvedValue({});
      renderModal({ formula: { ...existingFormula, signals: ["sig1"] } });

      const checkbox = (await screen.findByLabelText(/send weekly digest/i)) as HTMLInputElement;
      expect(checkbox.checked).toBe(true);

      fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
      await waitFor(() =>
        expect(api.updateFormula).toHaveBeenCalledWith("f1", expect.objectContaining({ signals: ["sig1"] })),
      );
    });
  });
});
