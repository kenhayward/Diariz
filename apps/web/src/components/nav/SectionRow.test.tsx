import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import SectionRow from "./SectionRow";
import { MoveClipboardProvider, useMoveClipboard, type MoveClipboardCut } from "../../lib/moveClipboard";

const roomStub: { sharedRoomId: string | undefined } = { sharedRoomId: undefined };
vi.mock("../../lib/rooms", () => ({
  useRoomBasePath: () => "",
  useSharedRoomId: () => roomStub.sharedRoomId,
}));

vi.mock("../../lib/api", () => ({
  api: {
    renameSection: vi.fn(),
    createSection: vi.fn(),
    deleteSection: vi.fn(),
  },
}));

const noop = () => {};

// Captures the move clipboard's current cut so a test can assert what the kebab's Cut item put on it.
function ClipboardSpy({ onChange }: { onChange: (cut: MoveClipboardCut | null) => void }) {
  const { cut } = useMoveClipboard();
  onChange(cut);
  return null;
}

function renderRow(parentSectionId: string | null, onCut: (cut: MoveClipboardCut | null) => void) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MoveClipboardProvider>
        <MemoryRouter>
          <ClipboardSpy onChange={onCut} />
          <SectionRow
            id="ambu"
            name="Ambu"
            count={3}
            canNest
            parentSectionId={parentSectionId}
            onDrill={noop}
            onSectionDropBefore={noop}
            onSectionDropNest={noop}
            onRecordingDrop={noop}
          />
        </MemoryRouter>
      </MoveClipboardProvider>
    </QueryClientProvider>,
  );
}

function openKebab() {
  fireEvent.click(screen.getByRole("button", { name: /section actions/i }));
}

describe("SectionRow", () => {
  beforeEach(() => {
    roomStub.sharedRoomId = undefined;
  });

  // A folder is cut from its own parent, not from itself - a later paste check relies on this to detect
  // "pasting back where you cut from".
  it("puts the folder on the clipboard, using its own parent as the source", () => {
    let cut: MoveClipboardCut | null = null;
    renderRow("customers", (c) => (cut = c));
    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /^cut$/i }));
    expect(cut).toEqual({ kind: "folders", ids: ["ambu"], sourceSectionId: "customers", sourceRoomId: null });
  });

  it("records the root as the source for a top-level folder", () => {
    let cut: MoveClipboardCut | null = null;
    renderRow(null, (c) => (cut = c));
    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /^cut$/i }));
    expect(cut).toEqual({ kind: "folders", ids: ["ambu"], sourceSectionId: null, sourceRoomId: null });
  });

  it("records the shared room as the source when browsing one", () => {
    roomStub.sharedRoomId = "eng-room";
    let cut: MoveClipboardCut | null = null;
    renderRow("customers", (c) => (cut = c));
    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /^cut$/i }));
    expect(cut).toEqual({ kind: "folders", ids: ["ambu"], sourceSectionId: "customers", sourceRoomId: "eng-room" });
  });
});
