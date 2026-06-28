import { useEffect, useMemo, useRef, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu from "../components/KebabMenu";
import MoveToSectionModal from "../components/MoveToSectionModal";
import DownloadTranscriptModal from "../components/DownloadTranscriptModal";
import { recordingMenu } from "../components/recordingMenu";
import { formatBytes } from "../lib/format";
import { allSpeakersAssigned } from "../lib/speakers";
import type { SegmentDto, SpeakerInfo, SpeakerProfile } from "../lib/types";

function fmt(ms: number): string {
  const s = Math.floor(ms / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

export default function RecordingDetail() {
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const { data: rec } = useQuery({
    queryKey: ["recording", id],
    queryFn: () => api.getRecording(id),
    enabled: Boolean(id),
  });
  const { data: profiles = [] } = useQuery({
    queryKey: ["speaker-profiles"],
    queryFn: api.listSpeakerProfiles,
  });

  const audioRef = useRef<HTMLAudioElement>(null);
  const [activeIdx, setActiveIdx] = useState<number | null>(null);
  const [requeuing, setRequeuing] = useState(false);
  const [summarizing, setSummarizing] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [editingSeg, setEditingSeg] = useState<SegmentDto | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionInfo, setActionInfo] = useState<string | null>(null);
  const [retranscribeOpen, setRetranscribeOpen] = useState(false);
  // Default the speaker panel collapsed when every speaker is already assigned (nothing left to label),
  // expanded otherwise. Decided once when the recording first loads; manual toggles win after that.
  const [speakersCollapsed, setSpeakersCollapsed] = useState<boolean | null>(null);
  const collapsed = speakersCollapsed ?? false;
  function toggleSpeakers() {
    setSpeakersCollapsed((v) => !(v ?? false));
  }

  useEffect(() => {
    const hub = createHub((e) => {
      if (e.recordingId === id) qc.invalidateQueries({ queryKey: ["recording", id] });
    });
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [id, qc]);

  useEffect(() => {
    if (speakersCollapsed === null && rec) setSpeakersCollapsed(allSpeakersAssigned(rec.speakers));
  }, [rec, speakersCollapsed]);

  const labels = useMemo(() => {
    const set = new Set<string>();
    rec?.current?.segments.forEach((s) => set.add(s.speaker));
    return [...set];
  }, [rec]);

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
      setActionError(apiErrorMessage(e, "Could not reassign the speaker."));
    }
  }

  async function newPerson(label: string) {
    const name = window.prompt("Name for this person?")?.trim();
    if (!name) return;
    setActionError(null);
    try {
      await api.createSpeakerProfile(name, id, label);
      qc.invalidateQueries({ queryKey: ["recording", id] });
      qc.invalidateQueries({ queryKey: ["speaker-profiles"] });
    } catch (e) {
      setActionError(apiErrorMessage(e, "Could not create the person."));
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
      setActionInfo("Re-transcribing…");
    } catch (e) {
      setActionError(apiErrorMessage(e, "Could not start re-transcription."));
    } finally {
      setRequeuing(false);
    }
  }

  async function summarize() {
    setActionError(null);
    setSummarizing(true);
    try {
      await api.summarize(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, "Could not start summarisation."));
    } finally {
      setSummarizing(false);
    }
  }

  async function mergeSegments() {
    if (!window.confirm("Merge consecutive rows from the same speaker into single blocks? Re-transcribe to undo.")) return;
    setActionError(null);
    setActionInfo(null);
    try {
      await api.mergeSegments(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, "Could not merge the transcript."));
    }
  }

  async function emailTranscript() {
    setActionError(null);
    setActionInfo(null);
    try {
      await api.emailTranscript(id);
      setActionInfo("Transcript emailed to your account address.");
    } catch (e) {
      setActionError(apiErrorMessage(e, "Could not email the transcript."));
    }
  }

  async function reidentify() {
    setActionError(null);
    setActionInfo(null);
    try {
      await api.reidentify(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setActionInfo("Re-ran speaker identification against your current voiceprints.");
    } catch (e) {
      setActionError(apiErrorMessage(e, "Could not re-identify speakers."));
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
      setActionError(apiErrorMessage(e, "Could not play audio."));
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

  if (!rec) return <p className="text-sm text-gray-500 dark:text-gray-400">Loading…</p>;

  const hasTranscript = (rec.current?.segments.length ?? 0) > 0;
  const isSummarizing = rec.status === "Summarizing" || summarizing;

  const menuActions = recordingMenu({
    onRename: () => setRenaming(true),
    onRetranscribe: () => setRetranscribeOpen(true),
    onSummarise: summarize,
    onReidentify: reidentify,
    onMove: () => setMoving(true),
    onPlay: () => void playFrom(0),
    onDownloadTranscript: () => setDownloading(true),
    onEmailTranscript: emailTranscript,
    onDownloadAudio: () => void api.downloadAudio(id),
    onDelete: async () => {
      if (!window.confirm(`Delete "${rec.name ?? rec.title}"? This cannot be undone.`)) return;
      await api.deleteRecording(id);
      navigate("/");
    },
    hasTranscript,
    isSummarizing,
  });

  return (
    <div className="space-y-5">
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
            {rec.source === "System" ? "System audio" : "Microphone"} ·{" "}
            {new Date(rec.createdAt).toLocaleString()} · {rec.status}
            {rec.sizeBytes > 0 ? ` · ${formatBytes(rec.sizeBytes)}` : ""}
            {rec.current?.language ? ` · ${rec.current.language}` : ""}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {(isSummarizing || requeuing) && (
            <span className="text-xs text-gray-500 dark:text-gray-400">
              {isSummarizing ? "Summarising…" : "Queuing…"}
            </span>
          )}
          <KebabMenu actions={menuActions} />
        </div>
      </div>

      {actionError && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{actionError}</p>
      )}

      {actionInfo && (
        <p className="rounded bg-green-50 p-3 text-sm text-green-700 dark:bg-green-900/30 dark:text-green-300">{actionInfo}</p>
      )}

      {rec.status === "Failed" && rec.error && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{rec.error}</p>
      )}

      {isSummarizing && !rec.summary && (
        <p className="rounded bg-amber-50 p-3 text-sm text-amber-800 dark:bg-amber-900/30 dark:text-amber-300">Summarising…</p>
      )}
      {rec.summary && (
        <div className="rounded-lg border bg-white p-4 dark:border-gray-700 dark:bg-gray-900">
          <h2 className="mb-2 text-sm font-medium text-gray-500 dark:text-gray-400">Summary</h2>
          <p className="whitespace-pre-wrap text-sm text-gray-800 dark:text-gray-200">{rec.summary.text}</p>
        </div>
      )}

      {labels.length > 0 && (
        <div className="rounded-lg border bg-white p-4 dark:border-gray-700 dark:bg-gray-900">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-medium text-gray-500 dark:text-gray-400">Speakers</h2>
            <button
              type="button"
              aria-label={collapsed ? "Expand speakers panel" : "Collapse speakers panel"}
              aria-expanded={!collapsed}
              onClick={toggleSpeakers}
              className="rounded px-1 text-xs text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:hover:bg-gray-800 dark:hover:text-gray-200"
            >
              {collapsed ? "▸" : "▾"}
            </button>
          </div>
          {!collapsed && (
          <div className="mt-2 flex flex-wrap gap-4">
            {labels.map((label) => {
              const info = rec.speakers.find((s) => s.label === label);
              return (
                <SpeakerRow
                  key={label}
                  label={label}
                  info={info}
                  initial={rec.speakerNames[label] ?? info?.displayName ?? label}
                  profiles={profiles}
                  onRename={(name) => rename(label, name)}
                  onAssign={(profileId) => assignSpeaker(label, profileId)}
                  onNewPerson={() => newPerson(label)}
                />
              );
            })}
          </div>
          )}
        </div>
      )}

      {rec.current ? (
        <>
          <div className="flex flex-wrap items-center gap-3">
            <button
              onClick={() => playFrom(0)}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              ▶ Play all
            </button>
            <button
              onClick={mergeSegments}
              title="Combine consecutive rows from the same speaker into single blocks"
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              Merge same-speaker rows
            </button>
            <audio ref={audioRef} controls onTimeUpdate={onTimeUpdate} className="h-8 min-w-48 flex-1" />
          </div>
          <ul className="space-y-2">
            {rec.current.segments.map((s, i) => (
              <SegmentRow
                key={s.id}
                seg={s}
                active={i === activeIdx}
                onPlay={() => playFrom(s.startMs)}
                onEdit={() => setEditingSeg(s)}
              />
            ))}
          </ul>
        </>
      ) : (
        <p className="text-sm text-gray-500 dark:text-gray-400">
          No transcript yet — it appears here automatically when transcription finishes.
        </p>
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

      {moving && <MoveToSectionModal recordingId={id} onClose={() => setMoving(false)} />}
      {downloading && <DownloadTranscriptModal recordingId={id} onClose={() => setDownloading(false)} />}

      {retranscribeOpen && (
        <RetranscribeModal
          initialMin={rec.minSpeakers}
          initialMax={rec.maxSpeakers}
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
  busy,
  onCancel,
  onConfirm,
}: {
  initialMin: number | null;
  initialMax: number | null;
  busy: boolean;
  onCancel: () => void;
  onConfirm: (min: number | null, max: number | null) => void;
}) {
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
        aria-label="Re-transcribe"
        className="w-full max-w-md rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-1 text-base font-semibold dark:text-gray-100">Re-transcribe</h2>
        <p className="mb-4 text-xs text-gray-500 dark:text-gray-400">
          Optionally tell the diarizer how many speakers to expect. If two people were merged into one
          speaker, set the minimum to 2. Leave blank for automatic detection.
        </p>
        <div className="flex items-center gap-4 text-sm">
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            Min speakers
            <input
              type="number"
              min={1}
              value={min}
              onChange={(e) => setMin(e.target.value)}
              aria-label="Minimum speakers"
              className="w-20 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            Max speakers
            <input
              type="number"
              min={1}
              value={max}
              onChange={(e) => setMax(e.target.value)}
              aria-label="Maximum speakers"
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
            Cancel
          </button>
          <button
            type="button"
            onClick={confirm}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? "Starting…" : "Re-transcribe"}
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
  onSave: (text: string) => Promise<void>;
}) {
  const [text, setText] = useState(seg.text);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await onSave(text);
    } catch (e) {
      setError(apiErrorMessage(e, "Could not save the segment."));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label="Edit segment"
        className="w-full max-w-lg rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">Edit segment</h2>
        <textarea
          autoFocus
          value={text}
          onChange={(e) => setText(e.target.value)}
          rows={4}
          aria-label="Segment text"
          className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
        <div className="mt-3 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={save}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? "Saving…" : "Save"}
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
        placeholder="Recording name"
        aria-label="Recording name"
        className="w-64 rounded border px-2 py-1 text-base dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800">
        Save
      </button>
    </form>
  );
}

const NEW_PERSON = "__new__";

export function SpeakerRow({
  label,
  info,
  initial,
  profiles,
  onRename,
  onAssign,
  onNewPerson,
}: {
  label: string;
  info: SpeakerInfo | undefined;
  initial: string;
  profiles: SpeakerProfile[];
  onRename: (name: string) => void;
  onAssign: (profileId: string | null) => void;
  onNewPerson: () => void;
}) {
  const [value, setValue] = useState(initial);
  // Keep the field in sync when identification/reassignment changes the name out from under us.
  useEffect(() => setValue(initial), [initial]);

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-center gap-1">
        <span className="text-xs text-gray-400 dark:text-gray-500">{label}</span>
        {info?.identifiedAuto && (
          <span
            title="Name applied automatically by speaker identification"
            className="rounded bg-blue-100 px-1 text-[10px] font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
          >
            auto
          </span>
        )}
      </div>
      <input
        value={value}
        aria-label={`Name for ${label}`}
        onChange={(e) => setValue(e.target.value)}
        onBlur={() => value !== initial && onRename(value)}
        className="w-40 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <select
        value={info?.profileId ?? ""}
        aria-label={`Assign ${label} to a person`}
        onChange={(e) => {
          const v = e.target.value;
          if (v === NEW_PERSON) onNewPerson();
          else onAssign(v || null);
        }}
        className="w-40 rounded border px-2 py-1 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      >
        <option value="">Unassigned</option>
        {profiles.map((p) => (
          <option key={p.id} value={p.id}>
            {p.name}
          </option>
        ))}
        <option value={NEW_PERSON}>+ New person…</option>
      </select>
    </div>
  );
}

function SegmentRow({
  seg,
  active,
  onPlay,
  onEdit,
}: {
  seg: SegmentDto;
  active: boolean;
  onPlay: () => void;
  onEdit: () => void;
}) {
  return (
    <li
      onClick={onPlay}
      className={`flex cursor-pointer items-start gap-3 rounded-lg border px-4 py-2 hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800 ${
        active
          ? "border-blue-300 bg-blue-50 dark:border-blue-700 dark:bg-blue-900/30"
          : "bg-white dark:bg-gray-900"
      }`}
    >
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400 dark:text-gray-500">{fmt(seg.startMs)}</span>
      <span className="w-28 shrink-0 text-sm font-medium text-gray-700 dark:text-gray-200">{seg.speakerDisplay}</span>
      {/* Auto-expands vertically to show the full (possibly merged) block of text. */}
      <span className="flex-1 whitespace-pre-wrap break-words text-sm dark:text-gray-200">{seg.text}</span>
      <KebabMenu actions={[{ label: "Edit", onClick: onEdit }]} label="Segment actions" />
    </li>
  );
}
