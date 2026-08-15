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

/// Like `renderModal`, but hands back the QueryClient so a test can watch what the modal invalidates.
/// The breadcrumbs on the recording detail page are derived from the `["recording", id]` query, which is a
/// different key from the `["recordings"]` list - so "the list refreshed" does not mean "the breadcrumbs
/// refreshed", and only a direct assertion on the key distinguishes them.
function renderModalWithClient(currentSectionId?: string | null) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const invalidate = vi.spyOn(qc, "invalidateQueries");
  const view = render(
    <QueryClientProvider client={qc}>
      <MoveToSectionModal recordingId="rec-1" currentSectionId={currentSectionId} onClose={() => {}} />
    </QueryClientProvider>,
  );
  /// Every key this modal invalidated, flattened for readable assertions.
  const invalidatedKeys = () => invalidate.mock.calls.map((c) => JSON.stringify(c[0]?.queryKey));
  return { ...view, invalidatedKeys };
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
    fireEvent.change(screen.getByLabelText(/new folder name/i), { target: { value: "Ideas" } });
    fireEvent.click(screen.getByRole("button", { name: /create.*move/i }));

    await waitFor(() => expect(api.createSection).toHaveBeenCalledWith("Ideas", null, undefined));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-new", undefined));
  });

  describe("create-and-move follows the folder picker's drill position", () => {
    beforeEach(() => {
      // A real, non-null id to drill into - the point is to prove the created folder's parent follows the
      // drill rather than always being null (the old behaviour, which a null-id fixture could not
      // distinguish from the new one).
      (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
        { id: "sec-1", name: "Work", parentId: null, position: 0 },
        { id: "sec-1a", name: "Acme Corp", parentId: "sec-1", position: 0 },
      ]);
    });

    it("creates the folder under the drilled folder, not at the top level", async () => {
      renderModal(null);
      fireEvent.click(await screen.findByLabelText("Open Work"));

      fireEvent.change(screen.getByLabelText(/new sub-folder in work/i), { target: { value: "Falcon" } });
      fireEvent.click(screen.getByRole("button", { name: /create.*move/i }));

      await waitFor(() => expect(api.createSection).toHaveBeenCalledWith("Falcon", "sec-1", undefined));
      await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-new", undefined));
    });

    it("still creates at the top level (null parent) when not drilled into anything", async () => {
      renderModal(null);
      await screen.findByLabelText("Filter folders");
      fireEvent.change(screen.getByLabelText(/new folder name/i), { target: { value: "Ideas" } });
      fireEvent.click(screen.getByRole("button", { name: /create.*move/i }));

      await waitFor(() => expect(api.createSection).toHaveBeenCalledWith("Ideas", null, undefined));
    });

    it("shows where the folder will be created, and updates the placeholder as the drill changes", async () => {
      renderModal(null);
      expect(await screen.findByLabelText("Open Work")).toBeTruthy(); // waits for the fetched sections
      expect(screen.getByLabelText("New folder name")).toBeTruthy();

      fireEvent.click(screen.getByLabelText("Open Work"));
      expect(await screen.findByLabelText("New sub-folder in Work")).toBeTruthy();
    });

    it("disables the create form at the folder depth cap, with the existing nest-capped message, and makes no request", async () => {
      // A chain exactly 8 levels deep - drilling into the 8th (deepest) folder sits right at the cap
      // sectionCreateTarget enforces (MAX_FOLDER_DEPTH = 8).
      const deepChain = Array.from({ length: 8 }, (_, i) => ({
        id: `d${i + 1}`,
        name: `Level ${i + 1}`,
        parentId: i === 0 ? null : `d${i}`,
        position: 0,
      }));
      (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue(deepChain);

      renderModal(null);
      for (let i = 1; i <= 8; i++) {
        fireEvent.click(await screen.findByLabelText(`Open Level ${i}`));
      }

      const input = await screen.findByLabelText("Folders can only be nested 8 levels deep");
      expect((input as HTMLInputElement).disabled).toBe(true);
      const button = screen.getByRole("button", { name: /create.*move/i });
      expect((button as HTMLButtonElement).disabled).toBe(true);

      fireEvent.change(input, { target: { value: "Too deep" } });
      fireEvent.click(button);
      expect(api.createSection).not.toHaveBeenCalled();
    });
  });

  it("shows an error and keeps the picker open when the move fails", async () => {
    (api.moveRecording as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("boom"));
    const onClose = vi.fn();
    renderModal(null, onClose);
    fireEvent.click(await screen.findByLabelText("Select Work"));
    expect(await screen.findByText(/boom/)).toBeTruthy();
    expect(screen.getByRole("dialog", { name: /move to folder/i })).toBeTruthy();
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

  /// The recording's detail page derives its folder breadcrumbs from the `["recording", id]` query. Moving
  /// the recording used to invalidate only the `["recordings"]` list, so the page you moved it *from* kept
  /// showing the old folder until a reload.
  describe("refreshes the recording it moved", () => {
    it("invalidates the recording's detail query after moving to a folder", async () => {
      const { invalidatedKeys } = renderModalWithClient(null);
      fireEvent.click(await screen.findByLabelText("Select Work"));

      await waitFor(() => expect(api.moveRecording).toHaveBeenCalled());
      await waitFor(() => expect(invalidatedKeys()).toContain(JSON.stringify(["recording", "rec-1"])));
    });

    it("still invalidates the recordings list after moving to a folder", async () => {
      const { invalidatedKeys } = renderModalWithClient(null);
      fireEvent.click(await screen.findByLabelText("Select Work"));

      await waitFor(() => expect(invalidatedKeys()).toContain(JSON.stringify(["recordings"])));
    });

    it("invalidates the recording's detail query after create-and-move", async () => {
      const { invalidatedKeys } = renderModalWithClient(null);
      await screen.findByLabelText("Filter folders");
      fireEvent.change(screen.getByLabelText(/new folder name/i), { target: { value: "Ideas" } });
      fireEvent.click(screen.getByRole("button", { name: /create.*move/i }));

      await waitFor(() => expect(api.createSection).toHaveBeenCalled());
      await waitFor(() => expect(invalidatedKeys()).toContain(JSON.stringify(["recording", "rec-1"])));
    });
  });
});
