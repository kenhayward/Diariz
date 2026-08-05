import { useId } from "react";
import { useTranslation } from "react-i18next";
import { useMoveClipboard } from "../../lib/moveClipboard";
import { pasteTarget, type PasteBlockedReason } from "../../lib/pasteTarget";
import type { SectionDto } from "../../lib/types";

/// Persistent bar under the toolbar that carries the move clipboard: "cut here, drill elsewhere, paste"
/// means a toolbar button would be out of the user's attention by the time paste matters. It names the
/// destination, doubles as the paste control, and gives Cancel a home. Renders nothing when the
/// clipboard is empty - there is nothing to carry and nothing to paste.
///
/// When `pasteTarget` blocks the paste, the control stays **visibly disabled with the reason shown**,
/// never hidden - the clipboard survives a room switch, so "cut in your personal room, drill into a
/// shared room" is a few clicks away, and a vanished control there would read as a broken app. This
/// component owns the mapping from `pasteTarget`'s reason codes to translated messages; `pasteTarget`
/// itself stays free of prose so it can be tested exhaustively without a translation catalogue.
///
/// Does not perform the paste itself - it only calls `onPaste`. Mounted in `RecordingsPanel.tsx`, right
/// after `DrillBreadcrumb`, whose `pasteClipboard()` is the actual move: the bulk recordings endpoint or
/// `reorderSections` depending on the clipboard's kind (see that function for the ordering rule and the
/// keep-the-clipboard-on-failure behaviour).
export default function ClipboardBar({
  sections,
  destSectionId,
  destRoomId,
  onPaste,
}: {
  /// Every folder in the room currently being browsed - passed straight through to `pasteTarget` and
  /// also used to look up the destination's display name.
  sections: SectionDto[];
  /// The drill level currently open - the only legal paste destination. Null is the root (Ungrouped).
  destSectionId: string | null;
  /// The room currently being browsed. Null is the personal room.
  destRoomId: string | null;
  onPaste: () => void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const { cut, clear } = useMoveClipboard();
  const reasonId = useId();

  if (!cut) return null;

  const result = pasteTarget({ cut, sections, destSectionId, destRoomId });
  const destName =
    destSectionId === null
      ? t("ungrouped")
      : (sections.find((s) => s.id === destSectionId)?.name ?? t("ungrouped"));

  const countLabel =
    cut.kind === "recordings"
      ? t("clipboardBarRecordings", { count: cut.ids.length })
      : t("clipboardBarFolder");

  const blocked = result.kind === "blocked" ? BLOCKED_MESSAGE_KEYS[result.reason] : null;
  const blockedMessage = blocked ? t(blocked) : null;

  return (
    <div className="flex flex-wrap items-center gap-2 border-b px-2 py-2 text-sm dark:border-gray-800">
      <span className="text-gray-700 dark:text-gray-300">{countLabel}</span>
      <button
        type="button"
        onClick={onPaste}
        disabled={result.kind === "blocked"}
        aria-describedby={blockedMessage ? reasonId : undefined}
        className="rounded border px-2 py-1 font-medium text-blue-700 hover:bg-blue-50 disabled:cursor-not-allowed disabled:border-gray-200 disabled:text-gray-400 disabled:hover:bg-transparent dark:border-blue-800 dark:text-blue-300 dark:hover:bg-blue-900/30 dark:disabled:border-gray-700 dark:disabled:text-gray-500"
      >
        {t("clipboardBarPasteInto", { name: destName })}
      </button>
      {blockedMessage && (
        <span id={reasonId} className="text-xs text-gray-500 dark:text-gray-400">
          {blockedMessage}
        </span>
      )}
      <button
        type="button"
        onClick={clear}
        className="rounded px-2 py-1 text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
      >
        {t("common:cancel")}
      </button>
    </div>
  );
}

const BLOCKED_MESSAGE_KEYS: Record<PasteBlockedReason, string> = {
  "same-folder": "clipboardBarBlockedSameFolder",
  "shared-room": "clipboardBarBlockedSharedRoom",
  "cross-room": "clipboardBarBlockedCrossRoom",
  "too-deep": "clipboardBarBlockedTooDeep",
  "into-itself": "clipboardBarBlockedIntoItself",
  // `pasteTarget` returns this reason on `!cut || cut.ids.length === 0` - two separate conditions. This
  // component's own guard above (`if (!cut) return null`) only closes the first one: a non-null cut with
  // an empty `ids` array still reaches this map and renders a disabled Paste control with this message.
  // That currently can't happen only because every caller of `cutRecordings`/`cutFolder` guards against
  // an empty selection before calling it (e.g. `RecordingsPanel.tsx`'s cut button is disabled when
  // nothing is selected) - an invariant that lives outside this component and outside `pasteTarget`, not
  // something this file enforces. Mapped (and covered by a render test below) so a future caller that
  // skips that guard, or a `cutFolder` against a since-deleted id, still shows a real message instead of
  // a blank branch.
  empty: "clipboardBarBlockedEmpty",
};
