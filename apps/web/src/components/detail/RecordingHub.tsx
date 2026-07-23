import { useTranslation } from "react-i18next";
import type { Attachment, FormulaResult, MeetingNote, RecordingDetail } from "../../lib/types";
import type { SectionKey } from "../../lib/detailSection";
import { hubCounts } from "../../lib/hubCounts";
import { formatBytes, formatDurationApprox } from "../../lib/format";
import HeroSummaryCard from "./HeroSummaryCard";
import HubTile, { TileAction } from "./HubTile";
import { SpeakerAvatarStack } from "./SpeakerAvatar";
import {
  ActionsGlyph,
  FileIcon,
  FilesGlyph,
  FormulasGlyph,
  LinkIcon,
  NotesGlyph,
  PlayIcon,
  PlusIcon,
  SpeakersGlyph,
  TemplateIcon,
  TranscriptGlyph,
} from "./SectionIcons";

/// The recording hub — the detail page's landing view, replacing the old Overview tab and the tab strip
/// that sat above it. A hero summary card over a grid of capability tiles, each showing its real count
/// and a preview of what is inside, so nothing is discoverable only by clicking a tab.
///
/// Tiles are content-height and top-aligned (`auto-rows-min`); stretching them to fill the row is what
/// produced the slab of dead space the redesign set out to remove.

/// A fixed silhouette for the Transcript tile's waveform. It is decoration, not data — the real audio's
/// amplitudes aren't available client-side, and drawing them would mean decoding the whole blob.
const WAVE = [40, 75, 55, 95, 35, 65, 80, 45, 70, 50, 85, 60];
const WAVE_PLAYHEAD = 3;

