/// The pure paste-validation rule: given a cut clipboard and the drill level currently open, may the user
/// paste here, and if not why. Kept free of React so the awkward cases - depth boundaries, cycles - are
/// cheap to test exhaustively. Returns a reason *code*, never user-facing text; a later layer maps codes to
/// translated messages.
///
/// The `too-deep` and `into-itself` rules mirror ones the API already enforces in
/// `SectionsController.Reorder` (`depth(target) + height(movedBranch) <= MAX_FOLDER_DEPTH`, and the target
/// may not be the moved folder nor anything beneath it) - the client must agree with the server exactly, or
/// a move the UI allows becomes a 400, or a move the UI blocks was actually legal.

import { breadcrumbOf, depthOf, heightOf, MAX_FOLDER_DEPTH } from "./drillView";
import type { MoveClipboardCut } from "./moveClipboard";
import type { SectionDto } from "./types";

export type PasteBlockedReason = "same-folder" | "shared-room" | "too-deep" | "into-itself" | "empty";

export type PasteTargetResult = { kind: "ok" } | { kind: "blocked"; reason: PasteBlockedReason };

export interface PasteTargetArgs {
  /// The clipboard's current cut, or null when nothing has been cut.
  cut: MoveClipboardCut | null;
  /// Every folder in the room currently being browsed (the destination room, not necessarily the cut's
  /// source room).
  sections: SectionDto[];
  /// The drill level currently open - the only legal paste destination. Null is the root (Ungrouped).
  destSectionId: string | null;
  /// The room currently being browsed. Null is the personal room, matching `MoveClipboardCut.sourceRoomId`'s
  /// convention.
  destRoomId: string | null;
}

export function pasteTarget(args: PasteTargetArgs): PasteTargetResult {
  const { cut, sections, destSectionId, destRoomId } = args;

  if (!cut || cut.ids.length === 0) return { kind: "blocked", reason: "empty" };

  // Blanket rule, independent of source: browsing any shared room disables paste outright. Checked before
  // same-folder deliberately - shared-room is a property of *where you are* (the whole destination is
  // off-limits), while same-folder is a property of *what you picked* (this particular spot is a no-op).
  // The broader, more explanatory reason should win: telling someone their paste is a no-op when the real
  // problem is that they can't paste here at all would be misleading. No server rule to appeal to here, this
  // is purely a client UX call (see pasteTarget.test.ts for the overlap case).
  if (destRoomId !== null) return { kind: "blocked", reason: "shared-room" };

  if (destSectionId === cut.sourceSectionId) return { kind: "blocked", reason: "same-folder" };

  if (cut.kind === "folders") {
    // Cycle check first, matching Reorder's order: the target may not be the moved folder itself or any of
    // its descendants. breadcrumbOf(destSectionId) is the ancestor chain root-first ending at the
    // destination itself, so a moved id appearing anywhere in it means the destination is that folder or
    // beneath it. Pasting to root yields an empty chain, so root is never "into itself".
    const destChain = breadcrumbOf(sections, destSectionId);
    for (const id of cut.ids) {
      if (destChain.some((s) => s.id === id)) return { kind: "blocked", reason: "into-itself" };
    }

    // Depth check: a move carries the folder's whole branch, so the target's depth plus the branch's height
    // is what must fit within MAX_FOLDER_DEPTH.
    const targetDepth = depthOf(sections, destSectionId);
    for (const id of cut.ids) {
      if (targetDepth + heightOf(sections, id) > MAX_FOLDER_DEPTH) return { kind: "blocked", reason: "too-deep" };
    }
  }

  return { kind: "ok" };
}
