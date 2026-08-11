import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi } from "vitest";
import type { RecordingSummary } from "../../lib/types";

// The row's kebab menu pulls in useRecordingActions, which opens a MoveToSectionModal that lists sections.
// An absent method fails as an opaque crash rather than a clear assertion, so stub what it can reach.
vi.mock("../../lib/api", () => ({
  api: {
    renameRecording: vi.fn(),
    deleteRecording: vi.fn(),
    audioUrl: vi.fn(),
    listSections: vi.fn().mockResolvedValue([]),
    moveRecording: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

// useRecordingActions reads useSharedRoomId as well as useRoomBasePath - a partial mock crashes on mount.
vi.mock("../../lib/rooms", () => ({
  useRoom: () => ({ currentRoom: { id: "p1", isPersonal: true }, can: () => true }),
  useRoomBasePath: () => "",
  useSharedRoomId: () => undefined,
}));

import { RecordingRow } from "./RecordingRow";

const rec: RecordingSummary = {
  id: "rec-1",
  title: "Mic 6/26/2026",
  name: "Weekly Standup",
  source: "System",
  durationMs: 9000, // 0:09
  status: "Transcribed",
  createdAt: new Date(2025, 7, 11, 14, 30).toISOString(),
  sectionId: null,
  sectionName: null,
  hasActions: false,
  hasAudio: true,
  calendarEventId: null,
};

function renderRow(props: Partial<React.ComponentProps<typeof RecordingRow>> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <ul>
          <RecordingRow
            r={rec}
            indentClass="pl-3"
            selectMode={false}
            selected={false}
            onToggleSelect={() => {}}
            {...props}
          />
        </ul>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("RecordingRow", () => {
  /// The row's right-hand column answers "when was this?", which is what people scan a list for. The
  /// duration is still one hover away.
  it("shows the recording's date and time", () => {
    renderRow();
    expect(screen.getByText("11 Aug 2025 14:30")).toBeTruthy();
  });

  it("no longer shows the duration in the row", () => {
    renderRow();
    expect(screen.queryByText("0:09")).toBeNull();
  });

  it("keeps the duration in the hover title", () => {
    renderRow();
    expect(screen.getByRole("link").getAttribute("title")).toContain("0:09");
  });

  it("reorders when a drop handler is supplied", () => {
    const onDropBefore = vi.fn();
    renderRow({ onDropBefore });

    fireEvent.drop(screen.getByRole("link").closest("li")!, { dataTransfer: { getData: () => "other" } });

    expect(onDropBefore).toHaveBeenCalledWith("other");
  });

  /// Reordering is switched off while the list is sorted. The row must then be *transparent* to the drop,
  /// not swallow it - the level behind it appends the recording instead.
  it("lets a drop through when no drop handler is supplied", () => {
    const onBackgroundDrop = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter>
          <div onDrop={onBackgroundDrop}>
            <ul>
              <RecordingRow
                r={rec}
                indentClass="pl-3"
                selectMode={false}
                selected={false}
                onToggleSelect={() => {}}
              />
            </ul>
          </div>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.drop(screen.getByRole("link").closest("li")!, { dataTransfer: { getData: () => "other" } });

    expect(onBackgroundDrop).toHaveBeenCalled();
  });
});
