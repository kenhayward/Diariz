import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { listSections: vi.fn(), moveRecording: vi.fn(), createSection: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import MoveToSectionModal from "./MoveToSectionModal";

function renderModal(currentSectionId?: string | null) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MoveToSectionModal recordingId="rec-1" currentSectionId={currentSectionId} onClose={() => {}} />
    </QueryClientProvider>,
  );
}

describe("MoveToSectionModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Work" },
      { id: "sec-2", name: "Personal" },
    ]);
    (api.moveRecording as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.createSection as ReturnType<typeof vi.fn>).mockResolvedValue({ id: "sec-new", name: "Ideas" });
  });

  it("moves to an existing section", async () => {
    renderModal(null);
    fireEvent.click(await screen.findByRole("button", { name: /work/i }));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-1"));
  });

  it("ungroups the recording", async () => {
    renderModal("sec-1");
    fireEvent.click(await screen.findByRole("button", { name: /ungrouped/i }));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", null));
  });

  it("creates a new section and moves into it", async () => {
    renderModal(null);
    await screen.findByRole("button", { name: /work/i });
    fireEvent.change(screen.getByLabelText(/new section name/i), { target: { value: "Ideas" } });
    fireEvent.click(screen.getByRole("button", { name: /create.*move/i }));

    await waitFor(() => expect(api.createSection).toHaveBeenCalledWith("Ideas"));
    await waitFor(() => expect(api.moveRecording).toHaveBeenCalledWith("rec-1", "sec-new"));
  });
});
