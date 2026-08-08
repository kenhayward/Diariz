import { useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import { hasTranscript, isProcessing } from "../../lib/recordingStatus";
import { copyRichLink, transcriptUrl } from "../../lib/clipboard";
import { useSharedRoomId, useRoomBasePath } from "../../lib/rooms";
import { useActiveRecordingId } from "../../lib/activeRoute";
import MoveToSectionModal from "../MoveToSectionModal";
import DownloadTranscriptModal from "../DownloadTranscriptModal";
import { recordingMenu } from "../recordingMenu";
import type { KebabAction } from "../KebabMenu";
import type { RecordingSummary } from "../../lib/types";

/// Everything a recording's "⋮" menu needs, so the list row and the Calendar tab's day-grid block drive the
/// same actions rather than each wiring `recordingMenu` themselves - two copies of twelve handlers would
/// drift silently the first time a menu item is added to one surface only.
///
/// Note `useTranslation()` takes **no namespace** here, for the same reason `RecordingRow` doesn't: every key
/// is explicitly prefixed (`workspace:`, `recordings:`, `common:`) and `recordingMenu` expects that same `t`.
/// Adding a default namespace would silently break every label on both surfaces.
export interface RecordingActions {
  /// Ready for `<KebabMenu actions={...} />`.
  actions: KebabAction[];
  /// The modals an action can open (move-to-section, download-transcript). Render anywhere inside the row
  /// or block; they are identical on both surfaces.
  modals: ReactNode;
  /// The last action failure, returned rather than rendered: a list row shows it as a line underneath,
  /// but a 22px calendar block has no room and surfaces it in the tooltip instead.
  error: string | null;
  renaming: boolean;
  saveName: (name: string) => Promise<void>;
  cancelRename: () => void;
}

export function useRecordingActions(r: RecordingSummary): RecordingActions {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const basePath = useRoomBasePath();
  const sharedRoomId = useSharedRoomId();
  const activeRecordingId = useActiveRecordingId();
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => qc.invalidateQueries({ queryKey: ["recordings"] });
  const run = (fn: () => Promise<unknown>) => async () => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  };

  async function saveName(name: string) {
    await api.renameRecording(r.id, name.trim() || null);
    setRenaming(false);
    refresh();
  }

  const actions = recordingMenu({
    onRename: () => setRenaming(true),
    onCopyLink: run(() => copyRichLink(transcriptUrl(r.id), r.name ?? r.title)),
    onRetranscribe: run(async () => { await api.retranscribe(r.id); refresh(); }),
    onSummarise: run(async () => { await api.summarize(r.id); refresh(); }),
    onExtractActions: run(async () => {
      if (r.hasActions && !window.confirm(t("workspace:confirmReextract"))) return;
      await api.extractActions(r.id);
      refresh();
    }),
    onReidentify: run(async () => { await api.reidentify(r.id); refresh(); }),
    onMove: () => setMoving(true),
    onDownloadTranscript: () => setDownloading(true),
    onEmailTranscript: run(() => api.emailTranscript(r.id)),
    onDownloadAudio: run(() => api.downloadAudio(r.id)),
    onDeleteAudio: run(async () => {
      if (!window.confirm(t("workspace:confirmDeleteAudio", { name: r.name ?? r.title }))) return;
      await api.deleteAudio(r.id);
      refresh();
      qc.invalidateQueries({ queryKey: ["user-storage"] }); // freed quota → refresh the account menu
    }),
    onDelete: run(async () => {
      if (!window.confirm(t("workspace:confirmDelete", { name: r.name ?? r.title }))) return;
      await api.deleteRecording(r.id);
      // If the deleted recording is the one open in the detail panel, leave it — otherwise its transcript
      // stays on screen and any further action targets a now-missing recording.
      if (activeRecordingId === r.id) navigate(basePath || "/");
      refresh();
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    }),
    hasTranscript: hasTranscript(r.status),
    hasAudio: r.hasAudio,
    isSummarizing: r.status === "Summarizing",
    isProcessing: isProcessing(r.status),
  }, t);

  const modals = (
    <>
      {moving && (
        <MoveToSectionModal recordingId={r.id} currentSectionId={r.sectionId} roomId={sharedRoomId} onClose={() => setMoving(false)} />
      )}
      {downloading && <DownloadTranscriptModal recordingId={r.id} onClose={() => setDownloading(false)} />}
    </>
  );

  return { actions, modals, error, renaming, saveName, cancelRename: () => setRenaming(false) };
}
