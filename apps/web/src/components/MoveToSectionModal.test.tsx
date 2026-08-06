import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { listSections: vi.fn(), moveRecording: vi.fn(), createSection: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import MoveToSectionModal from "./MoveToSectionModal";

function renderModal(currentSectionId?: string | null, onClose: () => void = () => {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MoveToSectionModal recordingId="rec-1" currentSectionId={currentSectionId} onClose={onClose} />
    </QueryClientProvider>,
  );
}

describe("MoveToSectionModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Work", parentId: null, position: 0 },
      { id: "sec-2", name: "Personal", parentId: null, position: 1 },
    ]);
    (api.moveRecording as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.createSection as ReturnType<typeof vi.fn>).mockResolvedValue({ id: "sec-new", name: "Ideas" });
  });

  it("renders the folder picker instead of a flat button list", async () => {
    renderModal(null);
    // FolderPicker's own filter box and folder list, not the old flat list of section buttons.
    expect(await screen.findByLabelText("Filter folders")).toBeTruthy();
    expect(screen.getByRole("list", { name: "Folders" })).toBeTruthy();
  });

  it("moves to an existing section chosen from the picker", async () => {
    renderModal(null);
    fireEvent.click(await screen.findByLabelText("Select Work"));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-1", undefined));
  });

  it("ungroups the recording when the picker's root is chosen", async () => {
    renderModal("sec-1");
    fireEvent.click(await screen.findByLabelText("Select Ungrouped"));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", null, undefined));
  });

  it("marks the recording's current section as selected in the picker", async () => {
    renderModal("sec-1");
    expect((await screen.findByLabelText("Select Work")).getAttribute("aria-current")).toBe("true");
    expect(screen.getByLabelText("Select Ungrouped").getAttribute("aria-current")).toBeNull();
  });

  it("marks nothing as selected when the current section is unknown", async () => {
    renderModal(undefined);
    expect((await screen.findByLabelText("Select Work")).getAttribute("aria-current")).toBeNull();
    expect(screen.getByLabelText("Select Ungrouped").getAttribute("aria-current")).toBeNull();
  });

  it("still finds a deeply nested section via the picker's filter", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Work", parentId: null, position: 0 },
      { id: "sec-1a", name: "Acme Corp", parentId: "sec-1", position: 0 },
    ]);
    renderModal(null);
    await userEvent.type(await screen.findByLabelText("Filter folders"), "Acme");
    fireEvent.click(await screen.findByLabelText("Select Work › Acme Corp"));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-1a", undefined));
  });

  it("creates a new section and moves into it", async () => {
    renderModal(null);
    await screen.findByLabelText("Filter folders");
    fireEvent.change(screen.getByLabelText(/new section name/i), { target: { value: "Ideas" } });
    fireEvent.click(screen.getByRole("button", { name: /create.*move/i }));

    await waitFor(() => expect(api.createSection).toHaveBeenCalledWith("Ideas", null, undefined));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-new", undefined));
  });

  it("shows an error and keeps the picker open when the move fails", async () => {
    (api.moveRecording as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("boom"));
    const onClose = vi.fn();
    renderModal(null, onClose);
    fireEvent.click(await screen.findByLabelText("Select Work"));
    expect(await screen.findByText(/boom/)).toBeTruthy();
    expect(screen.getByRole("dialog", { name: /move to section/i })).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("closes after a successful move", async () => {
    const onClose = vi.fn();
    renderModal(null, onClose);
    fireEvent.click(await screen.findByLabelText("Select Work"));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it("scopes the move and section creation to a shared room", async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MoveToSectionModal recordingId="rec-1" currentSectionId={null} roomId="eng-room" onClose={() => {}} />
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByLabelText("Select Work"));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-1", "eng-room"));
    expect(api.listSections).toHaveBeenCalledWith("eng-room");
  });
});
