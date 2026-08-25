import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDuration } from "../lib/format";
import { clipQueue, type ClipRequest } from "../lib/clipPlayback";
import { isSelected, spansForSegments } from "../lib/voiceprintSelection";
import type { AttributionSegment, PersonAttribution, VoiceSample } from "../lib/types";

/// One recording this person appears in: how they came to be attributed there, whether that speaker trains
/// the voiceprint, and - once expanded - exactly which of their segments do.
///
/// The row exists for every attributed speaker, not only enrolled ones. Automatic identification links a
/// speaker without creating a voice sample, so a list built from samples showed a fraction of where a
/// voiceprint is actually being used and read as an arbitrary subset.
export default function PersonAttributionRow({
  personId,
  attribution,
  sample,
  canManage,
  onPlay,
  onStop,
  onChanged,
  playingSegmentId,
}: {
  personId: string;
  attribution: PersonAttribution;
  /// The sample behind this speaker, when one exists. Null for a speaker that trains nothing, and also
  /// present-but-excluded for one that used to.
  sample: VoiceSample | null;
  canManage: boolean;
  onPlay: (speakerId: string, queue: ClipRequest[]) => void;
  onStop: () => void;
  /// Called after a change lands, so the tab can re-read both the attributions and the samples behind them.
  onChanged: () => void;
  playingSegmentId: string | null;
}) {
  const { t } = useTranslation(["people", "common"]);
  const [expanded, setExpanded] = useState(false);
  /// The ticked segment ids, or null before the segments have arrived and seeded it.
  const [ticked, setTicked] = useState<Set<string> | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Fetched only once expanded: the directory should not pull a transcript for every row you look at.
  const { data: segments = [] } = useQuery<AttributionSegment[]>({
    queryKey: ["attributionSegments", personId, attribution.speakerId],
    queryFn: () => api.getAttributionSegments(personId, attribution.speakerId),
    enabled: expanded && attribution.canAccessRecording,
  });

  // Seed from what is stored the first time the segments land. No spans means the whole speaker, so an
  // untouched voiceprint shows everything ticked rather than nothing.
  const effective = useMemo(() => {
    if (ticked) return ticked;
    return new Set(segments.filter((s) => isSelected(s, sample?.spans ?? [])).map((s) => s.id));
  }, [ticked, segments, sample?.spans]);

  function toggleSegment(id: string) {
    const next = new Set(effective);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setTicked(next);
  }

  async function run(action: () => Promise<void>, fallback: string) {
    setBusy(true);
    setError(null);
    try {
      await action();
      onChanged();
    } catch (e) {
      setError(apiErrorMessage(e, fallback));
    } finally {
      setBusy(false);
    }
  }

  const toggleTraining = () =>
    run(
      () => api.setAttributionTraining(personId, attribution.speakerId, !attribution.isTraining),
      t("people:errTrainingFailed"),
    );

  const recompute = () =>
    run(async () => {
      const picked = segments.filter((s) => effective.has(s.id));
      // Everything ticked means the whole speaker, sent as an empty selection rather than a snapshot of
      // today's boundaries - a re-transcribe would move them, and pinned spans would then describe audio
      // that no longer lines up.
      const spans = picked.length === segments.length ? [] : spansForSegments(picked);
      await api.setVoiceSampleSpans(personId, sample!.id, spans);
      setTicked(null); // the server's answer takes over again
    }, t("people:errRecomputeFailed"));

  // Pending first: while a recompute is in flight the old figures describe a selection that is no longer the
  // one being used, so stating them would be worse than saying nothing.
  const trainedOn = !sample
    ? null
    : sample.pending
      ? t("people:voiceprintRecomputing")
      : sample.usedMs != null && sample.usedMs < sample.selectedMs
        ? t("people:voiceprintUsingOf", {
            used: formatDuration(sample.usedMs),
            selected: formatDuration(sample.selectedMs),
          })
        : formatDuration(sample.selectedMs);

  const playing = playingSegmentId != null && segments.some((s) => s.id === playingSegmentId);

  return (
    <div>
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="min-w-0 truncate font-medium text-gray-900 dark:text-gray-50">
          {attribution.recordingName}
        </span>
        <span className="text-xs text-gray-500 dark:text-gray-400">{attribution.speakerLabel}</span>
        <span className="text-xs text-gray-500 dark:text-gray-400">
          {attribution.linkedBy === "auto"
            ? t("people:attributionLinkedAuto")
            : t("people:attributionLinkedManual")}
        </span>
        <span className="text-xs text-gray-500 dark:text-gray-400">
          {formatDuration(attribution.speechMs)}
        </span>
        {trainedOn && (
          <span className="text-xs text-gray-500 dark:text-gray-400">
            {t("people:attributionTrainedOn", { duration: trainedOn })}
          </span>
        )}
        {sample?.stale && (
          <span className="rounded bg-amber-100 px-1.5 py-0.5 text-xs text-amber-900 dark:bg-amber-900/40 dark:text-amber-200">
            {t("people:voiceprintNeedsRecompute")}
          </span>
        )}
      </div>

      {!attribution.canAccessRecording ? (
        <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
          {t("people:attributionNoAccess")}
        </p>
      ) : (
        <div className="mt-2 flex flex-wrap items-center gap-3">
          {canManage && (
            <label className="flex items-center gap-1.5 text-xs text-gray-700 dark:text-gray-200">
              <input
                type="checkbox"
                checked={attribution.isTraining}
                disabled={busy}
                onChange={toggleTraining}
                aria-label={t("people:attributionTraining")}
              />
              {t("people:attributionTraining")}
            </label>
          )}
          <button
            type="button"
            onClick={() =>
              playing ? onStop() : onPlay(attribution.speakerId, clipQueue(segments, segments.map((s) => s.id)))
            }
            // Playing the voice needs the segments, and they only arrive once expanded. Expanding first keeps
            // one fetch path rather than a second lazy one that would race it.
            disabled={!expanded || segments.length === 0}
            className="text-xs underline text-gray-600 disabled:opacity-50 dark:text-gray-300"
          >
            {playing ? t("people:attributionStop") : t("people:attributionPlayVoice")}
          </button>
          <button
            type="button"
            onClick={() => setExpanded((v) => !v)}
            className="ml-auto text-xs underline text-gray-600 dark:text-gray-300"
          >
            {expanded ? t("people:voiceprintHideSegments") : t("people:voiceprintShowSegments")}
          </button>
        </div>
      )}

      {expanded && attribution.canAccessRecording && (
        <div className="mt-3 border-t pt-3 dark:border-gray-700">
          {segments.length === 0 ? (
            <p className="text-xs text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
          ) : (
            <>
              <ul className="max-h-64 space-y-1 overflow-y-auto">
                {segments.map((s) => (
                  <li key={s.id} className="flex items-start gap-2">
                    {canManage && sample && (
                      <input
                        type="checkbox"
                        checked={effective.has(s.id)}
                        onChange={() => toggleSegment(s.id)}
                        aria-label={s.text}
                        className="mt-1 shrink-0"
                      />
                    )}
                    <button
                      type="button"
                      onClick={() =>
                        playingSegmentId === s.id
                          ? onStop()
                          : onPlay(attribution.speakerId, clipQueue(segments, [s.id]))
                      }
                      aria-label={t("people:attributionPlaySegment")}
                      className="mt-0.5 shrink-0 text-xs underline text-gray-600 dark:text-gray-300"
                    >
                      {playingSegmentId === s.id
                        ? t("people:attributionStop")
                        : t("people:attributionPlaySegment")}
                    </button>
                    <span className="min-w-0 flex-1 text-xs text-gray-700 dark:text-gray-200">{s.text}</span>
                    <span className="shrink-0 font-mono text-[10px] text-gray-400 dark:text-gray-500">
                      {formatDuration(s.endMs - s.startMs)}
                    </span>
                  </li>
                ))}
              </ul>

              {canManage && sample && (
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
              )}
            </>
          )}
          {error && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
        </div>
      )}
      {error && !expanded && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
    </div>
  );
}
