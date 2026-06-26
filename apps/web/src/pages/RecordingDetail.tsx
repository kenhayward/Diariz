import { useEffect, useMemo, useRef, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu, { type KebabAction } from "../components/KebabMenu";
import type { SegmentDto } from "../lib/types";

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

  const audioRef = useRef<HTMLAudioElement>(null);
  const [activeIdx, setActiveIdx] = useState<number | null>(null);
  const [requeuing, setRequeuing] = useState(false);
  const [summarizing, setSummarizing] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

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

  async function rename(label: string, name: string) {
    await api.renameSpeaker(id, label, name);
    qc.invalidateQueries({ queryKey: ["recording", id] });
  }

  async function saveRecordingName(name: string) {
    await api.renameRecording(id, name.trim() || null);
    setRenaming(false);
    qc.invalidateQueries({ queryKey: ["recording", id] });
  }

  async function retranscribe() {
    setActionError(null);
    setRequeuing(true);
    try {
      await api.retranscribe(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
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

  if (!rec) return <p className="text-sm text-gray-500">Loading…</p>;

  const hasTranscript = (rec.current?.segments.length ?? 0) > 0;
  const isSummarizing = rec.status === "Summarizing" || summarizing;

  const menuActions: KebabAction[] = [
    { label: "Rename", onClick: () => setRenaming(true) },
    { label: "Download transcript (.txt)", onClick: () => void api.downloadTranscript(id, "txt"), disabled: !hasTranscript },
    { label: "Download transcript (.srt)", onClick: () => void api.downloadTranscript(id, "srt"), disabled: !hasTranscript },
    { label: "Download audio", onClick: () => void api.downloadAudio(id) },
    {
      label: "Delete",
      danger: true,
      onClick: async () => {
        if (!window.confirm(`Delete "${rec.name ?? rec.title}"? This cannot be undone.`)) return;
        await api.deleteRecording(id);
        navigate("/");
      },
    },
  ];

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
            <h1 className="text-lg font-semibold">{rec.name ?? rec.title}</h1>
          )}
          <p className="text-xs text-gray-500">
            {rec.source === "System" ? "System audio" : "Microphone"} ·{" "}
            {new Date(rec.createdAt).toLocaleString()} · {rec.status}
            {rec.current?.language ? ` · ${rec.current.language}` : ""}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <button
            onClick={summarize}
            disabled={isSummarizing || !hasTranscript}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSummarizing ? "Summarising…" : "Summarise"}
          </button>
          <button
            onClick={retranscribe}
            disabled={requeuing}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {requeuing ? "Queuing…" : "Re-transcribe"}
          </button>
          <KebabMenu actions={menuActions} />
        </div>
      </div>

      {actionError && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700">{actionError}</p>
      )}

      {rec.status === "Failed" && rec.error && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700">{rec.error}</p>
      )}

      {isSummarizing && !rec.summary && (
        <p className="rounded bg-amber-50 p-3 text-sm text-amber-800">Summarising…</p>
      )}
      {rec.summary && (
        <div className="rounded-lg border bg-white p-4">
          <h2 className="mb-2 text-sm font-medium text-gray-500">Summary</h2>
          <p className="whitespace-pre-wrap text-sm text-gray-800">{rec.summary.text}</p>
        </div>
      )}

      {labels.length > 0 && (
        <div className="rounded-lg border bg-white p-4">
          <h2 className="mb-2 text-sm font-medium text-gray-500">Speakers</h2>
          <div className="flex flex-wrap gap-3">
            {labels.map((label) => (
              <SpeakerInput
                key={label}
                label={label}
                initial={rec.speakerNames[label] ?? label}
                onSave={(name) => rename(label, name)}
              />
            ))}
          </div>
        </div>
      )}

      {rec.current ? (
        <>
          <div className="flex items-center gap-3">
            <button
              onClick={() => playFrom(0)}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
            >
              ▶ Play all
            </button>
            <audio ref={audioRef} controls onTimeUpdate={onTimeUpdate} className="h-8 flex-1" />
          </div>
          <ul className="space-y-2">
            {rec.current.segments.map((s, i) => (
              <SegmentRow
                key={i}
                seg={s}
                active={i === activeIdx}
                onPlay={() => playFrom(s.startMs)}
              />
            ))}
          </ul>
        </>
      ) : (
        <p className="text-sm text-gray-500">
          No transcript yet — it appears here automatically when transcription finishes.
        </p>
      )}
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
        className="w-64 rounded border px-2 py-1 text-base"
      />
      <button type="submit" className="rounded border px-2 py-1 text-sm hover:bg-gray-50">
        Save
      </button>
    </form>
  );
}

function SpeakerInput({
  label,
  initial,
  onSave,
}: {
  label: string;
  initial: string;
  onSave: (name: string) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <div className="flex items-center gap-1">
      <span className="text-xs text-gray-400">{label}</span>
      <input
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onBlur={() => value !== initial && onSave(value)}
        className="w-32 rounded border px-2 py-1 text-sm"
      />
    </div>
  );
}

function SegmentRow({
  seg,
  active,
  onPlay,
}: {
  seg: SegmentDto;
  active: boolean;
  onPlay: () => void;
}) {
  return (
    <li
      onClick={onPlay}
      className={`flex cursor-pointer gap-3 rounded-lg border px-4 py-2 hover:bg-gray-50 ${
        active ? "border-blue-300 bg-blue-50" : "bg-white"
      }`}
    >
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400">{fmt(seg.startMs)}</span>
      <span className="w-28 shrink-0 text-sm font-medium text-gray-700">{seg.speakerDisplay}</span>
      <span className="text-sm">{seg.text}</span>
    </li>
  );
}