export default function RecordingHub({
  rec,
  notes,
  attachments,
  formulaResults,
  shots = [],
  meetingTypeTitle,
  speakerNameOf,
  minutesRunning,
  hasTranscript,
  isSummarizing,
  showRooms,
  onOpenSection,
  onApplyMeetingType,
  onEditSummary,
  onResummarise,
  onNewNote,
  onAddFile,
  onRunFormula,
}: {
  rec: RecordingDetail;
  notes: MeetingNote[];
  attachments: Attachment[];
  formulaResults: FormulaResult[];
  shots?: unknown[];
  /// The applied template's name, for the Formulas tile's "From <template>" line.
  meetingTypeTitle: string | null;
  speakerNameOf: (label: string) => string;
  minutesRunning: boolean;
  hasTranscript: boolean;
  isSummarizing: boolean;
  showRooms: boolean;
  onOpenSection: (key: SectionKey) => void;
  onApplyMeetingType: (typeId: string) => void;
  onEditSummary: () => void;
  onResummarise: () => void;
  onNewNote: () => void;
  onAddFile: () => void;
  onRunFormula: () => void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const counts = hubCounts(rec, notes, attachments, formulaResults, shots);
  const labels = rec.speakers.map((s) => s.label);
  const latestNote = notes[notes.length - 1];
  const notesSubtitle = t("workspace:hubNotesSubtitle", { count: counts.notes });
  const notesAndScreenshotsSubtitle =
    counts.screenshots > 0
      ? `${notesSubtitle} · ${t("workspace:hubScreenshotsCount", { count: counts.screenshots })}`
      : notesSubtitle;

  return (
    <div className="flex flex-col gap-3.5">
      <HeroSummaryCard
        rec={rec}
        speakerNameOf={speakerNameOf}
        minutesRunning={minutesRunning}
        hasTranscript={hasTranscript}
        isSummarizing={isSummarizing}
        showRooms={showRooms}
        onOpenMinutes={() => onOpenSection("minutes")}
        onApplyMeetingType={onApplyMeetingType}
        onEditSummary={onEditSummary}
        onResummarise={onResummarise}
      />

      <div className="grid auto-rows-min grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
        <HubTile
          color="blue"
          icon={<TranscriptGlyph />}
          title={t("workspace:detailTabTranscript")}
          subtitle={t("workspace:hubTranscriptSubtitle", {
            segments: counts.segments,
            duration: formatDurationApprox(counts.durationMs),
          })}
          onOpen={() => onOpenSection("transcript")}
        >
          <div className="flex h-6 items-center gap-0.5" aria-hidden>
            {WAVE.map((h, i) => (
              <span
                key={i}
                style={{ height: `${h}%` }}
                className={`flex-1 rounded-sm ${
                  i === WAVE_PLAYHEAD ? "bg-blue-500" : "bg-gray-200 dark:bg-gray-600"
                }`}
              />
            ))}
          </div>
        </HubTile>

        <HubTile
          color="amber"
          icon={<ActionsGlyph />}
          title={t("workspace:detailTabActions")}
          subtitle={t("workspace:hubActionsSubtitle", { open: counts.actionsOpen, done: counts.actionsDone })}
          onOpen={() => onOpenSection("actions")}
        >
          <div className="flex flex-col gap-1.5">
            {rec.actions.slice(0, 2).map((a) => (
              <div key={a.id} className="flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300">
                <span
                  className={`h-3.5 w-3.5 shrink-0 rounded border-[1.5px] ${
                    a.completed
                      ? "border-emerald-500 bg-emerald-500/20"
                      : "border-gray-400 dark:border-gray-500"
                  }`}
                />
                <span className="truncate">
                  {a.text}
                  {a.actor && <span className="text-gray-400 dark:text-gray-500"> - {a.actor}</span>}
                </span>
              </div>
            ))}
            {rec.actions.length === 0 && (
              <p className="text-xs text-gray-400 dark:text-gray-500">{t("workspace:hubNoActions")}</p>
            )}
          </div>
        </HubTile>

        <HubTile
          color="pink"
          icon={<SpeakersGlyph />}
          title={t("workspace:detailTabSpeakers")}
          subtitle={t("workspace:hubSpeakersSubtitle", { count: counts.speakers })}
          onOpen={() => onOpenSection("speakers")}
        >
          {labels.length > 0 ? (
            <SpeakerAvatarStack labels={labels} nameOf={speakerNameOf} size="sm" />
          ) : (
            <p className="text-xs text-gray-400 dark:text-gray-500">{t("workspace:hubNoSpeakers")}</p>
          )}
        </HubTile>

        <HubTile
          color="purple"
          icon={<NotesGlyph />}
          title={t("workspace:detailTabNotes")}
          subtitle={notesAndScreenshotsSubtitle}
          action={<TileAction label={t("workspace:hubNewNote")} icon={<PlusIcon />} onClick={onNewNote} />}
          onOpen={() => onOpenSection("notes")}
        >
          {latestNote ? (
            <p className="line-clamp-2 text-xs leading-relaxed text-gray-500 dark:text-gray-400">{latestNote.text}</p>
          ) : (
            <p className="text-xs text-gray-400 dark:text-gray-500">{t("workspace:hubNoNotes")}</p>
          )}
        </HubTile>

        <HubTile
          color="cyan"
          icon={<FilesGlyph />}
          title={t("workspace:detailTabFiles")}
          subtitle={t("workspace:hubFilesSubtitle", { count: counts.files })}
          action={<TileAction label={t("workspace:hubAddFile")} icon={<PlusIcon />} onClick={onAddFile} />}
          onOpen={() => onOpenSection("files")}
        >
          <div className="flex flex-col gap-1.5">
            {attachments.slice(0, 3).map((a) => (
              <div
                key={a.id}
                className={`flex items-center gap-2 text-xs ${
                  a.kind === "Url" ? "text-blue-600 dark:text-blue-300" : "text-gray-700 dark:text-gray-300"
                }`}
              >
                <span className="shrink-0 text-gray-400 dark:text-gray-500">
                  {a.kind === "Url" ? <LinkIcon size={13} /> : <FileIcon />}
                </span>
                <span className="flex-1 truncate">{a.name}</span>
                {a.kind === "File" && (
                  <span className="shrink-0 text-[11px] text-gray-400 dark:text-gray-500">
                    {formatBytes(a.sizeBytes)}
                  </span>
                )}
              </div>
            ))}
            {attachments.length === 0 && (
              <p className="text-xs text-gray-400 dark:text-gray-500">{t("workspace:hubNoFiles")}</p>
            )}
          </div>
        </HubTile>

        <HubTile
          color="blue"
          icon={<FormulasGlyph />}
          title={t("workspace:detailTabFormulas")}
          subtitle={t("workspace:hubFormulasSubtitle", { count: counts.formulaRuns })}
          action={
            <TileAction label={t("workspace:hubRunFormula")} icon={<PlayIcon size={13} />} onClick={onRunFormula} accent />
          }
          onOpen={() => onOpenSection("formulas")}
        >
          <div className="flex flex-col gap-1.5">
            {formulaResults.slice(0, 2).map((r) => (
              <div key={r.id} className="flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300">
                <span
                  className={`h-1.5 w-1.5 shrink-0 rounded-full ${
                    r.status === "Ready"
                      ? "bg-emerald-500"
                      : r.status === "Failed"
                        ? "bg-red-500"
                        : "bg-amber-500"
                  }`}
                />
                <span className="flex-1 truncate">{r.name}</span>
                <span
                  className={`shrink-0 text-[11px] ${
                    r.status === "Ready"
                      ? "text-emerald-600 dark:text-emerald-400"
                      : r.status === "Failed"
                        ? "text-red-600 dark:text-red-400"
                        : "text-amber-600 dark:text-amber-400"
                  }`}
                >
                  {r.status === "Ready"
                    ? t("workspace:hubFormulaReady")
                    : r.status === "Failed"
                      ? t("workspace:formulaFailed")
                      : t("workspace:formulaGenerating")}
                </span>
              </div>
            ))}
            {formulaResults.length === 0 && (
              <p className="text-xs text-gray-400 dark:text-gray-500">{t("workspace:hubNoFormulaRuns")}</p>
            )}
            {meetingTypeTitle && (
              <div className="flex items-center gap-2 text-xs text-gray-400 dark:text-gray-500">
                <span className="shrink-0">
                  <TemplateIcon />
                </span>
                <span className="truncate">{t("workspace:hubFromTemplate", { template: meetingTypeTitle })}</span>
              </div>
            )}
          </div>
        </HubTile>
      </div>
    </div>
  );
}
