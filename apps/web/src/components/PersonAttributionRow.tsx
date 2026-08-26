import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDuration } from "../lib/format";
import { clipQueue, type ClipRequest } from "../lib/clipPlayback";
import { isSelected, spansForSegments } from "../lib/voiceprintSelection";
import SpeakerAssign from "./SpeakerAssign";
import { similarityPercent, type RowVerdict } from "../lib/voiceprintVerdict";
import type { AttributionSegment, PersonAttribution, SampleDiagnosis, VoiceSample } from "../lib/types";

/// One recording this person appears in: how they came to be attributed there, whether that speaker trains
/// the voiceprint, and - once expanded - exactly which of their segments do.
///
/// The row exists for every attributed speaker, not only enrolled ones. Automatic identification links a
/// speaker without creating a voice sample, so a list built from samples showed a fraction of where a
/// voiceprint is actually being used and read as an arbitrary subset.
export default function PersonAttributionRow({
  personId,
  personName,
  attribution,
  sample,
  diagnosis,
  verdict,
  canManage,
  onPlay,
  onStop,
  onChanged,
  playingSegmentId,
}: {
  personId: string;
  /// Shown on the reassign control as the current answer, so the row states who it currently says this
  /// is rather than making you infer it from which person's card you are on.
  personName: string;
  attribution: PersonAttribution;
  /// The sample behind this speaker, when one exists. Null for a speaker that trains nothing, and also
  /// present-but-excluded for one that used to.
  sample: VoiceSample | null;
  /// How this recording compares with the person's others, when it is one of the ones being compared.
  /// Absent for a speaker that trains nothing - most rows, since automatic identification links a speaker
  /// without ever creating a sample.
  diagnosis?: SampleDiagnosis;
  verdict: RowVerdict;
  canManage: boolean;
  onPlay: (speakerId: string, queue: ClipRequest[]) => void;
  onStop: () => void;
  /// Called after a change lands, so the tab can re-read both the attributions and the samples behind them.
  onChanged: () => void;
  playingSegmentId: string | null;
}) {
  const { t } = useTranslation(["people", "common", "workspace"]);
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

  // All three go through `run`, so a refusal shows on the row and the lists re-read on success. Each is
  // gated by canReassign above, which reports recording ownership - the API enforces the same on all
  // three endpoints, so a control offered without it would simply always fail.
  const reassign = (toPersonId: string | null) =>
    run(
      () => api.assignSpeaker(attribution.recordingId, attribution.speakerLabel, toPersonId),
      t("people:errReassignFailed"),
    );

  const markMulti = () =>
    run(
      () => api.markMultiSpeaker(attribution.recordingId, attribution.speakerLabel),
      t("people:errReassignFailed"),
    );

  // Creates and assigns in one call, so someone not yet in the directory does not need a detour to add
  // them first.
  const createAndAssign = (name: string) =>
    run(
      async () => {
        await api.createPerson({
          name,
          recordingId: attribution.recordingId,
          label: attribution.speakerLabel,
        });
      },
      t("people:errReassignFailed"),
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

  // Collapsing takes every control for this row off the screen, including Stop. Leaving the clip running
  // would give the user audio they can no longer reach.
  function collapseOrExpand() {
    if (expanded && playing) onStop();
    setExpanded((v) => !v);
  }

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
        <VerdictChip verdict={verdict} impostorName={diagnosis?.nearestImpostorName} />
        {diagnosis?.nearestSiblingDistance != null && (
          <span className="text-xs text-gray-500 dark:text-gray-400">
            {t("people:vpClosestMatch", { value: similarityPercent(diagnosis.nearestSiblingDistance) })}
          </span>
        )}
        {diagnosis?.distanceToOthers != null && (
          <span className="text-xs text-gray-500 dark:text-gray-400">
            {t("people:vpMatchToRest", { value: similarityPercent(diagnosis.distanceToOthers) })}
          </span>
        )}
        {/* Said out loud rather than left as a row that is simply not ticked. Its sample was training the
            voiceprint with nothing on screen accounting for it - a silent row would be the same defect in a
            new place. */}
        {!attribution.stillLinked && (
          <span className="rounded bg-amber-100 px-1.5 py-0.5 text-xs text-amber-900 dark:bg-amber-900/40 dark:text-amber-200">
            {t("people:attributionUnlinked")}
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
          {/* Hidden rather than disabled. Playing the voice needs the segments, and they only arrive once
              expanded - so before that the control cannot work, and a greyed-out button reads as broken
              rather than as not yet applicable. */}
          {expanded && segments.length > 0 && (
            <button
              type="button"
              onClick={() =>
                playing
                  ? onStop()
                  : onPlay(attribution.speakerId, clipQueue(segments, segments.map((s) => s.id)))
              }
              className="text-xs underline text-gray-600 dark:text-gray-300"
            >
              {playing ? t("people:attributionStop") : t("people:attributionPlayVoice")}
            </button>
          )}
          {/* The same typeahead the transcript and Speakers tab use. A bespoke button here would be a
              second way to do something the app already does one way, and this one already searches the
              ungated people endpoint, offers Create for someone not in the directory, and offers
              Multiple speakers - which matters here, because overlapping speech is one of the commonest
              reasons a recording sounds unlike a person's others. */}
          {attribution.canReassign && (
            <SpeakerAssign
              label={attribution.speakerLabel}
              ariaLabel={t("people:attributionReassignAria", {
                label: attribution.speakerLabel,
                recording: attribution.recordingName,
              })}
              isMulti={false}
              displayName={attribution.stillLinked ? personName : undefined}
              width="w-48"
              subtle
              onAssign={reassign}
              onCreate={createAndAssign}
              onMulti={markMulti}
            />
          )}
          <button
            type="button"
            onClick={collapseOrExpand}
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
                  {/* Beside the button, not in the row's header line. The header already said
                      "Recomputing..." but sits above a scrolling segment list with this button below it,
                      so the only feedback was off-screen from the control that caused it. */}
                  {sample.pending && (
                    <span role="status" className="text-xs text-gray-500 dark:text-gray-400">
                      {t("people:voiceprintRecomputing")}
                    </span>
                  )}
                  {!sample.pending && sample.recomputeFailed && (
                    <span className="text-xs text-red-600 dark:text-red-400">
                      {t("people:voiceprintRecomputeFailed")}
                    </span>
                  )}
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

/// The verdict in words, never a bare number. "0.62" tells a user nothing they can act on; "sounds unlike
/// their others" tells them which recording to go and listen to.
///
/// `only` renders nothing: having no other recording to compare against is the state most of the directory
/// is in, and it is not a finding. `unlinked` renders nothing here either - the row already carries its own
/// badge, and saying it twice would read as two separate problems.
function VerdictChip({ verdict, impostorName }: { verdict: RowVerdict; impostorName?: string | null }) {
  const { t } = useTranslation("people");

  const tone: Partial<Record<RowVerdict, string>> = {
    core: "text-green-700 dark:text-green-400",
    variant: "text-blue-700 dark:text-blue-300",
    alone: "text-amber-800 dark:text-amber-300",
    // Red rather than amber: a different order of problem from "sounds unlike the rest".
    impostor: "text-red-700 dark:text-red-400",
  };
  const label: Partial<Record<RowVerdict, string>> = {
    core: t("vpVerdictCore"),
    variant: t("vpVerdictVariant"),
    alone: t("vpVerdictAlone"),
    // Named, because a verdict without the name is not actionable - it would leave the user to work out
    // who from scratch when the server already knows and the reassign control is on this same row. The
    // unnamed form is a fallback for a person deleted between the diagnosis and the render.
    impostor: impostorName
      ? t("vpVerdictImpostorNamed", { name: impostorName })
      : t("vpVerdictImpostor"),
  };

  if (!label[verdict]) return null;
  return <span className={`text-xs font-medium ${tone[verdict]}`}>{label[verdict]}</span>;
}
