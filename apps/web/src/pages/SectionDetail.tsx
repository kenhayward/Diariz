import { useState } from "react";
import { useParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDurationHm, formatDate } from "../lib/format";
import { folderUrl, copyRichLink } from "../lib/clipboard";
import { renderMarkdown } from "../lib/markdown";
import DetailTabs, { type DetailTab } from "../components/DetailTabs";
import ToolbarButton, { iconProps } from "../components/ToolbarButton";
import KebabMenu from "../components/KebabMenu";
import { folderMenu } from "../components/folderMenu";
import MeetingTypeMenu from "../components/MeetingTypeMenu";
import SummaryEditModal from "../components/SummaryEditModal";
import MeetingMinutesEditModal from "../components/MeetingMinutesEditModal";
import FolderRecordingList from "../components/FolderRecordingList";
import FolderActionsTable from "../components/FolderActionsTable";
import FolderNotesList from "../components/FolderNotesList";
import FolderAttachmentsList from "../components/FolderAttachmentsList";
import FolderAttachmentsManager from "../components/FolderAttachmentsManager";
import type { SectionAttachmentItem, SectionDetail as SectionDetailT } from "../lib/types";

const TAB_KEY = "diariz.sectionTab";

export default function SectionDetail() {
  const { id } = useParams<{ id: string }>();
  const { t, i18n } = useTranslation(["workspace", "common", "recordings"]);
  const qc = useQueryClient();

  const [tab, setTab] = useState<string>(() => localStorage.getItem(TAB_KEY) ?? "overview");
  const selectTab = (k: string) => { setTab(k); try { localStorage.setItem(TAB_KEY, k); } catch { /* non-fatal */ } };
  const [renaming, setRenaming] = useState(false);
  const [editingSummary, setEditingSummary] = useState(false);
  const [editingMinutes, setEditingMinutes] = useState(false);
  const [info, setInfo] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const { data: section, isError, error: sectionError } = useQuery({
    queryKey: ["section", id],
    queryFn: () => api.getSection(id!),
    enabled: !!id,
    // Poll while a folder artifact is generating so the page updates even if the SignalR event is missed.
    refetchInterval: (q) => {
      const d = q.state.data as SectionDetailT | undefined;
      const gen = d?.summary?.status === "Generating" || d?.minutes?.status === "Generating";
      return gen ? 2500 : false;
    },
  });
  const { data: actions } = useQuery({ queryKey: ["section-actions", id], queryFn: () => api.listSectionActions(id!), enabled: !!id });
  const { data: notes } = useQuery({ queryKey: ["section-notes", id], queryFn: () => api.listSectionNotes(id!), enabled: !!id });
  const { data: attachments } = useQuery({ queryKey: ["section-attachments", id], queryFn: () => api.listSectionAttachments(id!), enabled: !!id });
  const { data: folderAttachments } = useQuery({ queryKey: ["folder-attachments", id], queryFn: () => api.listFolderAttachments(id!), enabled: !!id });

  if (!id) return null;
  // Surface a load failure (e.g. a folder you can't access) instead of hanging on "Loading ..." forever.
  if (isError)
    return <p className="p-4 text-sm text-red-600 dark:text-red-400">{apiErrorMessage(sectionError, t("workspace:errLoadFolder"))}</p>;
  if (!section) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;

  const refetchSection = () => qc.invalidateQueries({ queryKey: ["section", id] });
  const run = async (fn: () => Promise<unknown>, fallback: string, invalidate: unknown[]) => {
    setError(null);
    try { await fn(); qc.invalidateQueries({ queryKey: invalidate }); }
    catch (e) { setError(apiErrorMessage(e, t(fallback))); }
  };

  // ---- header actions ----
  async function saveName(name: string) {
    await run(() => api.renameSection(id!, name.trim()), "workspace:mtSaveError", ["section", id]);
    qc.invalidateQueries({ queryKey: ["sections"] });
    setRenaming(false);
  }
  async function copyLink() {
    const ok = await copyRichLink(folderUrl(id!), section!.name);
    setInfo(ok ? t("workspace:linkCopied") : null);
    setError(ok ? null : t("workspace:errCopyLink"));
  }

  const hasRecordings = section.stats.transcriptCount > 0;
  const summaryGenerating = section.summary?.status === "Generating";
  const minutesGenerating = section.minutes?.status === "Generating";

  // ---- Overview tab ----
  const overview: DetailTab = {
    key: "overview",
    label: t("workspace:detailTabOverview"),
    toolbar: (
      <>
        <ToolbarButton label={t("workspace:editSummaryAction")} disabled={!section.summary} onClick={() => setEditingSummary(true)} icon={<PencilIcon />} />
        <ToolbarButton
          label={t("workspace:resummarise")}
          disabled={!hasRecordings || summaryGenerating}
          onClick={() => run(() => api.generateSectionSummary(id!), "workspace:errSummarise", ["section", id]).then(refetchSection)}
          icon={<RefreshIcon />}
        />
      </>
    ),
    content: (
      <div className="space-y-4 px-4 pb-4">
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
          <dt className="text-gray-500 dark:text-gray-400">{t("workspace:folderTranscripts")}</dt>
          <dd className="dark:text-gray-200">{section.stats.transcriptCount}</dd>
          <dt className="text-gray-500 dark:text-gray-400">{t("workspace:durationLabel")}</dt>
          <dd className="dark:text-gray-200">{formatDurationHm(section.stats.totalDurationMs)}</dd>
        </dl>

        <div>
          <h3 className="mb-1 text-sm font-semibold dark:text-gray-100">{t("workspace:sectionSummary")}</h3>
          {summaryGenerating ? (
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:folderGenerating")}</p>
          ) : section.summary?.status === "Failed" ? (
            <p className="text-sm text-red-600 dark:text-red-400">{section.summary.error || t("workspace:errSummarise")}</p>
          ) : section.summary?.text ? (
            <p className="whitespace-pre-wrap text-sm text-gray-800 dark:text-gray-200">{section.summary.text}</p>
          ) : (
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:folderSummaryEmpty")}</p>
          )}
        </div>

        <div>
          <h3 className="mb-1 text-sm font-semibold dark:text-gray-100">{t("workspace:folderTranscriptList")}</h3>
          <div className="-mx-4"><FolderRecordingList sectionId={id} /></div>
        </div>
      </div>
    ),
  };

  // ---- Minutes tab ----
  const minutes: DetailTab = {
    key: "minutes",
    label: t("workspace:detailTabMinutes"),
    toolbar: (
      <>
        <MeetingTypeMenu
          currentTypeId={section.meetingTypeId}
          busy={minutesGenerating}
          onApply={(typeId) => run(() => api.generateSectionMinutes(id!, typeId), "workspace:errMinutes", ["section", id]).then(refetchSection)}
        />
        <ToolbarButton label={t("workspace:editMeetingMinutes")} disabled={!section.minutes?.text} onClick={() => setEditingMinutes(true)} icon={<PencilIcon />} />
        <ToolbarButton
          label={t("workspace:recreateMeetingMinutes")}
          disabled={!hasRecordings || minutesGenerating}
          onClick={() => run(() => api.generateSectionMinutes(id!, section.meetingTypeId), "workspace:errMinutes", ["section", id]).then(refetchSection)}
          icon={<RefreshIcon />}
        />
      </>
    ),
    content: (
      <div className="px-4 pb-4">
        {minutesGenerating ? (
          <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:folderGenerating")}</p>
        ) : section.minutes?.status === "Failed" ? (
          <p className="text-sm text-red-600 dark:text-red-400">{section.minutes.error || t("workspace:errMinutes")}</p>
        ) : section.minutes?.text ? (
          <div
            className="chat-md text-sm dark:text-gray-200 [&_h1]:mb-2 [&_h1]:mt-3 [&_h1]:text-lg [&_h1]:font-semibold [&_h2]:mb-1 [&_h2]:mt-3 [&_h2]:text-base [&_h2]:font-semibold [&_li]:ml-4 [&_li]:list-disc [&_p]:mb-2"
            dangerouslySetInnerHTML={{ __html: renderMarkdown(section.minutes.text) }}
          />
        ) : (
          <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:folderMinutesEmpty")}</p>
        )}
      </div>
    ),
  };

  // ---- Actions / Notes / Attachments tabs (aggregated; edit/delete only) ----
  const actionsTab: DetailTab = {
    key: "actions",
    label: t("workspace:detailTabActions"),
    content: (
      <FolderActionsTable
        items={actions ?? []}
        onUpdate={(recId, actionId, patch) => run(() => api.updateAction(recId, actionId, patch), "workspace:errUpdateAction", ["section-actions", id])}
        onToggleComplete={(actionId, completed) => run(() => api.completeActions([actionId], completed), "workspace:errUpdateAction", ["section-actions", id])}
        onDelete={(recId, actionId) => run(() => api.deleteAction(recId, actionId), "workspace:errRemoveAction", ["section-actions", id])}
      />
    ),
  };
  const notesTab: DetailTab = {
    key: "notes",
    label: t("workspace:detailTabNotes"),
    content: (
      <FolderNotesList
        items={notes ?? []}
        onEdit={(recId, noteId, text) => run(() => api.updateNote(recId, noteId, text), "workspace:errUpdateNote", ["section-notes", id])}
        onDelete={(recId, noteId) => run(() => api.deleteNote(recId, noteId), "workspace:errUpdateNote", ["section-notes", id])}
      />
    ),
  };
  const refreshFolderAttachments = () => {
    qc.invalidateQueries({ queryKey: ["folder-attachments", id] });
    qc.invalidateQueries({ queryKey: ["user-storage"] });
  };
  const attachmentsTab: DetailTab = {
    key: "attachments",
    label: t("workspace:detailTabAttachments"),
    content: (
      <div>
        {/* Folder-direct attachments (addable) */}
        <h3 className="px-4 pt-3 text-xs font-semibold uppercase tracking-wide text-gray-400 dark:text-gray-500">
          {t("workspace:folderOwnAttachmentsHeading")}
        </h3>
        <FolderAttachmentsManager
          sectionId={id}
          attachments={folderAttachments ?? []}
          onChange={refreshFolderAttachments}
        />

        {/* Attachments aggregated from the folder's transcripts (read-only) */}
        <h3 className="border-t px-4 pt-3 text-xs font-semibold uppercase tracking-wide text-gray-400 dark:border-gray-700 dark:text-gray-500">
          {t("workspace:folderTranscriptAttachmentsHeading")}
        </h3>
        <FolderAttachmentsList
          items={attachments ?? []}
          onChange={() => qc.invalidateQueries({ queryKey: ["section-attachments", id] })}
          onRemove={(a: SectionAttachmentItem) => {
            if (!window.confirm(t("workspace:confirmDeleteAttachment"))) return;
            run(() => api.deleteAttachment(a.recordingId, a.id), "workspace:errDeleteAttachment", ["section-attachments", id])
              .then(() => qc.invalidateQueries({ queryKey: ["user-storage"] }));
          }}
        />
      </div>
    ),
  };

  const tabs = [overview, minutes, actionsTab, notesTab, attachmentsTab];

  // ---- subheading: N transcripts · duration · first-last date ----
  const s = section.stats;
  const parts = [
    t("workspace:folderTranscriptsCount", { count: s.transcriptCount }),
    formatDurationHm(s.totalDurationMs),
    s.firstRecordingAt && s.lastRecordingAt
      ? `${formatDate(s.firstRecordingAt, i18n.language)} - ${formatDate(s.lastRecordingAt, i18n.language)}`
      : null,
  ].filter(Boolean);

  return (
    <div className="relative space-y-2.5">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          {renaming ? (
            <NameForm initial={section.name} onSave={saveName} onCancel={() => setRenaming(false)} placeholder={t("workspace:mtFieldTitle")} />
          ) : (
            <h1 className="text-lg font-semibold dark:text-gray-100">{section.name}</h1>
          )}
          <p className="text-xs text-gray-500 dark:text-gray-400">{parts.join(" · ")}</p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <ToolbarButton label={t("recordings:rename")} onClick={() => setRenaming(true)} icon={<PencilIcon />} />
          <ToolbarButton label={t("recordings:copyLink")} onClick={copyLink} icon={<LinkIcon />} />
          <KebabMenu actions={folderMenu({ onRename: () => setRenaming(true), onCopyLink: copyLink }, t)} />
        </div>
      </div>

      {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
      {info && <p className="text-sm text-green-600 dark:text-green-400">{info}</p>}

      <DetailTabs tabs={tabs} active={tab} onSelect={selectTab} />

      {editingSummary && section.summary && (
        <SummaryEditModal
          initial={section.summary.text}
          onClose={() => setEditingSummary(false)}
          onSave={async (text) => { await run(() => api.updateSectionSummary(id, text), "workspace:errEditSummary", ["section", id]); setEditingSummary(false); }}
        />
      )}
      {editingMinutes && section.minutes && (
        <MeetingMinutesEditModal
          initial={section.minutes.text}
          onClose={() => setEditingMinutes(false)}
          onSave={async (md) => { await run(() => api.updateSectionMinutes(id, md), "workspace:errMinutes", ["section", id]); setEditingMinutes(false); }}
        />
      )}
    </div>
  );
}

/// A small inline rename form (autofocus, Enter to save, Escape to cancel).
function NameForm({ initial, onSave, onCancel, placeholder }: {
  initial: string; onSave: (v: string) => void; onCancel: () => void; placeholder: string;
}) {
  const { t } = useTranslation("common");
  const [value, setValue] = useState(initial);
  return (
    <form
      onSubmit={(e) => { e.preventDefault(); if (value.trim()) onSave(value); }}
      className="flex items-center gap-1"
    >
      <input
        autoFocus
        value={value}
        placeholder={placeholder}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => e.key === "Escape" && onCancel()}
        className="rounded border px-2 py-1 text-lg font-semibold dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded bg-gray-900 px-2 py-1 text-sm text-white dark:bg-gray-100 dark:text-gray-900">{t("save")}</button>
      <button type="button" onClick={onCancel} className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:text-gray-200">{t("cancel")}</button>
    </form>
  );
}

const PencilIcon = () => (<svg {...iconProps}><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" /></svg>);
const RefreshIcon = () => (<svg {...iconProps}><path d="M23 4v6h-6" /><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" /></svg>);
const LinkIcon = () => (<svg {...iconProps}><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" /><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" /></svg>);
