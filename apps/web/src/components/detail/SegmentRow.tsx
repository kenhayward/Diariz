import { useTranslation } from "react-i18next";
import SpeakerAssign from "../SpeakerAssign";
import { segmentText } from "../../lib/transcriptView";
import type { MeetingNote, SegmentDto, SpeakerInfo } from "../../lib/types";

/// mm:ss for a transcript row's timestamp. Minutes are zero-padded here, unlike
/// lib/format's formatDuration, so the column stays aligned down the transcript.
function fmt(ms: number): string {
  const s = Math.floor(ms / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

/// A note-taker's note woven into the transcript: same row layout as a segment (timestamp · speaker · text)
/// but the current user is the "speaker" and the text is green, to distinguish it from transcribed speech.
export function NoteRow({ note, speaker }: { note: MeetingNote; speaker: string }) {
  return (
    <li className="flex items-start gap-3 rounded-lg border border-green-200 bg-green-50/40 px-4 py-2 dark:border-green-900 dark:bg-green-950/20">
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400 dark:text-gray-500">
        {note.capturedAtMs != null ? fmt(note.capturedAtMs) : ""}
      </span>
      <span className="w-28 shrink-0 text-sm font-medium text-green-700 dark:text-green-400">{speaker}</span>
      <span className="flex-1 whitespace-pre-wrap break-words text-sm text-green-700 dark:text-green-400">{note.text}</span>
    </li>
  );
}

/// What a transcript row needs to offer the Speakers-tab assignment typeahead in its speaker column, so a
/// speaker can be named while reading/playing the transcript. Omitted (Speakers tab, where the speaker row
/// above already carries the typeahead) → the row shows a plain label as before.
export type SegmentAssign = {
  infoOf: (label: string) => SpeakerInfo | undefined;
  onAssign: (label: string, profileId: string | null) => void | Promise<void>;
  onCreate: (label: string, name: string) => void | Promise<void>;
  onMulti: (label: string) => void | Promise<void>;
};

export default function SegmentRow({
  seg,
  speakerName,
  assign,
  active,
  selected,
  selectMode,
  showOriginal,
  onClick,
}: {
  seg: SegmentDto;
  /// The speaker name to show (localised "Multiple Speakers" overrides the server display).
  speakerName: string;
  assign?: SegmentAssign;
  /// Currently playing (highlighted by the audio position).
  active: boolean;
  /// Picked in the transcript selection (drives the toolbar's bulk actions).
  selected: boolean;
  selectMode: boolean;
  showOriginal: boolean;
  /// Click anywhere on the row: select it (single, or toggle in Select mode). No longer auto-plays.
  onClick: () => void;
}) {
  const { t } = useTranslation("workspace");
  const revised = seg.revised != null;
  return (
    <li
      id={`seg-${seg.id}`}
      onClick={onClick}
      className={`flex cursor-pointer items-start gap-3 rounded-lg border px-4 py-2 hover:bg-gray-50 dark:hover:bg-gray-800 ${
        selected
          ? "border-blue-400 bg-blue-50 ring-1 ring-blue-300 dark:border-blue-600 dark:bg-blue-900/30 dark:ring-blue-700"
          : active
            ? "border-blue-300 bg-blue-50 dark:border-blue-700 dark:bg-blue-900/30"
            : "border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900"
      }`}
    >
      {selectMode && (
        // Visual only — the whole row is the click target, which toggles selection.
        <input type="checkbox" checked={selected} readOnly tabIndex={-1} aria-hidden className="mt-1 shrink-0 pointer-events-none" />
      )}
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400 dark:text-gray-500">{fmt(seg.startMs)}</span>
      {assign ? (
        // The typeahead is a click target inside a clickable row, so it stops propagation (which would
        // otherwise select the segment). Closed, it renders as one button - cheap enough per row.
        <div className="shrink-0" onClick={(e) => e.stopPropagation()}>
          <SpeakerAssign
            label={seg.speaker}
            width="w-40"
            subtle
            displayName={speakerName}
            isMulti={assign.infoOf(seg.speaker)?.isMultiSpeaker ?? false}
            onAssign={(profileId) => assign.onAssign(seg.speaker, profileId)}
            onCreate={(name) => assign.onCreate(seg.speaker, name)}
            onMulti={() => assign.onMulti(seg.speaker)}
          />
        </div>
      ) : (
        <span className="w-28 shrink-0 text-sm font-medium text-gray-700 dark:text-gray-200">{speakerName}</span>
      )}
      {/* Auto-expands vertically to show the full (possibly merged) block of text. */}
      <span className="flex-1 whitespace-pre-wrap break-words text-sm dark:text-gray-200">
        {segmentText(seg, showOriginal)}
      </span>
      {/* The currently-playing row gets a small ▶ marker (distinct from the selection highlight). */}
      {active && <span aria-hidden className="mt-0.5 shrink-0 text-blue-500 dark:text-blue-400">▶</span>}
      {/* Marks a segment whose text has been edited or translated (a revision exists). */}
      {revised && (
        <span
          title={t("revisedTitle")}
          aria-label={t("editedAria")}
          className="mt-0.5 shrink-0 text-teal-500 dark:text-teal-400"
        >
          ✎
        </span>
      )}
    </li>
  );
}
