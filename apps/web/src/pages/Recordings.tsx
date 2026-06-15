import { useEffect } from "react";
import { Link } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../lib/api";
import { createHub } from "../lib/signalr";
import Recorder from "../components/Recorder";
import type { RecordingStatus } from "../lib/types";

const statusColor: Record<RecordingStatus, string> = {
  Uploaded: "bg-gray-100 text-gray-700",
  Queued: "bg-amber-100 text-amber-800",
  Transcribing: "bg-amber-100 text-amber-800",
  Transcribed: "bg-green-100 text-green-800",
  Summarized: "bg-green-100 text-green-800",
  Failed: "bg-red-100 text-red-800",
};

export default function Recordings() {
  const qc = useQueryClient();
  const { data: recordings = [], isLoading } = useQuery({
    queryKey: ["recordings"],
    queryFn: api.listRecordings,
  });

  // Live status updates over SignalR refresh the list.
  useEffect(() => {
    const hub = createHub(() => qc.invalidateQueries({ queryKey: ["recordings"] }));
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [qc]);

  return (
    <div className="space-y-6">
      <Recorder onUploaded={() => qc.invalidateQueries({ queryKey: ["recordings"] })} />

      <div>
        <h2 className="mb-2 text-sm font-medium text-gray-500">Recordings</h2>
        {isLoading ? (
          <p className="text-sm text-gray-500">Loading…</p>
        ) : recordings.length === 0 ? (
          <p className="text-sm text-gray-500">No recordings yet. Hit Record above.</p>
        ) : (
          <ul className="divide-y rounded-lg border bg-white">
            {recordings.map((r) => (
              <li key={r.id}>
                <Link
                  to={`/recordings/${r.id}`}
                  className="flex items-center justify-between px-4 py-3 hover:bg-gray-50"
                >
                  <div>
                    <div className="font-medium">{r.title}</div>
                    <div className="text-xs text-gray-500">
                      {new Date(r.createdAt).toLocaleString()} ·{" "}
                      {Math.round(r.durationMs / 1000)}s
                    </div>
                  </div>
                  <span className={`rounded px-2 py-0.5 text-xs ${statusColor[r.status]}`}>
                    {r.status}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
