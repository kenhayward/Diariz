import { useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDuration } from "../lib/format";
import { isSelected, spansForSegments } from "../lib/voiceprintSelection";
import type { Person, VoiceSample } from "../lib/types";

/// What a person's voiceprint was actually trained on, and which parts of it to keep.
///
/// The contributing recordings have been on `GET /api/people/{id}` since voiceprints shipped and were
/// rendered nowhere, which is why a drifting voiceprint had no diagnosis: you could see recognition was
/// wrong but not what it had learned from. Expanding a recording goes further and lets you drop the
/// segments where someone else was talking over them.
///
/// **Every duration is stated twice where the two differ** - what was selected, and what the worker
/// actually pooled after its cap. Showing only the selection would quietly promise something untrue.
export default function PersonVoiceprintTab({ person }: { person: Person }) {
  const { t } = useTranslation(["people", "common"]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["person", person.id],
    queryFn: () => api.getPerson(person.id),
    // Nothing to fetch or show for someone who opted out - and asking would be the wrong signal to send.
    enabled: !person.voiceprintOptOut,
    // A queued recompute is server-derived (`pending`), so polling picks up its result and the state
    // survives a reload - unlike a flag held in this component.
    refetchInterval: (query) => (query.state.data?.samples.some((s) => s.pending) ? 3000 : false),
  });

  if (person.voiceprintOptOut) {
    return (
      <div className="p-4">
        <p className="text-sm text-gray-600 dark:text-gray-300">{t("people:voiceprintOptedOut")}</p>
      </div>
    );
  }

  if (isLoading) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;
  if (isError) return <p className="p-4 text-sm text-red-600 dark:text-red-400">{t("people:errLoadFailed")}</p>;

  const samples = data?.samples ?? [];

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto p-4">
      <p className="text-xs text-gray-500 dark:text-gray-400">
        {t("people:voiceprintTrainedFrom", { count: samples.length })}
      </p>

      {samples.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t("people:voiceprintNoSamples")}</p>
      ) : (
        <ul className="space-y-2">
          {samples.map((s) => (
            <li key={s.id} className="rounded border p-3 text-sm dark:border-gray-700">
              <SampleRow
                personId={person.id}
                sample={s}
                // The server's answer, never recomputed here, or the two drift the first time either side
                // is edited. Someone may still SEE what a voiceprint learned from without it.
                canManage={person.canManageBiometrics}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/// One contributing recording: which speaker it came from, how much audio is behind the voiceprint, and -
/// once expanded - which of that speaker's segments to keep.
function SampleRow({
  personId,
  sample,
  canManage,
}: {
  personId: string;
  sample: VoiceSample;
  canManage: boolean;
}) {
  const { t } = useTranslation(["people", "common"]);
  const qc = useQueryClient();
  const [expanded, setExpanded] = useState(false);
  /// The ticked segment ids, or null before the segments have arrived and seeded it.
  const [ticked, setTicked] = useState<Set<string> | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Fetched only once expanded: the directory should not pull a transcript for every row you look at.
  const { data: recording } = useQuery({
    queryKey: ["recording", sample.recordingId],
    queryFn: () => api.getRecording(sample.recordingId),
    enabled: expanded,
  });

  const segments = useMemo(
    () => (recording?.current?.segments ?? []).filter((s) => s.speaker === sample.speakerLabel),
    [recording, sample.speakerLabel],
  );

  // Seed from what is stored the first time the segments land. No spans means the whole speaker, so an
  // untouched voiceprint shows everything ticked rather than nothing.
  const effective = useMemo(() => {
    if (ticked) return ticked;
    return new Set(segments.filter((s) => isSelected(s, sample.spans)).map((s) => s.id));
  }, [ticked, segments, sample.spans]);

  function toggle(id: string) {
    const next = new Set(effective);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setTicked(next);
  }

  async function recompute() {
    const picked = segments.filter((s) => effective.has(s.id));
    // Everything ticked means the whole speaker, sent as an empty selection rather than a snapshot of
    // today's boundaries - a re-transcribe would move them, and pinned spans would then describe audio
    // that no longer lines up.
    const spans = picked.length === segments.length ? [] : spansForSegments(picked);

    setBusy(true);
    setError(null);
    try {
      await api.setVoiceSampleSpans(personId, sample.id, spans);
      setTicked(null); // the server's answer takes over again
      await qc.invalidateQueries({ queryKey: ["person", personId] });
    } catch (e) {
      setError(apiErrorMessage(e, t("people:errRecomputeFailed")));
    } finally {
      setBusy(false);
    }
  }

  // Pending first: while a recompute is in flight the old figures describe a selection that is no longer
  // the one being used, so stating them would be worse than saying nothing.
  const duration = sample.pending
    ? t("people:voiceprintRecomputing")
    : sample.usedMs != null && sample.usedMs < sample.selectedMs
      ? t("people:voiceprintUsingOf", {
          used: formatDuration(sample.usedMs),
          selected: formatDuration(sample.selectedMs),
        })
      : formatDuration(sample.selectedMs);

  return (
    <div>
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="font-medium text-gray-900 dark:text-gray-50">{sample.recordingName}</span>
        <span className="text-xs text-gray-500 dark:text-gray-400">{sample.speakerLabel}</span>
        <span className="text-xs text-gray-500 dark:text-gray-400">{duration}</span>
        {sample.stale && (
          <span className="rounded bg-amber-100 px-1.5 py-0.5 text-xs text-amber-900 dark:bg-amber-900/40 dark:text-amber-200">
            {t("people:voiceprintNeedsRecompute")}
          </span>
        )}
        {canManage && (
          <button
            type="button"
            onClick={() => setExpanded((v) => !v)}
            className="ml-auto text-xs underline text-gray-600 dark:text-gray-300"
          >
            {expanded ? t("people:voiceprintHideSegments") : t("people:voiceprintShowSegments")}
          </button>
        )}
      </div>

      {expanded && (
        <div className="mt-3 border-t pt-3 dark:border-gray-700">
          {segments.length === 0 ? (
            <p className="text-xs text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
          ) : (
            <>
              <ul className="max-h-64 space-y-1 overflow-y-auto">
                {segments.map((s) => (
                  <li key={s.id} className="flex items-start gap-2">
                    <input
                      type="checkbox"
                      checked={effective.has(s.id)}
                      onChange={() => toggle(s.id)}
                      aria-label={s.text}
                      className="mt-1 shrink-0"
                    />
                    <span className="min-w-0 flex-1 text-xs text-gray-700 dark:text-gray-200">{s.text}</span>
                    <span className="shrink-0 font-mono text-[10px] text-gray-400 dark:text-gray-500">
                      {formatDuration(s.endMs - s.startMs)}
                    </span>
                  </li>
                ))}
              </ul>

              <div className="mt-3 flex items-center gap-3">
                <button
                  type="button"
                  onClick={recompute}
                  // Nothing ticked would be sent as "the whole speaker", which is the opposite of what
                  // unticking everything looks like it should do. Refuse rather than surprise.
                  disabled={busy || effective.size === 0}
                  className="rounded border px-2 py-1 text-xs disabled:opacity-50 dark:border-gray-700 dark:text-gray-200"
                >
                  {t("people:voiceprintRecompute")}
                </button>
                {effective.size === 0 && (
                  <span className="text-xs text-gray-500 dark:text-gray-400">
                    {t("people:voiceprintPickAtLeastOne")}
                  </span>
                )}
              </div>
            </>
          )}
          {error && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
        </div>
      )}
    </div>
  );
}
