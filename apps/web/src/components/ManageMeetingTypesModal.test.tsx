import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { MeetingType } from "../lib/types";

let isAdmin = false;
vi.mock("../auth", () => ({ useAuth: () => ({ isPlatformAdmin: isAdmin }) }));

vi.mock("../lib/api", () => ({
  api: {
    listMeetingTypes: vi.fn(),
    listFormulas: vi.fn(),
    createMeetingType: vi.fn(),
    updateMeetingType: vi.fn(),
    deleteMeetingType: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import ManageMeetingTypesModal from "./ManageMeetingTypesModal";

function mt(id: string, title: string, isPlatform: boolean, canEdit: boolean): MeetingType {
  return {
    id, isPlatform, canEdit, groupName: isPlatform ? "Standard" : "Mine", title,
    overview: "", icon: "document", color: "#5C6BC0",
    primaryFormulaId: null, additionalFormulaIds: [], isDefault: id === "general",
  };
}

function renderModal() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={qc}>
      <ManageMeetingTypesModal onClose={onClose} />
    </QueryClientProvider>,
  );
  return { onClose };
}

describe("ManageMeetingTypesModal", () => {
  beforeEach(() => {
    isAdmin = false;
    vi.mocked(api.listFormulas).mockResolvedValue([
      {
        id: "f1", scope: "Platform", ownerUserId: null, name: "Interview minutes", description: null,
        content: { sections: [] }, context: 1, enabled: true, isBuiltIn: false, shared: false,
      },
    ]);
    vi.mocked(api.listMeetingTypes).mockResolvedValue([
      mt("general", "General Meeting", true, false),
      mt("mine", "My template", false, true),
    ]);
  });

  it("shows only the user's own templates for a normal user", async () => {
    renderModal();
    expect(await screen.findByText("My template")).toBeTruthy();
    expect(screen.queryByText("General Meeting")).toBeNull(); // platform type hidden from a non-admin
  });

  it("shows platform templates and the Platform switch for an admin", async () => {
    isAdmin = true;
    renderModal();
    fireEvent.click(await screen.findByText("General Meeting")); // select a platform type
    expect(screen.getByText("Shared platform template (visible to everyone)")).toBeTruthy();
  });

  it("does not show the Platform switch to a normal user editing their own type", async () => {
    renderModal();
    fireEvent.click(await screen.findByText("My template"));
    expect(screen.queryByText("Shared platform template (visible to everyone)")).toBeNull();
  });

  it("creates a new template via the API on Save", async () => {
    vi.mocked(api.createMeetingType).mockResolvedValue(mt("new", "Interview", false, true));
    renderModal();
    await screen.findByText("My template");

    fireEvent.click(screen.getByText(/New template/));
    // Fill Title + Group (the first two text inputs in the editor).
    const inputs = screen.getAllByRole("textbox");
    fireEvent.change(inputs[0], { target: { value: "Interview" } }); // Title
    fireEvent.change(inputs[1], { target: { value: "Hiring" } });    // Group name

    fireEvent.click(screen.getByText("Save"));

    await waitFor(() => expect(api.createMeetingType).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.createMeetingType).mock.calls[0][0]).toMatchObject({
      title: "Interview", groupName: "Hiring", isPlatform: false,
    });
  });

  it("enables Export only once a template is selected", async () => {
    renderModal();
    await screen.findByText("My template");
    const exportBtn = screen.getByRole("button", { name: "Export" }) as HTMLButtonElement;
    expect(exportBtn.disabled).toBe(true);

    fireEvent.click(screen.getByText("My template"));
    expect(exportBtn.disabled).toBe(false);
  });

  it("imports a template: parses the file, prompts for a name, and creates it", async () => {
    vi.mocked(api.createMeetingType).mockClear(); // this file's beforeEach doesn't clear, so drop earlier calls
    vi.mocked(api.createMeetingType).mockResolvedValue(mt("imp", "Imported Interview", false, true));
    const origPrompt = window.prompt;
    window.prompt = () => "Imported Interview";
    try {
      renderModal();
      await screen.findByText("My template");

      const file = new File(
        [JSON.stringify({
          "diariz-meeting-type": 1, groupName: "Hiring", title: "Interview", overview: "", icon: "chat",
          color: "#5C6BC0",
          // Formula IDs mean nothing on another instance, so the export carries names.
          primaryFormulaName: "Interview minutes", additionalFormulaNames: [],
        })],
        "interview.json",
        { type: "application/json" },
      );
      fireEvent.change(screen.getByTestId("import-input"), { target: { files: [file] } });

      await waitFor(() => expect(api.createMeetingType).toHaveBeenCalledTimes(1));
      const input = vi.mocked(api.createMeetingType).mock.calls[0][0];
      expect(input).toMatchObject({ title: "Imported Interview", groupName: "Hiring", isPlatform: false });
      // The exported name resolved to this instance's formula.
      expect(input.primaryFormulaId).toBe("f1");
    } finally {
      window.prompt = origPrompt;
    }
  });

  it("does not close when the backdrop is clicked (X/Escape only)", async () => {
    const { onClose } = renderModal();
    await screen.findByText("My template");
    // The outermost element is the backdrop; it has no click handler.
    fireEvent.click(screen.getByRole("dialog").parentElement!);
    expect(onClose).not.toHaveBeenCalled();
  });
});
