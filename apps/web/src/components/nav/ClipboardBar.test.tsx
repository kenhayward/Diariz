import { useEffect } from "react";
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ClipboardBar from "./ClipboardBar";
import { MoveClipboardProvider, useMoveClipboard, type MoveClipboardCut } from "../../lib/moveClipboard";
import type { SectionDto } from "../../lib/types";

// Seeds the clipboard on mount, before ClipboardBar's own render, so each test can start from a chosen
// cut without going through a real Cut affordance (those are covered by SectionRow.test.tsx and
// RecordingsPanel.test.tsx already).
function Seeder({ seed }: { seed: (clipboard: ReturnType<typeof useMoveClipboard>) => void }) {
  const clipboard = useMoveClipboard();
  useEffect(() => {
    seed(clipboard);
    // Only ever seed once per test - the clipboard setters are stable identities from useMemo, but the
    // callback itself is deliberately not a dependency (each test supplies a fresh, single-shot seed).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return null;
}

// Reports the clipboard's current cut on every render (not just on mount), so a test can observe a later
// change (e.g. Cancel clearing it) rather than a value frozen from the first render.
function ClipboardSpy({ onChange }: { onChange: (cut: MoveClipboardCut | null) => void }) {
  const { cut } = useMoveClipboard();
  onChange(cut);
  return null;
}

const sections: SectionDto[] = [
  { id: "falcon", name: "Project Falcon", parentId: null, position: 0 },
  { id: "customers", name: "Customers", parentId: null, position: 1 },
  { id: "northwind", name: "Northwind", parentId: "customers", position: 0 },
];

function renderBar(opts: {
  seed: (clipboard: ReturnType<typeof useMoveClipboard>) => void;
  destSectionId: string | null;
  destRoomId: string | null;
  onPaste?: () => void;
}) {
  const onPaste = opts.onPaste ?? vi.fn();
  let latestCut: MoveClipboardCut | null = null;
  const utils = render(
    <MoveClipboardProvider>
      <Seeder seed={opts.seed} />
      <ClipboardSpy onChange={(cut) => (latestCut = cut)} />
      <ClipboardBar
        sections={sections}
        destSectionId={opts.destSectionId}
        destRoomId={opts.destRoomId}
        onPaste={onPaste}
      />
    </MoveClipboardProvider>,
  );
  return { ...utils, onPaste, getCut: () => latestCut };
}

const noSeed = () => {};

describe("ClipboardBar", () => {
  it("renders nothing when the clipboard is empty", () => {
    const { container } = renderBar({ seed: noSeed, destSectionId: "falcon", destRoomId: null });
    expect(container.innerHTML).toBe("");
  });

  it("shows a singular count for one recording and the destination name", () => {
    renderBar({
      seed: (c) => c.cutRecordings(["r1"], "customers", null),
      destSectionId: "falcon",
      destRoomId: null,
    });
    expect(screen.getByText("1 recording ready to move")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Paste into Project Falcon" })).toBeTruthy();
  });

  it("shows a plural count for several recordings", () => {
    renderBar({
      seed: (c) => c.cutRecordings(["r1", "r2", "r3"], "customers", null),
      destSectionId: "falcon",
      destRoomId: null,
    });
    expect(screen.getByText("3 recordings ready to move")).toBeTruthy();
  });

  it("shows the folder count for a folder cut", () => {
    renderBar({
      seed: (c) => c.cutFolder("northwind", "customers", null),
      destSectionId: "falcon",
      destRoomId: null,
    });
    expect(screen.getByText("1 folder ready to move")).toBeTruthy();
  });

  it("names the destination as Ungrouped at the root", () => {
    renderBar({
      seed: (c) => c.cutRecordings(["r1"], "customers", null),
      destSectionId: null,
      destRoomId: null,
    });
    expect(screen.getByRole("button", { name: "Paste into Ungrouped" })).toBeTruthy();
  });

  it("calls onPaste, and only onPaste, when Paste is clicked", () => {
    const { onPaste, getCut } = renderBar({
      seed: (c) => c.cutRecordings(["r1"], "customers", null),
      destSectionId: "falcon",
      destRoomId: null,
    });
    fireEvent.click(screen.getByRole("button", { name: "Paste into Project Falcon" }));
    expect(onPaste).toHaveBeenCalledTimes(1);
    // The clipboard must still hold the cut - ClipboardBar performs no paste and no clear of its own.
    expect(getCut()).not.toBeNull();
  });

  it("clears the clipboard when Cancel is clicked, without calling onPaste", () => {
    const { onPaste, getCut } = renderBar({
      seed: (c) => c.cutRecordings(["r1"], "customers", null),
      destSectionId: "falcon",
      destRoomId: null,
    });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(getCut()).toBeNull();
    expect(onPaste).not.toHaveBeenCalled();
    // An emptied clipboard means an empty bar - the same "renders nothing" rule applies after Cancel as
    // it does on first mount.
    expect(screen.queryByRole("button", { name: /paste into/i })).toBeNull();
  });

  it("disables Paste and shows the reason for the same folder", () => {
    renderBar({
      seed: (c) => c.cutRecordings(["r1"], "falcon", null),
      destSectionId: "falcon",
      destRoomId: null,
    });
    const button = screen.getByRole("button", { name: "Paste into Project Falcon" }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByText("This is already the current location.")).toBeTruthy();
  });

  it("disables Paste and shows the reason for a shared room", () => {
    renderBar({
      seed: (c) => c.cutRecordings(["r1"], "customers", null),
      destSectionId: "falcon",
      destRoomId: "eng-room",
    });
    const button = screen.getByRole("button", { name: "Paste into Project Falcon" }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByText("Can't paste into a shared room.")).toBeTruthy();
  });

  it("disables Paste and shows the reason for a cut from a shared room pasted into the personal room", () => {
    renderBar({
      seed: (c) => c.cutRecordings(["r1"], "customers", "eng-room"),
      destSectionId: "falcon",
      destRoomId: null,
    });
    const button = screen.getByRole("button", { name: "Paste into Project Falcon" }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByText("This was cut from a different room.")).toBeTruthy();
  });

  it("disables Paste and shows the reason when a folder move goes too deep", () => {
    const deepSections: SectionDto[] = [
      { id: "d0", name: "D0", parentId: null, position: 0 },
      { id: "d1", name: "D1", parentId: "d0", position: 0 },
      { id: "d2", name: "D2", parentId: "d1", position: 0 },
      { id: "d3", name: "D3", parentId: "d2", position: 0 },
      { id: "d4", name: "D4", parentId: "d3", position: 0 },
      { id: "d5", name: "D5", parentId: "d4", position: 0 },
      { id: "d6", name: "D6", parentId: "d5", position: 0 },
      { id: "d7", name: "D7", parentId: "d6", position: 0 },
      { id: "moved", name: "Moved", parentId: null, position: 1 },
    ];
    const utils = render(
      <MoveClipboardProvider>
        <Seeder seed={(c) => c.cutFolder("moved", null, null)} />
        <ClipboardBar
          sections={deepSections}
          destSectionId="d7"
          destRoomId={null}
          onPaste={vi.fn()}
        />
      </MoveClipboardProvider>,
    );
    const button = screen.getByRole("button", { name: "Paste into D7" }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByText("This would nest folders too deep.")).toBeTruthy();
    utils.unmount();
  });

  it("disables Paste and shows the reason when pasting a folder into itself", () => {
    renderBar({
      seed: (c) => c.cutFolder("customers", null, null),
      destSectionId: "northwind",
      destRoomId: null,
    });
    const button = screen.getByRole("button", { name: "Paste into Northwind" }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByText("Can't paste a folder into itself.")).toBeTruthy();
  });

  // pasteTarget returns "empty" on `!cut || cut.ids.length === 0` - two separate conditions. This
  // component's own guard only closes the first (a null cut). A non-null cut with an empty ids array -
  // which cannot happen through the app's own Cut affordances today, but is only prevented by a guard
  // outside this component (RecordingsPanel disables its Cut button on an empty selection) - must still
  // reach a real, disabled, explained Paste control rather than a blank branch.
  it("disables Paste and shows the reason when the cut carries no ids", () => {
    renderBar({
      seed: (c) => c.cutRecordings([], "customers", null),
      destSectionId: "falcon",
      destRoomId: null,
    });
    const button = screen.getByRole("button", { name: "Paste into Project Falcon" }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByText("Nothing to paste.")).toBeTruthy();
  });

  it("associates the blocked reason with the Paste control for assistive tech", () => {
    renderBar({
      seed: (c) => c.cutRecordings(["r1"], "customers", null),
      destSectionId: "falcon",
      destRoomId: "eng-room",
    });
    const button = screen.getByRole("button", { name: "Paste into Project Falcon" });
    const describedBy = button.getAttribute("aria-describedby");
    expect(describedBy).toBeTruthy();
    const reasonEl = document.getElementById(describedBy!);
    expect(reasonEl?.textContent).toBe("Can't paste into a shared room.");
  });
});
