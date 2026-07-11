import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { RoomPermission, type RoomListItem } from "../lib/types";

const podcasts: RoomListItem = {
  id: "eng", name: "Podcasts", kind: 1, icon: "star", color: "#123456", isPersonal: false,
  permissions: RoomPermission.CreateRecording,
};
const personal: RoomListItem = {
  id: "p1", name: "Ada", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63,
};

vi.mock("../lib/rooms", () => ({ useRoom: () => ({ rooms: [personal, podcasts] }) }));
vi.mock("../lib/api", () => ({
  api: { shareRecordingToRoom: vi.fn().mockResolvedValue(undefined) },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import { ToastProvider } from "../lib/toast";
import ShareToRoomModal from "./ShareToRoomModal";

function renderModal(onClose = () => {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ToastProvider>
        <ShareToRoomModal
          recordingId="rec1"
          recordingName="Weekly meeting"
          fromRoomId="p1"
          alreadyInRoomIds={[]}
          onClose={onClose}
        />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

describe("ShareToRoomModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("lists shareable shared rooms with their icon badge", () => {
    renderModal();
    const button = screen.getByRole("button", { name: /Podcasts/i });
    expect(button.querySelector("svg")).toBeTruthy(); // the room's icon, not just a dot
    expect(screen.queryByRole("button", { name: /^Ada$/i })).toBeNull(); // personal room isn't a target
  });

  it("shares into the picked room and shows a confirmation toast", async () => {
    const onClose = vi.fn();
    renderModal(onClose);
    fireEvent.click(screen.getByRole("button", { name: /Podcasts/i }));

    await waitFor(() => expect(api.shareRecordingToRoom).toHaveBeenCalledWith("rec1", "p1", "eng"));
    expect(await screen.findByText("Weekly meeting shared to Podcasts.")).toBeTruthy();
    expect(onClose).toHaveBeenCalled();
  });
});
