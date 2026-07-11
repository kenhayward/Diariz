import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

const roomStub = { currentRoom: { id: "p1", isPersonal: true } as { id: string; isPersonal: boolean } };
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
