import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { RecordingDetail } from "../../lib/types";
import { formatDurationApprox, formatLongDate, formatTimeHm } from "../../lib/format";
import { retentionDaysLeft } from "../../lib/audioRetention";
import MeetingTypeMenu from "../MeetingTypeMenu";
import RoomBadge from "../RoomBadge";
import ToolbarButton, { iconProps } from "../ToolbarButton";
import DetailChip from "./DetailChip";
import { SpeakerAvatarStack } from "./SpeakerAvatar";
import {
  AudioIcon,
  CalendarIcon,
  ChevronRightIcon,
  ClockIcon,
  GlobeIcon,
  MinutesGlyph,
  UserIcon,
} from "./SectionIcons";

/// The hub's hero: what the recording *is*, in one card. The meeting-type chip (which drives the minutes
/// template and the formulas offered for it), a scannable row of detail chips, and the summary shown
/// **inline** — not behind a hover or a click, which hid it from touch and made it undiscoverable.
///
/// This card absorbs the old Overview tab: its date/time/duration/audio/retention/recorded-by/rooms
/// list became the chip row, and its Edit-summary / Re-summarise toolbar buttons moved here.

/// Long summaries expand rather than truncating silently.
const CLAMP_CHARS = 420;

const PencilIcon = (
  <svg {...iconProps} width={16} height={16}>
    <path d="M12 20h9" />
    <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4 12.5-12.5z" />
  </svg>
);
const RefreshIcon = (
  <svg {...iconProps} width={16} height={16}>
    <path d="M23 4v6h-6" />
    <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
  </svg>
);

export default function HeroSummaryCard({
  rec,
  speakerNameOf,
  minutesRunning,
  hasTranscript,
  isSummarizing,
  showRooms,
  onOpenMinutes,
  onApplyMeetingType,
  onEditSummary,
  onResummarise,
}: {
  rec: RecordingDetail;
  speakerNameOf: (label: string) => string;
  minutesRunning: boolean;
  hasTranscript: boolean;
  isSummarizing: boolean;
  /// Rooms are hidden while viewing in a shared room (the chip would just echo where you already are).
  showRooms: boolean;
  onOpenMinutes: () => void;
  onApplyMeetingType: (typeId: string) => void;
  onEditSummary: () => void;
  onResummarise: () => void;
}) {
  const { t, i18n } = useTranslation(["workspace", "recordings"]);
  const [expanded, setExpanded] = useState(false);

  const summary = rec.summary?.text ?? "";
  const isLong = summary.length > CLAMP_CHARS;
  const shown = isLong && !expanded ? `${summary.slice(0, CLAMP_CHARS).trimEnd()}…` : summary;

  const daysLeft = rec.hasAudio && !rec.audioProtectedAt ? retentionDaysLeft(rec.audioScheduledDeletionAt, new Date()) : null;
  const labels = rec.speakers.map((s) => s.label);
  const rooms = showRooms ? rec.rooms ?? [] : [];

  return (
    <section className="rounded-2xl border border-gray-200 bg-white p-5 dark:border-gray-600 dark:bg-gradient-to-br dark:from-gray-800 dark:to-gray-700/70">
      {/* Row 1: what kind of meeting this is, and the way through to the full minutes. */}
      <div className="flex flex-wrap items-center gap-2.5">
        <span className="grid h-[34px] w-[34px] shrink-0 place-items-center rounded-[9px] bg-emerald-500/15 text-emerald-600 dark:text-emerald-400">
          <MinutesGlyph />
        </span>
        <h2 className="text-[15px] font-semibold text-gray-900 dark:text-gray-100">{t("workspace:hubSummary")}</h2>

        <MeetingTypeMenu
          variant="pill"
          currentTypeId={rec.meetingTypeId}
          busy={minutesRunning}
          onApply={onApplyMeetingType}
        />

        <div className="ml-auto flex items-center gap-0.5">
          <ToolbarButton
            label={t("workspace:editSummaryAction")}
            icon={PencilIcon}
            disabled={!hasTranscript}
            onClick={onEditSummary}
          />
          <ToolbarButton
            label={t("workspace:resummarise")}
            icon={RefreshIcon}
            disabled={!hasTranscript || isSummarizing}
            onClick={onResummarise}
          />
          <button
            type="button"
            onClick={onOpenMinutes}
            className="ml-1 inline-flex items-center gap-1 text-xs font-semibold text-blue-600 hover:underline dark:text-blue-300"
          >
            {t("workspace:hubOpenFullMinutes")}
            <ChevronRightIcon size={14} />
          </button>
        </div>
      </div>

      {/* Row 2: the high-level facts, as a wrapping chip row. */}
      <div className="mt-3.5 flex flex-wrap items-center gap-2">
        <DetailChip icon={<CalendarIcon />} tone="cyan">
          {formatLongDate(rec.createdAt, i18n.language)} · {formatTimeHm(rec.createdAt)}
        </DetailChip>

        <DetailChip icon={<ClockIcon />}>{formatDurationApprox(rec.durationMs)}</DetailChip>

        <DetailChip icon={<AudioIcon />} tone={rec.hasAudio ? "green" : "muted"}>
          {rec.hasAudio ? t("workspace:audioAvailable") : t("workspace:hubAudioDeleted")}
          {daysLeft != null && (
            <span className="text-amber-600 dark:text-amber-400">
              · {t("workspace:hubAudioRetention", { days: daysLeft })}
            </span>
          )}
        </DetailChip>

        {rec.current?.language && (
          <DetailChip icon={<GlobeIcon />} tone="purple">
            {rec.current.language.toUpperCase()}
          </DetailChip>
        )}

        {labels.length > 0 && (
          <DetailChip>
            <SpeakerAvatarStack labels={labels} nameOf={speakerNameOf} size="xs" />
            {t("workspace:hubSpeakersChip", { count: labels.length })}
          </DetailChip>
        )}

        {rec.recordedByName && (
          <DetailChip icon={<UserIcon />} tone="pink">
            {rec.recordedByName}
          </DetailChip>
        )}

        {rooms.map((room) => (
          <DetailChip key={room.id} icon={<RoomBadge icon={room.icon} color={room.color} name={room.name} size="2xs" />}>
            {room.name}
            {room.isMain && <span className="text-gray-400 dark:text-gray-500">{t("workspace:roomHomeTag")}</span>}
          </DetailChip>
        ))}
      </div>

      {/* The summary itself — inline, always. */}
      {summary ? (
        <p className="mt-3.5 whitespace-pre-wrap text-[13.5px] leading-relaxed text-gray-700 dark:text-gray-300">
          {shown}
          {isLong && (
            <button
              type="button"
              onClick={() => setExpanded((v) => !v)}
              className="ml-1.5 font-semibold text-blue-600 hover:underline dark:text-blue-300"
            >
              {expanded ? t("workspace:hubShowLess") : t("workspace:hubShowMore")}
            </button>
          )}
        </p>
      ) : (
        <p className="mt-3.5 text-[13.5px] text-gray-500 dark:text-gray-400">{t("workspace:hubNoSummary")}</p>
      )}
    </section>
  );
}
