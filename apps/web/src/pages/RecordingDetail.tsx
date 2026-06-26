import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import type { SegmentDto } from "../lib/types";

function fmt(ms: number): string {
  const s = Math.floor(ms / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

export default function RecordingDetail() {
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const { data: rec } = useQuery({
    queryKey: ["recording", id],
    queryFn: () => api.getRecording(id),
    enabled: Boolean(id),
  });

  useEffect(() => {
    const hub = createHub((e) => {
      if (e.recordingId === id) qc.invalidateQueries({ queryKey: ["recording", id] });
    });
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [id, qc]);

  // Distinct speaker labels present in the current transcription.
  const labels = useMemo(() => {
    const set = new Set<string>();
    rec?.current?.segments.forEach((s) => set.add(s.speaker));
    return [...set];
  }, [rec]);

  const [requeuing, setRequeuing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  async function rename(label: string, name: string) {
    await api.renameSpeaker(id, label, name);
    qc.invalidateQueries({ queryKey: ["recording", id] });
  }

  async function retranscribe() {
    setActionError(null);
    setRequeuing(true);
    try {
      await api.retranscribe(id);
      // Refetch so the page reflects the new queued state immediately rather than
      // waiting on a SignalR event (which may not have delivered yet).
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, "Could not start re-transcription."));
    } finally {
      setRequeuing(false);
    }
  }

  if (!rec) return <p className="text-sm text-gray-500">Loading…</p>;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-semibold">{rec.title}</h1>
          <p className="text-xs text-gray-500">
            {new Date(rec.createdAt).toLocaleString()} · {rec.status}
            {rec.current?.language ? ` · ${rec.current.language}` : ""}
          </p>
        </div>
        <button
          onClick={retranscribe}
          disabled={requeuing}
          className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {requeuing ? "Queuing…" : "Re-transcribe"}
        </button>
      </div>

      {actionError && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700">{actionError}</p>
      )}

      {rec.status === "Failed" && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700">{rec.error}</p>
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
        <ul className="space-y-2">
          {rec.current.segments.map((s, i) => (
            <SegmentRow key={i} seg={s} />
          ))}
        </ul>
      ) : (
        <p className="text-sm text-gray-500">
          No transcript yet — it appears here automatically when transcription finishes.
        </p>
      )}
    </div>
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

function SegmentRow({ seg }: { seg: SegmentDto }) {
  return (
    <li className="flex gap-3 rounded-lg border bg-white px-4 py-2">
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400">{fmt(seg.startMs)}</span>
      <span className="w-28 shrink-0 text-sm font-medium text-gray-700">{seg.speakerDisplay}</span>
      <span className="text-sm">{seg.text}</span>
    </li>
  );
}
