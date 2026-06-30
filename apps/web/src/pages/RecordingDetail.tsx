import { useEffect, useMemo, useRef, useState } from "react";
import { useParams, useNavigate, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu from "../components/KebabMenu";
import DetailToolbar from "../components/DetailToolbar";
import ActionsPanel from "../components/ActionsPanel";
import CollapsibleSection from "../components/CollapsibleSection";
import MoveToSectionModal from "../components/MoveToSectionModal";
import DownloadTranscriptModal from "../components/DownloadTranscriptModal";
import SummaryEditModal from "../components/SummaryEditModal";
import AttachmentsModal from "../components/AttachmentsModal";
import AttachmentsSplitButton from "../components/AttachmentsSplitButton";
import { recordingMenu } from "../components/recordingMenu";
import { copyRichLink, transcriptUrl } from "../lib/clipboard";
import { segmentIndexAtMs } from "../lib/transcriptNav";
import { formatBytes, formatDate, formatDuration } from "../lib/format";
import { hasRevisions, segmentText, toggleLabel } from "../lib/transcriptView";
import { fetchLanguages } from "../lib/languages";
import { allSpeakersAssigned } from "../lib/speakers";
import type { SegmentDto, SpeakerInfo, SpeakerProfile } from "../lib/types";

function fmt(ms: number): string {
  const s = Math.floor(ms / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

export default function RecordingDetail() {
  const { t, i18n } = useTranslation();
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const { data: rec } = useQuery({
    queryKey: ["recording", id],
    queryFn: () => api.getRecording(id),
    enabled: Boolean(id),
  });
  const { data: profiles = [] } = useQuery({
    queryKey: ["speaker-profiles"],
    queryFn: api.listSpeakerProfiles,
  });
  const { data: attachments = [] } = useQuery({
    queryKey: ["attachments", id],
    queryFn: () => api.listAttachments(id),
    enabled: Boolean(id),
  });
  // The user's native language drives the "Translate to …" action; resolve its display name.
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const { data: languages = [] } = useQuery({ queryKey: ["languages"], queryFn: fetchLanguages });
  const nativeLang = languages.find((l) => l.code === profile?.nativeLanguage) ?? null;

  const audioRef = useRef<HTMLAudioElement>(null);
  const [activeIdx, setActiveIdx] = useState<number | null>(null);

  // When opened from a chat transcript link (/recordings/:id?t=ms), highlight and scroll to that segment.
  const tParam = searchParams.get("t");
  useEffect(() => {
    if (tParam == null) return;
    const ms = Number(tParam);
    const segs = rec?.current?.segments;
    if (!Number.isFinite(ms) || !segs || segs.length === 0) return;
    const idx = segmentIndexAtMs(segs, ms);
    if (idx < 0) return;
    setActiveIdx(idx);
    const segId = segs[idx].id;
    // Defer until the rows have rendered.
    requestAnimationFrame(() =>
      document.getElementById(`seg-${segId}`)?.scrollIntoView({ block: "center", behavior: "smooth" }),
    );
  }, [tParam, rec]);
  const [requeuing, setRequeuing] = useState(false);
  const [summarizing, setSummarizing] = useState(false);
  const [extracting, setExtracting] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [editingSummary, setEditingSummary] = useState(false);
  const [attachmentsOpen, setAttachmentsOpen] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [editingSeg, setEditingSeg] = useState<SegmentDto | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionInfo, setActionInfo] = useState<string | null>(null);
  const [retranscribeOpen, setRetranscribeOpen] = useState(false);
  // When the transcript has edited/translated segments, the user can flip the whole list back to the
  // model's original words.
  const [showOriginal, setShowOriginal] = useState(false);
  const [translating, setTranslating] = useState(false);

  useEffect(() => {
    const hub = createHub((e) => {
      if (e.recordingId === id) qc.invalidateQueries({ queryKey: ["recording", id] });
    });
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [id, qc]);

  const labels = useMemo(() => {
    const set = new Set<string>();
    rec?.current?.segments.forEach((s) => set.add(s.speaker));
    return [...set];
  }, [rec]);

  // Labels flagged "Multiple Speakers" — their segment/speaker display is localised in-app.
  const multiSpeakerLabels = useMemo(
    () => new Set((rec?.speakers ?? []).filter((s) => s.isMultiSpeaker).map((s) => s.label)),
    [rec],
  );

  async function rename(label: string, name: string) {
    await api.renameSpeaker(id, label, name);
    qc.invalidateQueries({ queryKey: ["recording", id] });
  }

  async function assignSpeaker(label: string, profileId: string | null) {
    setActionError(null);
    try {
      await api.assignSpeaker(id, label, profileId);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errAssignSpeaker")));
    }
  }

  async function newPerson(label: string) {
    const name = window.prompt(t("workspace:namePrompt"))?.trim();
    if (!name) return;
    setActionError(null);
    try {
      await api.createSpeakerProfile(name, id, label);
      qc.invalidateQueries({ queryKey: ["recording", id] });
      qc.invalidateQueries({ queryKey: ["speaker-profiles"] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errCreatePerson")));
    }
  }

  // Mark a speaker as "Multiple Speakers" (overlapping speech) — detaches it from any voiceprint.
  async function markMulti(label: string) {
    setActionError(null);
    try {
      await api.markMultiSpeaker(id, label);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errAssignSpeaker")));
    }
  }

  // Delete a single (e.g. meaningless) segment from the current transcription.
  async function deleteSegment(segmentId: string) {
    if (!window.confirm(t("workspace:confirmDeleteSegment"))) return;
    setActionError(null);
    try {
      await api.deleteSegment(id, segmentId);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errDeleteSegment")));
    }
  }

  async function saveRecordingName(name: string) {
    await api.renameRecording(id, name.trim() || null);
    setRenaming(false);
    qc.invalidateQueries({ queryKey: ["recording", id] });
  }

  // Re-transcribe with the (optional) speaker-count hints chosen in the modal.
  async function retranscribe(min: number | null, max: number | null) {
    setActionError(null);
    setActionInfo(null);
    setRequeuing(true);
    try {
      await api.retranscribe(id, { speakers: { min, max } });
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setRetranscribeOpen(false);
      setActionInfo(t("workspace:retranscribing"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errRetranscribe")));
    } finally {
      setRequeuing(false);
    }
  }

  async function summarize() {
    // Re-summarising replaces the summary — confirm first when the user has hand-edited it.
    if (rec?.summary?.isUserEdited && !window.confirm(t("workspace:confirmResummarise"))) return;
    setActionError(null);
    setSummarizing(true);
    try {
      await api.summarize(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errSummarise")));
    } finally {
      setSummarizing(false);
    }
  }

  // Copy a persistent rich-text link to this transcript (the name shows as the link text).
  async function copyLink() {
    setActionError(null);
    setActionInfo(null);
    const ok = await copyRichLink(transcriptUrl(id), rec?.name ?? rec?.title ?? "");
    if (ok) setActionInfo(t("workspace:linkCopied"));
    else setActionError(t("workspace:errCopyLink"));
  }

  // Save a manually-written/edited summary (flagged user-edited so auto-summary won't clobber it).
  async function saveSummary(text: string) {
    setActionError(null);
    try {
      await api.updateSummary(id, text);
      setEditingSummary(false);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errEditSummary")));
    }
  }

  const refreshAttachments = () => {
    qc.invalidateQueries({ queryKey: ["attachments", id] });
    qc.invalidateQueries({ queryKey: ["user-storage"] }); // attachment bytes count toward quota
  };

  // Files dropped anywhere on the detail page are attached to this recording.
  async function onDropFiles(e: React.DragEvent) {
    e.preventDefault();
    setDragging(false);
    const files = e.dataTransfer.files;
    if (!files || files.length === 0) return;
    setActionError(null);
    try {
      for (const f of Array.from(files)) await api.addFileAttachment(id, f);
      refreshAttachments();
    } catch (err) {
      setActionError(apiErrorMessage(err, t("workspace:errAddAttachment")));
    }
  }

  // Extract action items from the current transcript via the LLM, then show the Actions panel.
  // Re-extracting replaces the existing list — confirm first if there's anything to lose.
  async function extractActions() {
    if (rec?.actionsExtracted && rec.actions.length > 0 &&
        !window.confirm(t("workspace:confirmReextract"))) return;
    setActionError(null);
    setActionInfo(null);
    setExtracting(true);
    try {
      const actions = await api.extractActions(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setActionInfo(actions.length ? t("workspace:extractedActions", { count: actions.length })
                                   : t("workspace:noActionsFound"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errExtract")));
    } finally {
      setExtracting(false);
    }
  }

  // Translate the whole transcript (+ summary + actions) into the user's native language. Overwrites any
  // existing revision/translation, so confirm first when there's edited text to lose.
  async function translateRecording() {
    if (!nativeLang) return;
    if (rec?.current && hasRevisions(rec.current.segments) &&
        !window.confirm(t("workspace:confirmTranslate", { language: nativeLang.englishName })))
      return;
    setActionError(null);
    setActionInfo(null);
    setTranslating(true);
    try {
      await api.translateRecording(id, nativeLang.code);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setActionInfo(t("workspace:translatedTo", { language: nativeLang.englishName }));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errTranslateTranscript")));
    } finally {
      setTranslating(false);
    }
  }

  async function translateSegment(segmentId: string) {
    if (!nativeLang) return;
    setActionError(null);
    setActionInfo(null);
    setTranslating(true);
    try {
      await api.translateSegment(id, segmentId, nativeLang.code);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errTranslateSegment")));
    } finally {
      setTranslating(false);
    }
  }

  async function addAction() {
    setActionError(null);
    try {
      await api.createAction(id, { text: "", actor: "", deadline: "" });
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errAddAction")));
    }
  }

  async function updateAction(actionId: string, patch: { text?: string; actor?: string; deadline?: string }) {
    setActionError(null);
    try {
      await api.updateAction(id, actionId, patch);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errUpdateAction")));
    }
  }

  async function removeAction(actionId: string) {
    setActionError(null);
    try {
      await api.deleteAction(id, actionId);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errRemoveAction")));
    }
  }

  async function mergeSegments() {
    if (!window.confirm(t("workspace:confirmMerge"))) return;
    setActionError(null);
    setActionInfo(null);
    try {
      await api.mergeSegments(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errMerge")));
    }
  }

  async function emailTranscript() {
    setActionError(null);
    setActionInfo(null);
    try {
      await api.emailTranscript(id);
      setActionInfo(t("workspace:emailedTranscript"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errEmail")));
    }
  }

  async function reidentify() {
    setActionError(null);
    setActionInfo(null);
    try {
      await api.reidentify(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setActionInfo(t("workspace:reidentified"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errReidentify")));
    }
  }

  // Lazily resolve the presigned URL and seek the shared <audio> element.
  async function playFrom(startMs: number) {
    const el = audioRef.current;
    if (!el) return;
    setActionError(null);
    try {
      if (!el.src) el.src = await api.audioUrl(id);
      el.currentTime = startMs / 1000;
      await el.play();
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errPlayAudio")));
    }
  }

  function onTimeUpdate() {
    const el = audioRef.current;
    const segs = rec?.current?.segments;
    if (!el || !segs) return;
    const ms = el.currentTime * 1000;
    const idx = segs.findIndex((s) => ms >= s.startMs && ms < s.endMs);
    setActiveIdx(idx >= 0 ? idx : null);
  }

  if (!rec) return <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;

  const hasTranscript = (rec.current?.segments.length ?? 0) > 0;
  const isSummarizing = rec.status === "Summarizing" || summarizing;

  const menuActions = recordingMenu({
    onRename: () => setRenaming(true),
    onCopyLink: copyLink,
    onRetranscribe: () => setRetranscribeOpen(true),
    onSummarise: summarize,
    onEditSummary: () => setEditingSummary(true),
    onExtractActions: extractActions,
    onReidentify: reidentify,
    onTranslate: nativeLang ? translateRecording : undefined,
    translateLabel: nativeLang?.englishName,
    onMove: () => setMoving(true),
    onPlay: () => void playFrom(0),
    onDownloadTranscript: () => setDownloading(true),
    onEmailTranscript: emailTranscript,
    onDownloadAudio: () => void api.downloadAudio(id),
    onDeleteAudio: async () => {
      if (!window.confirm(t("workspace:confirmDeleteAudio", { name: rec.name ?? rec.title }))) return;
      await api.deleteAudio(id);
      qc.invalidateQueries({ queryKey: ["recording", id] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    },
    onDelete: async () => {
      if (!window.confirm(t("workspace:confirmDelete", { name: rec.name ?? rec.title }))) return;
      await api.deleteRecording(id);
      navigate("/");
    },
    hasTranscript,
    hasAudio: rec.hasAudio,
    isSummarizing,
  }, t);

  return (
    <div
      className="relative space-y-5"
      onDragOver={(e) => {
        // Only react to file drags (ignore in-app text/section drags).
        if (Array.from(e.dataTransfer.types).includes("Files")) {
          e.preventDefault();
          setDragging(true);
        }
      }}
      onDragLeave={(e) => {
        if (e.currentTarget === e.target) setDragging(false);
      }}
      onDrop={onDropFiles}
    >
      {dragging && (
        <div className="pointer-events-none absolute inset-0 z-40 flex items-center justify-center rounded-lg border-2 border-dashed border-blue-400 bg-blue-50/80 text-sm font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-200">
          {t("workspace:dropToAttach")}
        </div>
      )}
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          {renaming ? (
            <RecordingNameForm
              initial={rec.name ?? ""}
              onSave={saveRecordingName}
              onCancel={() => setRenaming(false)}
            />
          ) : (
            <h1 className="text-lg font-semibold dark:text-gray-100">{rec.name ?? rec.title}</h1>
          )}
          <p className="text-xs text-gray-500 dark:text-gray-400">
            {rec.source === "System" ? t("workspace:sourceSystem") : rec.source === "Upload" ? t("workspace:sourceUpload") : t("workspace:sourceMicrophone")} ·{" "}
            {formatDate(rec.createdAt, i18n.language)}
            {rec.durationMs > 0 ? ` · ${formatDuration(rec.durationMs)}` : ""} · {rec.status}
            {rec.sizeBytes > 0 ? ` · ${formatBytes(rec.sizeBytes)}` : ""}
            {rec.current?.language ? ` · ${rec.current.language}` : ""}
            {rec.current?.processingMs ? ` · ${t("workspace:processedIn", { time: formatDuration(rec.current.processingMs) })}` : ""}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {(isSummarizing || requeuing || translating || rec.status === "Merging") && (
            <span className="text-xs text-gray-500 dark:text-gray-400">
              {rec.status === "Merging"
                ? t("workspace:merging")
                : translating
                  ? t("workspace:translating")
                  : isSummarizing
                    ? t("workspace:summarising")
                    : t("workspace:queuing")}
            </span>
          )}
          <DetailToolbar
            onRename={() => setRenaming(true)}
            onCopyLink={copyLink}
            onRetranscribe={() => setRetranscribeOpen(true)}
            onMove={() => setMoving(true)}
            onExtractActions={extractActions}
            onEmailTranscript={emailTranscript}
            onDownloadTranscript={() => setDownloading(true)}
            hasTranscript={hasTranscript}
            hasAudio={rec.hasAudio}
          />
          <KebabMenu actions={menuActions} />
        </div>
      </div>

      {actionError && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{actionError}</p>
      )}

      {actionInfo && (
        <p className="rounded bg-green-50 p-3 text-sm text-green-700 dark:bg-green-900/30 dark:text-green-300">{actionInfo}</p>
      )}

      {extracting && (
        <p className="rounded bg-amber-50 p-3 text-sm text-amber-800 dark:bg-amber-900/30 dark:text-amber-300">
          {t("workspace:extractingActions")}
        </p>
      )}

      <div className="flex items-center gap-2">
        <AttachmentsSplitButton
          recordingId={id}
          attachments={attachments}
          onManage={() => setAttachmentsOpen(true)}
        />
        <span className="text-xs text-gray-400 dark:text-gray-500">{t("workspace:dropHint")}</span>
      </div>

      {rec.status === "Failed" && rec.error && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{rec.error}</p>
      )}

      {isSummarizing && !rec.summary && (
        <p className="rounded bg-amber-50 p-3 text-sm text-amber-800 dark:bg-amber-900/30 dark:text-amber-300">{t("workspace:summarising")}</p>
      )}
      {rec.summary && (
        <CollapsibleSection title={t("workspace:sectionSummary")}>
          {rec.summary.isUserEdited && (
            <p className="mb-1 text-xs italic text-gray-400 dark:text-gray-500">{t("workspace:summaryEditedHint")}</p>
          )}
          <p className="whitespace-pre-wrap text-sm text-gray-800 dark:text-gray-200">{rec.summary.text}</p>
        </CollapsibleSection>
      )}

      {/* Shown by exception — only once the user has run "Extract actions" for this recording. */}
      {rec.actionsExtracted && (
        <ActionsPanel
          actions={rec.actions}
          onAdd={addAction}
          onUpdate={updateAction}
          onDelete={removeAction}
        />
      )}

      {labels.length > 0 && (
        // Default collapsed when every speaker is already assigned (nothing left to label).
        <CollapsibleSection title={t("workspace:sectionSpeakers")} defaultCollapsed={allSpeakersAssigned(rec.speakers)}>
          <div className="flex flex-wrap gap-4">
            {labels.map((label) => {
              const info = rec.speakers.find((s) => s.label === label);
              return (
                <SpeakerRow
                  key={label}
                  label={label}
                  info={info}
                  initial={
                    info?.isMultiSpeaker
                      ? t("workspace:multipleSpeakers")
                      : rec.speakerNames[label] ?? info?.displayName ?? label
                  }
                  profiles={profiles}
                  onRename={(name) => rename(label, name)}
                  onAssign={(profileId) => assignSpeaker(label, profileId)}
                  onNewPerson={() => newPerson(label)}
                  onMulti={() => markMulti(label)}
                />
              );
            })}
          </div>
        </CollapsibleSection>
      )}

      {rec.current ? (
        // The body is flush (no horizontal padding) so the segment rows keep the panel's full width.
        <CollapsibleSection title={t("workspace:sectionTranscript")} bodyClassName="space-y-3 pb-2">
          <div className="flex flex-wrap items-center gap-3 px-4">
            <button
              onClick={() => playFrom(0)}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              ▶ {t("workspace:playAll")}
            </button>
            <button
              onClick={mergeSegments}
              title={t("workspace:mergeRowsTitle")}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("workspace:mergeRows")}
            </button>
            {hasRevisions(rec.current.segments) && (
              <button
                onClick={() => setShowOriginal((v) => !v)}
                title={t("workspace:toggleViewTitle")}
                className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
              >
                {toggleLabel(showOriginal)}
              </button>
            )}
            {rec.hasAudio && (
              <audio ref={audioRef} controls onTimeUpdate={onTimeUpdate} className="h-8 min-w-48 flex-1" />
            )}
          </div>
          <ul className="space-y-2">
            {rec.current.segments.map((s, i) => (
              <SegmentRow
                key={s.id}
                seg={s}
                speakerName={multiSpeakerLabels.has(s.speaker) ? t("workspace:multipleSpeakers") : s.speakerDisplay}
                active={i === activeIdx}
                showOriginal={showOriginal}
                editLabel={t("recordings:edit")}
                deleteLabel={t("recordings:delete")}
                translateLabel={nativeLang ? t("recordings:translateTo", { language: nativeLang.englishName }) : undefined}
                onPlay={rec.hasAudio ? () => playFrom(s.startMs) : undefined}
                onEdit={() => setEditingSeg(s)}
                onDelete={() => deleteSegment(s.id)}
                onTranslate={nativeLang ? () => translateSegment(s.id) : undefined}
              />
            ))}
          </ul>
        </CollapsibleSection>
      ) : (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:noTranscriptYet")}</p>
      )}

      {editingSeg && (
        <SegmentEditModal
          seg={editingSeg}
          onClose={() => setEditingSeg(null)}
          onSave={async (text) => {
            await api.updateSegment(id, editingSeg.id, text);
            setEditingSeg(null);
            qc.invalidateQueries({ queryKey: ["recording", id] });
          }}
        />
      )}

      {editingSummary && (
        <SummaryEditModal
          initial={rec.summary?.text ?? ""}
          onClose={() => setEditingSummary(false)}
          onSave={saveSummary}
        />
      )}

      {attachmentsOpen && (
        <AttachmentsModal
          recordingId={id}
          attachments={attachments}
          onClose={() => setAttachmentsOpen(false)}
          onChange={refreshAttachments}
        />
      )}

      {moving && <MoveToSectionModal recordingId={id} onClose={() => setMoving(false)} />}
      {downloading && <DownloadTranscriptModal recordingId={id} onClose={() => setDownloading(false)} />}

      {retranscribeOpen && (
        <RetranscribeModal
          initialMin={rec.minSpeakers}
          initialMax={rec.maxSpeakers}
          hasRevisions={!!rec.current && hasRevisions(rec.current.segments)}
          busy={requeuing}
          onCancel={() => setRetranscribeOpen(false)}
          onConfirm={retranscribe}
        />
      )}
    </div>
  );
}

/// Asked for when the user re-transcribes: optional diarization speaker-count hints (the exception, not
/// the norm — used mainly to split two people the diarizer merged into one).
function RetranscribeModal({
  initialMin,
  initialMax,
  hasRevisions,
  busy,
  onCancel,
  onConfirm,
}: {
  initialMin: number | null;
  initialMax: number | null;
  hasRevisions: boolean;
  busy: boolean;
  onCancel: () => void;
  onConfirm: (min: number | null, max: number | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const [min, setMin] = useState(initialMin != null ? String(initialMin) : "");
  const [max, setMax] = useState(initialMax != null ? String(initialMax) : "");

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onCancel();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onCancel]);

  function confirm() {
    onConfirm(min.trim() ? Number(min) : null, max.trim() ? Number(max) : null);
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div
        role="dialog"
        aria-label={t("retranscribeTitle")}
        className="w-full max-w-md rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-1 text-base font-semibold dark:text-gray-100">{t("retranscribeTitle")}</h2>
        <p className="mb-4 text-xs text-gray-500 dark:text-gray-400">{t("retranscribeHelp")}</p>
        {hasRevisions && (
          <p className="mb-4 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-700/60 dark:bg-amber-900/30 dark:text-amber-300">
            {t("retranscribeRevisionsWarning")}
          </p>
        )}
        <div className="flex items-center gap-4 text-sm">
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            {t("minSpeakers")}
            <input
              type="number"
              min={1}
              value={min}
              onChange={(e) => setMin(e.target.value)}
              aria-label={t("minSpeakersAria")}
              className="w-20 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            {t("maxSpeakers")}
            <input
              type="number"
              min={1}
              value={max}
              onChange={(e) => setMax(e.target.value)}
              aria-label={t("maxSpeakersAria")}
              className="w-20 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
        </div>
        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={confirm}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("starting") : t("retranscribeTitle")}
          </button>
        </div>
      </div>
    </div>
  );
}

function SegmentEditModal({
  seg,
  onClose,
  onSave,
}: {
  seg: SegmentDto;
  onClose: () => void;
  onSave: (text: string | null) => Promise<void>;
}) {
  const { t } = useTranslation("workspace");
  const [text, setText] = useState(seg.text);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const taRef = useRef<HTMLTextAreaElement>(null);
  const revised = seg.revised != null;

  // Grow the textarea to fit its content; CSS max-height keeps the modal on-screen (it scrolls past that).
  function autosize() {
    const el = taRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }
  // Size to the initial text once mounted.
  useEffect(() => autosize(), []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function save(value: string | null) {
    setBusy(true);
    setError(null);
    try {
      await onSave(value);
    } catch (e) {
      setError(apiErrorMessage(e, t("errSaveSegment")));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("editSegment")}
        className="w-full max-w-3xl rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("editSegment")}</h2>
        <textarea
          ref={taRef}
          autoFocus
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            autosize();
          }}
          aria-label={t("segmentTextAria")}
          className="block max-h-[60vh] min-h-[8rem] w-full resize-none overflow-y-auto rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        {revised && (
          <p className="mt-2 text-xs text-gray-500 dark:text-gray-400">
            <span className="font-medium">{t("originalLabel")}</span> {seg.original}
          </p>
        )}
        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
        <div className="mt-3 flex items-center justify-end gap-2">
          {/* Clearing a revision restores the model's original words (sends null to the server). */}
          {revised && (
            <button
              type="button"
              onClick={() => save(null)}
              disabled={busy}
              className="mr-auto rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("resetToOriginal")}
            </button>
          )}
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={() => save(text)}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("common:saving") : t("common:save")}
          </button>
        </div>
      </div>
    </div>
  );
}

function RecordingNameForm({
  initial,
  onSave,
  onCancel,
}: {
  initial: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation("workspace");
  const [value, setValue] = useState(initial);
  return (
    <form
      className="flex items-center gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        onSave(value);
      }}
    >
      <input
        autoFocus
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => e.key === "Escape" && onCancel()}
        placeholder={t("recordingNamePlaceholder")}
        aria-label={t("recordingNamePlaceholder")}
        className="w-64 rounded border px-2 py-1 text-base dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800">
        {t("common:save")}
      </button>
    </form>
  );
}

const NEW_PERSON = "__new__";
const MULTI_SPEAKER = "__multi__";

export function SpeakerRow({
  label,
  info,
  initial,
  profiles,
  onRename,
  onAssign,
  onNewPerson,
  onMulti,
}: {
  label: string;
  info: SpeakerInfo | undefined;
  initial: string;
  profiles: SpeakerProfile[];
  onRename: (name: string) => void;
  onAssign: (profileId: string | null) => void;
  onNewPerson: () => void;
  onMulti: () => void;
}) {
  const { t } = useTranslation("workspace");
  const [value, setValue] = useState(initial);
  // Keep the field in sync when identification/reassignment changes the name out from under us.
  useEffect(() => setValue(initial), [initial]);

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-center gap-1">
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
      <input
        value={value}
        aria-label={t("nameForAria", { label })}
        onChange={(e) => setValue(e.target.value)}
        onBlur={() => value !== initial && onRename(value)}
        className="w-40 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <select
        value={info?.isMultiSpeaker ? MULTI_SPEAKER : info?.profileId ?? ""}
        aria-label={t("assignAria", { label })}
        onChange={(e) => {
          const v = e.target.value;
          if (v === NEW_PERSON) onNewPerson();
          else if (v === MULTI_SPEAKER) onMulti();
          else onAssign(v || null);
        }}
        className="w-40 rounded border px-2 py-1 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      >
        <option value="">{t("unassigned")}</option>
        {profiles.map((p) => (
          <option key={p.id} value={p.id}>
            {p.name}
          </option>
        ))}
        <option value={MULTI_SPEAKER}>{t("multipleSpeakers")}</option>
        <option value={NEW_PERSON}>{t("newPerson")}</option>
      </select>
    </div>
  );
}

function SegmentRow({
  seg,
  speakerName,
  active,
  showOriginal,
  editLabel,
  deleteLabel,
  translateLabel,
  onPlay,
  onEdit,
  onDelete,
  onTranslate,
}: {
  seg: SegmentDto;
  /// The speaker name to show (localised "Multiple Speakers" overrides the server display).
  speakerName: string;
  active: boolean;
  showOriginal: boolean;
  editLabel: string;
  deleteLabel: string;
  translateLabel?: string;
  /// Seek+play from this segment. Omitted when the recording has no audio (row isn't clickable then).
  onPlay?: () => void;
  onEdit: () => void;
  onDelete: () => void;
  onTranslate?: () => void;
}) {
  const { t } = useTranslation("workspace");
  const revised = seg.revised != null;
  const actions = [
    { label: editLabel, onClick: onEdit },
    ...(onTranslate && translateLabel ? [{ label: translateLabel, onClick: onTranslate }] : []),
    { label: deleteLabel, onClick: onDelete, danger: true },
  ];
  return (
    <li
      id={`seg-${seg.id}`}
      onClick={onPlay}
      className={`flex items-start gap-3 rounded-lg border px-4 py-2 hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800 ${
        onPlay ? "cursor-pointer" : ""
      } ${
        active
          ? "border-blue-300 bg-blue-50 dark:border-blue-700 dark:bg-blue-900/30"
          : "bg-white dark:bg-gray-900"
      }`}
    >
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400 dark:text-gray-500">{fmt(seg.startMs)}</span>
      <span className="w-28 shrink-0 text-sm font-medium text-gray-700 dark:text-gray-200">{speakerName}</span>
      {/* Auto-expands vertically to show the full (possibly merged) block of text. */}
      <span className="flex-1 whitespace-pre-wrap break-words text-sm dark:text-gray-200">
        {segmentText(seg, showOriginal)}
      </span>
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
      <KebabMenu actions={actions} label={t("segmentActions")} />
    </li>
  );
}
