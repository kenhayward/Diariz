import { describe, it, expect } from "vitest";
import { pasteTarget } from "./pasteTarget";
import { MAX_FOLDER_DEPTH } from "./drillView";
import type { MoveClipboardCut } from "./moveClipboard";
import type { SectionDto } from "./types";

const section = (id: string, name: string, parentId: string | null = null, position = 0): SectionDto =>
  ({ id, name, parentId, position }) as SectionDto;

// Customers > Northwind, a couple of loose top-level folders, and a chain exactly MAX_FOLDER_DEPTH deep for the
// boundary cases (d0 top-level at depth 1 ... the last entry at depth MAX_FOLDER_DEPTH).
const deepChain: SectionDto[] = Array.from({ length: MAX_FOLDER_DEPTH }, (_, i) =>
  section(`d${i}`, `L${i}`, i === 0 ? null : `d${i - 1}`),
);
const sections: SectionDto[] = [
  section("customers", "Customers"),
  section("northwind", "Northwind", "customers"),
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
    expect(pasteTarget({ cut: null, sections, destSectionId: "northwind", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "empty",
    });
  });

  // Defensive: the clipboard context never produces an empty-ids cut, but the rule must stay total.
  it("blocks with 'empty' for a cut with no ids", () => {
    const cut: MoveClipboardCut = { kind: "recordings", ids: [], sourceSectionId: null, sourceRoomId: null };
    expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "empty",
    });
  });

  it("blocks with 'shared-room' when browsing a shared room, regardless of anything else", () => {
    const cut = recordingsCut("customers");
    expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: "room-2" })).toEqual({
      kind: "blocked",
      reason: "shared-room",
    });
  });

  // Precedence: when both conditions hold at once, shared-room must win over same-folder - a same-folder
  // paste at the same drill level would be legal if the room were not shared, so the shared-room block is
  // the operative reason. Pins the order documented in pasteTarget.ts against a silent flip.
  it("blocks with 'shared-room' rather than 'same-folder' when both conditions hold", () => {
    const cut = recordingsCut("customers");
    expect(pasteTarget({ cut, sections, destSectionId: "customers", destRoomId: "room-2" })).toEqual({
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
    expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: null })).toEqual({ kind: "ok" });
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
    expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: null })).toEqual({
      kind: "blocked",
      reason: "into-itself",
    });
  });

  // A grandchild-or-deeper case, not just a direct child: proves the check walks the whole ancestor chain
  // rather than only comparing the destination's immediate parentId (which would wrongly allow this).
  it("blocks with 'into-itself' when the destination is several levels beneath the moved folder", () => {
    const cut = folderCut("d0", null);
    expect(pasteTarget({ cut, sections, destSectionId: "d3", destRoomId: null })).toEqual({
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
    // "customers" has height 2 (itself + northwind). d6 is depth 7, so 7 + 2 = 9 > 8.
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

  // The clipboard survives a room switch by design, so a cut made in a shared room can reach a paste
  // attempt back in the *personal* room - the one destination the shared-room blanket rule below does not
  // touch, since that rule only fires while browsing a shared room (destRoomId !== null). Without this
  // check the paste silently resolves server-side to the personal room: a no-op for someone else's
  // recordings that the client still reads as success and clears the clipboard for, or a real move to the
  // wrong room for your own.
  describe("cross-room", () => {
    it("blocks with 'cross-room' when a cut from a shared room is pasted into the personal room", () => {
      const cut = recordingsCut("customers", "room-2");
      expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: null })).toEqual({
        kind: "blocked",
        reason: "cross-room",
      });
    });

    it("blocks with 'cross-room' for a folder cut from a shared room pasted into the personal room", () => {
      const cut = folderCut("podcasts", null, "room-2");
      expect(pasteTarget({ cut, sections, destSectionId: "customers", destRoomId: null })).toEqual({
        kind: "blocked",
        reason: "cross-room",
      });
    });

    // Two nulls mean the same room (personal to personal) and must stay allowed.
    it("allows a personal-room cut pasted into the personal room (both sides null)", () => {
      const cut = recordingsCut("customers", null);
      expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: null })).toEqual({ kind: "ok" });
    });

    // The other direction: pasting a personal-room cut while browsing a shared room. Deliberate choice -
    // the broader, blanket 'shared-room' reason still wins here, the same "broader reason wins" precedence
    // already used for same-folder above - destRoomId !== null already makes the whole destination
    // off-limits regardless of where the cut came from, so 'cross-room' is reserved for the one gap the
    // blanket rule does not cover: a null (personal) destination.
    it("blocks with 'shared-room', not 'cross-room', when a personal cut is pasted into a shared room", () => {
      const cut = recordingsCut("customers", null);
      expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: "room-2" })).toEqual({
        kind: "blocked",
        reason: "shared-room",
      });
    });

    // Regression: pasting back into the very shared room a cut came from is still blocked today (cut/paste
    // is not wired up for shared rooms yet - see releases.ts) - and must still be blocked for the
    // pre-existing 'shared-room' reason, not newly allowed and not relabeled 'cross-room' just because the
    // source and destination room ids happen to match.
    it("blocks with 'shared-room' when a shared-room cut is pasted back into that same shared room", () => {
      const cut = recordingsCut("customers", "room-2");
      expect(pasteTarget({ cut, sections, destSectionId: "northwind", destRoomId: "room-2" })).toEqual({
        kind: "blocked",
        reason: "shared-room",
      });
    });
  });
});
