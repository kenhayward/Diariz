import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

const roomStub = {
  currentRoom: { id: "p1", isPersonal: true } as { id: string; isPersonal: boolean },
  recordingSectionId: null as string | null,
};
vi.mock("./rooms", () => ({ useRoom: () => roomStub }));
vi.mock("./api", () => ({
  api: { uploadFile: vi.fn().mockResolvedValue({ id: "r1" }) },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "./api";
import { UploadProvider, useUpload } from "./uploadContext";

function Harness() {
  const { uploadFiles } = useUpload();
  return (
    <button onClick={() => uploadFiles([new File(["x"], "clip.webm", { type: "audio/webm" })])}>go</button>
  );
}

function renderHarness() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <UploadProvider>
        <Harness />
      </UploadProvider>
    </QueryClientProvider>,
  );
}

describe("UploadProvider room placement", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    roomStub.currentRoom = { id: "p1", isPersonal: true };
    roomStub.recordingSectionId = null;
  });

  it("files an upload into the folder the placement preference resolves to", async () => {
    // Same preference (Settings -> Recordings) that decides where a recorded take lands - an uploaded file
    // is a recording too, so it follows the same rule rather than always landing in Ungrouped.
    roomStub.recordingSectionId = "sec-7";
    renderHarness();
    fireEvent.click(screen.getByText("go"));
    await waitFor(() => expect(api.uploadFile).toHaveBeenCalled());
    expect((api.uploadFile as Mock).mock.calls[0][3]).toBe("sec-7"); // the sectionId argument
  });

  it("sends no folder for an upload into a shared room", async () => {
    // Mirrors recording into a shared room: the file is shared there and its personal-room placement stays
    // ungrouped, so a personal folder id must not ride along.
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    roomStub.recordingSectionId = "sec-7";
    renderHarness();
    fireEvent.click(screen.getByText("go"));
    await waitFor(() => expect(api.uploadFile).toHaveBeenCalled());
    expect((api.uploadFile as Mock).mock.calls[0][2]).toBe("eng-room");
    expect((api.uploadFile as Mock).mock.calls[0][3]).toBeNull();
  });

  it("shares an upload into the current shared room", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    renderHarness();
    fireEvent.click(screen.getByText("go"));
    await waitFor(() => expect(api.uploadFile).toHaveBeenCalled());
    expect((api.uploadFile as Mock).mock.calls[0][2]).toBe("eng-room"); // the roomId argument
  });

  it("uploads to the personal room with no roomId", async () => {
    roomStub.currentRoom = { id: "p1", isPersonal: true };
    renderHarness();
    fireEvent.click(screen.getByText("go"));
    await waitFor(() => expect(api.uploadFile).toHaveBeenCalled());
    expect((api.uploadFile as Mock).mock.calls[0][2]).toBeNull();
  });
});
