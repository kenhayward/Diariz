import { useTranslation } from "react-i18next";
import SpeakerAssign from "../SpeakerAssign";
import ToolbarButton from "../ToolbarButton";
import { PencilIcon, PlayIcon, PauseIcon, TrashIcon } from "./icons";
import { contactSummary } from "../SpeakerContactCard";
import { formatDuration } from "../../lib/format";
import type { SpeakerInfo } from "../../lib/types";

export default function SpeakerRow({
  label,
  info,
  initial,
  count,
  durationMs,
  canPlay,
  playing,
  selected,
  onSelect,
  onTogglePlay,
  onDelete,
  onAssign,
  onCreate,
  onMulti,
  canManagePeople,
  onEditPerson,
}: {
  label: string;
  info: SpeakerInfo | undefined;
  initial: string;
  count: number;
  durationMs: number;
  canPlay: boolean;
  playing: boolean;
  selected: boolean;
  onSelect: () => void;
  onTogglePlay: () => void;
  onDelete: (name: string) => void;
  onAssign: (profileId: string | null) => void;
  onCreate: (name: string) => void;
  onMulti: () => void;
  canManagePeople: boolean;
  onEditPerson: () => void;
}) {
  const { t } = useTranslation("workspace");
  // The display name for the per-speaker action labels (the assignment typeahead owns the editing UI).
  const name = initial;
  // The interactive controls (assign box, play, delete) sit inside the clickable row, so they stop event
  // propagation to avoid toggling the row's selection when used.
  const stop = (e: { stopPropagation: () => void }) => e.stopPropagation();

  // One line per speaker: [label · auto] · assign typeahead · segment count · toolbar (play/delete). The whole
  // row is a button that toggles the speaker's segment table below the list; the leading label column is
  // fixed-width so the assign box and the items after it line up across rows.
  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={selected}
      aria-label={t("selectSpeakerAria", { name })}
      onClick={onSelect}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onSelect();
        }
      }}
      className={`flex cursor-pointer flex-wrap items-center gap-2 rounded-lg border px-2 py-1 hover:bg-gray-50 dark:hover:bg-gray-800 ${
        selected
          ? "border-blue-400 bg-blue-50 ring-1 ring-blue-300 dark:border-blue-600 dark:bg-blue-900/30 dark:ring-blue-700"
          : "border-transparent"
      }`}
    >
      <div className="flex w-32 shrink-0 items-center gap-1">
        <span className="text-xs text-gray-400 dark:text-gray-500">{label}</span>
        {info?.identifiedAuto && (
          <span
            title={t("autoNameTitle")}
            className="rounded bg-blue-100 px-1 text-[10px] font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
          >
            {t("autoBadge")}
          </span>
        )}
      </div>
      <div onClick={stop} onKeyDown={stop}>
        <SpeakerAssign
          label={label}
          // The trigger reads its current label from here. Without it every identified speaker showed
          // "Unassigned" while the row's own badges said otherwise.
          //
          // Only when it differs from the raw label: this row already prints SPEAKER_nn in the column to
          // the left, so echoing it in the dropdown would say nothing. The transcript row passes its name
          // unconditionally on purpose - there the trigger replaces the speaker label itself.
          displayName={info && info.displayName !== label ? info.displayName : undefined}
          isMulti={info?.isMultiSpeaker ?? false}
          onAssign={onAssign}
          onCreate={onCreate}
          onMulti={onMulti}
        />
      </div>
      {/* Who this person is, when we know. Deliberately absent for an anonymous speaker and for a
          "Multiple Speakers" slot - the server sends null for both, and inventing something here would
          claim more than Diariz knows. */}
      {info?.personId && !info.isMultiSpeaker && (info.title || info.companyName || info.isInternal !== null) && (
        <span className="flex min-w-0 shrink items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
          {(info.title || info.companyName) && (
            <span className="truncate">
              {info.title && info.companyName
                ? t("speakerAt", { title: info.title, company: info.companyName })
                : (info.title ?? info.companyName)}
            </span>
          )}
          {info.isInternal !== null && (
            <span
              // The row has room for a job title and a company and no more, so the rest of the person hangs
              // off the chip. Same text as the contact card below, from one function, so they cannot drift.
              title={contactSummary(info, {
                internal: t("speakerInternal"),
                external: t("speakerExternal"),
                none: t("speakerNoContactDetails"),
              })}
              className={`shrink-0 cursor-help rounded px-1 text-[10px] font-medium ${
                info.isInternal
                  ? "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300"
                  : "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
              }`}
            >
              {info.isInternal ? t("speakerInternal") : t("speakerExternal")}
            </span>
          )}
        </span>
      )}
      <span className="w-40 shrink-0 whitespace-nowrap text-xs text-gray-500 dark:text-gray-400">
        {t("speakerSegmentCount", { count })} · {formatDuration(durationMs)}
      </span>
      <div className="flex items-center gap-0.5" onClick={stop} onKeyDown={stop}>
        {/* Correcting who someone is belongs where you noticed they were wrong. Only for a real person - an
            anonymous speaker and a multi-speaker slot have no record to edit - and only with the permission
            the endpoint behind it enforces. */}
        {canManagePeople && info?.personId && !info.isMultiSpeaker && (
          <ToolbarButton label={t("editPersonAction", { name })} icon={PencilIcon} onClick={onEditPerson} />
        )}
        {canPlay && (
          <ToolbarButton
            label={playing ? t("pauseSpeaker") : t("playSpeaker", { label: name })}
            icon={playing ? PauseIcon : PlayIcon}
            active={playing}
            onClick={onTogglePlay}
          />
        )}
        <ToolbarButton label={t("deleteSpeaker", { label: name })} icon={TrashIcon} onClick={() => onDelete(name)} />
      </div>
    </div>
  );
}
