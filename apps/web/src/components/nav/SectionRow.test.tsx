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

function renderRow(
  parentSectionId: string | null,
  onCut: (cut: MoveClipboardCut | null) => void,
  opts: { cut?: boolean } = {},
) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MoveClipboardProvider>
        <MemoryRouter>
          <ClipboardSpy onChange={onCut} />
          <SectionRow
            id="northwind"
            name="Northwind"
            count={3}
            canNest
            parentSectionId={parentSectionId}
            cut={opts.cut}
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
  fireEvent.click(screen.getByRole("button", { name: /folder actions/i }));
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
    expect(cut).toEqual({ kind: "folders", ids: ["northwind"], sourceSectionId: "customers", sourceRoomId: null });
  });

  it("records the root as the source for a top-level folder", () => {
    let cut: MoveClipboardCut | null = null;
    renderRow(null, (c) => (cut = c));
    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /^cut$/i }));
    expect(cut).toEqual({ kind: "folders", ids: ["northwind"], sourceSectionId: null, sourceRoomId: null });
  });

  // Pasting into a shared room is disabled, and so is pasting a shared-room cut anywhere else - so a cut
  // made in a shared room would have nowhere at all to go. Rather than let someone stage one and then find
  // every destination refused, Cut is disabled at source with the same reason shown.
  it("disables Cut in a shared room, with the reason on the item", () => {
    roomStub.sharedRoomId = "eng-room";
    let cut: MoveClipboardCut | null = null;
    renderRow("customers", (c) => (cut = c));
    openKebab();

    const item = screen.getByRole("menuitem", { name: /^cut$/i }) as HTMLButtonElement;
    expect(item.disabled).toBe(true);
    expect(item.title).toMatch(/personal room/i);
    fireEvent.click(item);
    expect(cut).toBeNull(); // and clicking it does nothing
  });

  // Cut is a pending state, not a removal - the row stays, greyed with a dashed outline, so cancelling or
  // navigating away leaves no ambiguity about whether the move actually happened. An `outline`, not a
  // `border`: this row already carries its own `border-b dark:border-gray-800`, and a second border-colour
  // utility would fight that one for the same CSS property with the winner decided by stylesheet
  // generation order rather than anything visible here.
  it("greys out the row with a dashed outline when it is the clipboard's cut folder", () => {
    renderRow("customers", noop, { cut: true });
    const row = screen.getByText("Northwind").closest("div")!;
    expect(row.className).toContain("opacity-50");
    expect(row.className).toContain("outline-dashed");
  });

  it("does not grey out the row when it has not been cut", () => {
    renderRow("customers", noop);
    const row = screen.getByText("Northwind").closest("div")!;
    expect(row.className).not.toContain("opacity-50");
    expect(row.className).not.toContain("outline-dashed");
  });

  // Colour/opacity alone would leave a screen-reader user unable to tell WHICH folder is cut - the
  // clipboard bar's own count only says something is cut, never which row.
  it("carries a non-visual cue for a cut folder, for screen readers", () => {
    renderRow("customers", noop, { cut: true });
    expect(screen.getByText("Cut, pending paste")).toBeTruthy();
  });

  it("carries no cut cue when the folder has not been cut", () => {
    renderRow("customers", noop);
    expect(screen.queryByText("Cut, pending paste")).toBeNull();
  });
});
