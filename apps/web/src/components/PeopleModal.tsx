import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import type { SpeakerProfile } from "../lib/types";

/// Manage enrolled people / voiceprints: rename, view & prune training contributions, merge two people,
/// and erase one or all (GDPR). Voiceprints are biometric data — erasure reverts auto-applied labels.
export default function PeopleModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("people");
  const qc = useQueryClient();
  const { data: people = [], isLoading } = useQuery({
    queryKey: ["speaker-profiles"],
    queryFn: api.listSpeakerProfiles,
  });
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  /// Run an action, then refresh the list (and any open detail). Surfaces errors in the banner.
  async function act(fn: () => Promise<unknown>, detailId?: string) {
    setError(null);
    try {
      await fn();
      qc.invalidateQueries({ queryKey: ["speaker-profiles"] });
      if (detailId) qc.invalidateQueries({ queryKey: ["speaker-profile", detailId] });
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function eraseAll() {
    if (!window.confirm(t("confirmEraseAll"))) return;
    await act(() => api.deleteAllSpeakerProfiles());
  }

  // Shared audio element so playing one sample stops the previous; resolve the presigned URL lazily.
  const audioRef = useRef<HTMLAudioElement>(null);
  const audioSrcRecording = useRef<string | null>(null);
  async function playSample(recordingId: string, startMs: number) {
    const el = audioRef.current;
    if (!el) return;
    setError(null);
    try {
      if (audioSrcRecording.current !== recordingId) {
        el.src = await api.audioUrl(recordingId);
        audioSrcRecording.current = recordingId;
      }
      el.currentTime = startMs / 1000;
      await el.play();
    } catch (e) {
      setError(apiErrorMessage(e, t("errPlaySample")));
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("title")}
        className="max-h-[85vh] w-full max-w-2xl overflow-y-auto rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-1 text-base font-semibold dark:text-gray-100">{t("title")}</h2>
        <p className="mb-3 text-xs text-gray-500 dark:text-gray-400">{t("description")}</p>

        {error && <p className="mb-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
        {isLoading && <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>}
        {!isLoading && people.length === 0 && (
          <p className="text-sm text-gray-500 dark:text-gray-400">{t("empty")}</p>
        )}

        <ul className="divide-y dark:divide-gray-800">
          {people.map((p) => (
            <PersonRow
              key={p.id}
              person={p}
              others={people.filter((o) => o.id !== p.id)}
              expanded={expandedId === p.id}
              onToggle={() => setExpandedId((cur) => (cur === p.id ? null : p.id))}
              act={act}
              onPlay={playSample}
            />
          ))}
        </ul>

        {/* Shared player for listening to training samples. */}
        <audio ref={audioRef} controls className="mt-3 w-full" />

        <div className="mt-4 flex items-center justify-between border-t pt-3 dark:border-gray-700">
          {people.length > 0 ? (
            <button
              onClick={eraseAll}
              className="rounded border border-red-300 px-3 py-1.5 text-sm text-red-600 dark:border-red-800 dark:text-red-400"
            >
              {t("eraseAll")}
            </button>
          ) : (
            <span />
          )}
          <button
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </div>
  );
}

function PersonRow({
  person,
  others,
  expanded,
  onToggle,
  act,
  onPlay,
}: {
  person: SpeakerProfile;
  others: SpeakerProfile[];
  expanded: boolean;
  onToggle: () => void;
  act: (fn: () => Promise<unknown>, detailId?: string) => Promise<void>;
  onPlay: (recordingId: string, startMs: number) => void;
}) {
  const { t } = useTranslation("people");
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(person.name);
  const { data: detail } = useQuery({
    queryKey: ["speaker-profile", person.id],
    queryFn: () => api.getSpeakerProfile(person.id),
    enabled: expanded,
  });

  async function saveName() {
    const trimmed = name.trim();
    setRenaming(false);
    if (trimmed && trimmed !== person.name) await act(() => api.renameSpeakerProfile(person.id, trimmed), person.id);
  }

  return (
    <li className="py-2 text-sm dark:text-gray-200">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="flex min-w-0 items-center gap-2">
          <button
            aria-label={expanded ? t("collapse", { name: person.name }) : t("expand", { name: person.name })}
            onClick={onToggle}
            className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
          >
            {expanded ? "▾" : "▸"}
          </button>
          {renaming ? (
            <input
              autoFocus
              value={name}
              aria-label={t("rename", { name: person.name })}
              onChange={(e) => setName(e.target.value)}
              onBlur={saveName}
              onKeyDown={(e) => {
                if (e.key === "Enter") saveName();
                if (e.key === "Escape") {
                  setName(person.name);
                  setRenaming(false);
                }
              }}
              className="w-40 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          ) : (
            <button onClick={() => setRenaming(true)} className="truncate font-medium hover:underline">
              {person.name}
            </button>
          )}
          <span className="shrink-0 text-xs text-gray-500 dark:text-gray-400">
            {t("sampleCount", { count: person.sampleCount })}
          </span>
        </span>

        <span className="flex shrink-0 items-center gap-1.5">
          {others.length > 0 && (
            <select
              aria-label={t("mergeAria", { name: person.name })}
              value=""
              onChange={(e) => {
                const sourceId = e.target.value;
                if (!sourceId) return;
                const src = others.find((o) => o.id === sourceId);
                if (src && window.confirm(t("confirmMerge", { source: src.name, target: person.name })))
                  act(() => api.mergeSpeakerProfiles(person.id, sourceId), person.id);
              }}
              className="rounded border px-1 py-1 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            >
              <option value="">{t("mergeIn")}</option>
              {others.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.name}
                </option>
              ))}
            </select>
          )}
          <button
            onClick={() => {
              if (window.confirm(t("confirmDelete", { name: person.name })))
                act(() => api.deleteSpeakerProfile(person.id));
            }}
            className="rounded border border-red-300 px-2 py-1 text-xs text-red-600 dark:border-red-800 dark:text-red-400"
          >
            {t("delete")}
          </button>
        </span>
      </div>

      {expanded && (
        <div className="mt-2 pl-6">
          {detail && (
            <p className="mb-1 text-xs text-gray-500 dark:text-gray-400">
              {t("statsLabels", { count: detail.identifiedCount })} ·{" "}
              {t("statsTrained", { count: detail.contributions.length })}
            </p>
          )}
          <ul className="space-y-1">
            {detail?.contributions.map((c) => (
              <li key={c.id} className="flex items-center justify-between gap-2 text-xs">
                <span className="min-w-0 truncate">
                  <span className="font-medium">{c.recordingName}</span>
                  <span className="text-gray-500 dark:text-gray-400"> · {c.speakerLabel}</span>
                </span>
                <span className="flex shrink-0 gap-1.5">
                  <button
                    aria-label={t("playAria", { name: c.recordingName })}
                    onClick={() => onPlay(c.recordingId, c.startMs)}
                    className="rounded border px-2 py-0.5 text-[11px] dark:border-gray-700"
                  >
                    {t("play")}
                  </button>
                  <button
                    aria-label={t("removeAria", { name: c.recordingName })}
                    onClick={() => act(() => api.removeProfileContribution(person.id, c.id), person.id)}
                    className="rounded border px-2 py-0.5 text-[11px] dark:border-gray-700"
                  >
                    {t("common:remove")}
                  </button>
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </li>
  );
}
