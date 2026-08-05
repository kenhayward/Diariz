import { describe, it, expect } from "vitest";
import { pasteTarget } from "./pasteTarget";
import { MAX_FOLDER_DEPTH } from "./drillView";
import type { MoveClipboardCut } from "./moveClipboard";
import type { SectionDto } from "./types";

const section = (id: string, name: string, parentId: string | null = null, position = 0): SectionDto =>
  ({ id, name, parentId, position }) as SectionDto;

// Customers > Ambu, a couple of loose top-level folders, and a chain exactly MAX_FOLDER_DEPTH deep for the
// boundary cases (d0 top-level at depth 1 ... the last entry at depth MAX_FOLDER_DEPTH).
const deepChain: SectionDto[] = Array.from({ length: MAX_FOLDER_DEPTH }, (_, i) =>
  section(`d${i}`, `L${i}`, i === 0 ? null : `d${i - 1}`),
);
const sections: SectionDto[] = [
  section("customers", "Customers"),
  section("ambu", "Ambu", "customers"),
  section("podcasts", "Podcasts", null, 1),
  section("loose", "Loose", null, 2),
  ...deepChain,
];

const recordingsCut = (sourceSectionId: string | null, sourceRoomId: string | null = null): MoveClipboardCut => ({
  kind: "recordings",
  ids: ["r1", "r2"],
  sourceSectionId,
  sourceRoomId,
});

const folderCut = (id: string, sourceSectionId: string | null, sourceRoomId: string | null = null): MoveClipboardCut => ({
  kind: "folders",
  ids: [id],
  sourceSectionId,
  sourceRoomId,
});

describe("pasteTarget", () => {
  it("blocks with 'empty' when nothing has been cut", () => {
    expect(pasteTarget({ cut: null, sections, destSectionId: "ambu", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "empty",
    });
  });

  // Defensive: the clipboard context never produces an empty-ids cut, but the rule must stay total.
  it("blocks with 'empty' for a cut with no ids", () => {
    const cut: MoveClipboardCut = { kind: "recordings", ids: [], sourceSectionId: null, sourceRoomId: null };
    expect(pasteTarget({ cut, sections, destSectionId: "ambu", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "empty",
    });
  });

  it("blocks with 'shared-room' when browsing a shared room, regardless of anything else", () => {
    const cut = recordingsCut("customers");
    expect(pasteTarget({ cut, sections, destSectionId: "ambu", destRoomId: "room-2" })).toEqual({
      kind: "blocked",
      reason: "shared-room",
    });
  });

  it("blocks with 'same-folder' when the destination is where the recordings were cut from", () => {
    const cut = recordingsCut("customers");
    expect(pasteTarget({ cut, sections, destSectionId: "customers", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "same-folder",
    });
  });

  it("blocks with 'same-folder' at the root, cut from and pasted back to root", () => {
    const cut = recordingsCut(null);
    expect(pasteTarget({ cut, sections, destSectionId: null, destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "same-folder",
    });
  });

  it("allows recordings into a different folder", () => {
    const cut = recordingsCut("customers");
    expect(pasteTarget({ cut, sections, destSectionId: "ambu", destRoomId: null })).toEqual({ kind: "ok" });
  });

  it("allows recordings into root", () => {
    const cut = recordingsCut("customers");
    expect(pasteTarget({ cut, sections, destSectionId: null, destRoomId: null })).toEqual({ kind: "ok" });
  });

  it("allows a folder into a legal parent", () => {
    const cut = folderCut("podcasts", null);
    expect(pasteTarget({ cut, sections, destSectionId: "customers", destRoomId: null })).toEqual({ kind: "ok" });
  });

  it("blocks with 'into-itself' when pasting a folder into itself", () => {
    const cut = folderCut("customers", null);
    expect(pasteTarget({ cut, sections, destSectionId: "customers", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "into-itself",
    });
  });

  it("blocks with 'into-itself' when pasting a folder into its own descendant", () => {
    const cut = folderCut("customers", null);
    expect(pasteTarget({ cut, sections, destSectionId: "ambu", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "into-itself",
    });
  });

  it("blocks with 'too-deep' when a leaf folder would land past depth 8", () => {
    // d7 (the last entry) sits at depth 8 already; one more level would be 9.
    const cut = folderCut("loose", null);
    expect(pasteTarget({ cut, sections, destSectionId: "d7", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "too-deep",
    });
  });

  // The boundary that must be allowed: a height-1 folder into a depth-7 target lands at exactly 8.
  it("allows a height-1 folder pasted into a depth-7 target", () => {
    // d6 sits at depth 7 (index 6 -> depth i+1).
    const cut = folderCut("loose", null);
    expect(pasteTarget({ cut, sections, destSectionId: "d6", destRoomId: null })).toEqual({ kind: "ok" });
  });

  it("blocks with 'too-deep' for a tall branch even though the target itself is shallow", () => {
    // "customers" has height 2 (itself + ambu). d6 is depth 7, so 7 + 2 = 9 > 8.
    const cut = folderCut("customers", null);
    expect(pasteTarget({ cut, sections, destSectionId: "d6", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "too-deep",
    });
  });

  it("allows a tall branch into a shallower target that still fits exactly", () => {
    // "customers" has height 2. d5 is depth 6, so 6 + 2 = 8, exactly at the cap.
    const cut = folderCut("customers", null);
    expect(pasteTarget({ cut, sections, destSectionId: "d5", destRoomId: null })).toEqual({ kind: "ok" });
  });
});
