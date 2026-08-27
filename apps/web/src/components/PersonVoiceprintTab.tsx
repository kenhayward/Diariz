import { useCallback, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { ClipRequest } from "../lib/clipPlayback";
import { useClipPlayer } from "../lib/useClipPlayer";
import PersonAttributionRow from "./PersonAttributionRow";
import { rowVerdict, sortKey, worthChecking } from "../lib/voiceprintVerdict";
import type { Person } from "../lib/types";

/// Where this person's voice has been heard, and which of it trains their voiceprint.
///
/// Lists **every speaker attributed to them**, not only the ones enrolled by hand. Automatic identification
/// links a speaker without creating a voice sample, so the older sample-only list showed a fraction of where
/// the voiceprint is actually applied and read as an arbitrary subset of the recordings.
///
/// **Every duration is stated twice where the two differ** - what was selected, and what the worker actually
/// pooled after its cap. Showing only the selection would quietly promise something untrue.
export default function PersonVoiceprintTab({ person }: { person: Person }) {
  const { t } = useTranslation(["people", "common"]);
  const qc = useQueryClient();

  const detail = useQuery({
    queryKey: ["person", person.id],
    queryFn: () => api.getPerson(person.id),
    // Nothing to fetch or show for someone who opted out - and asking would be the wrong signal to send.
    enabled: !person.voiceprintOptOut,
    // A queued recompute is server-derived (`pending`), so polling picks up its result and the state survives
    // a reload - unlike a flag held in this component.
    refetchInterval: (query) => (query.state.data?.samples.some((s) => s.pending) ? 3000 : false),
  });

  const attributions = useQuery({
    queryKey: ["personAttributions", person.id],
    queryFn: () => api.getPersonAttributions(person.id),
    enabled: !person.voiceprintOptOut,
  });

  // Joined here rather than shown on a tab of its own. Two lists of the same recordings - one with the
  // verdicts, one with the controls - is what made a flagged recording impossible to act on: you had to
  // remember its name, switch tabs and find it again.
  const diagnostics = useQuery({
    queryKey: ["personDiagnostics", person.id],
    queryFn: () => api.getPersonDiagnostics(person.id),
    enabled: !person.voiceprintOptOut,
  });

  const [onlyWorthChecking, setOnlyWorthChecking] = useState(false);
  const [onlyTraining, setOnlyTraining] = useState(false);

  // Stable, so the player's callbacks are not rebuilt on every render.
  const fetchClip = useCallback(
    (speakerId: string, fromMs: number, toMs: number) =>
      api.personClip(person.id, speakerId, fromMs, toMs),
    [person.id],
  );
  const { play, stop, playingSegmentId } = useClipPlayer(fetchClip);

  const onPlay = useCallback(
    (speakerId: string, queue: ClipRequest[]) => play(speakerId, queue),
    [play],
  );

  const refresh = useCallback(async () => {
    await Promise.all([
      qc.invalidateQueries({ queryKey: ["person", person.id] }),
      qc.invalidateQueries({ queryKey: ["personAttributions", person.id] }),
    ]);
  }, [qc, person.id]);

  if (person.voiceprintOptOut) {
    return (
      <div className="p-4">
        <p className="text-sm text-gray-600 dark:text-gray-300">{t("people:voiceprintOptedOut")}</p>
      </div>
    );
  }

  if (detail.isLoading || attributions.isLoading)
    return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;
  if (detail.isError || attributions.isError)
    return <p className="p-4 text-sm text-red-600 dark:text-red-400">{t("people:errLoadFailed")}</p>;

  const samples = detail.data?.samples ?? [];
  const rows = attributions.data ?? [];
  const trainingCount = rows.filter((r) => r.isTraining).length;

  // Keyed on the speaker, not the voice sample: `voiceSampleId` is null for an attribution that trains
  // nothing, and those rows still belong in the list.
  const bySpeaker = new Map((diagnostics.data?.samples ?? []).map((d) => [d.speakerId, d]));

  const decorated = rows.map((r) => {
    const diagnosis = bySpeaker.get(r.speakerId);
    return { row: r, diagnosis, verdict: rowVerdict(diagnosis, r.stillLinked) };
  });

  // A stable sort, so the server's ordering by recording name survives underneath. Everything needing no
  // attention shares one key - ranking Core against Variant would imply a difference to act on.
  const sorted = decorated.slice().sort((a, b) => sortKey(a.verdict) - sortKey(b.verdict));
  const checkable = decorated.filter((d) => worthChecking(d.verdict)).length;
  const aloneCount = decorated.filter((d) => d.verdict === "alone").length;
  const visible = sorted
    .filter((d) => !onlyWorthChecking || worthChecking(d.verdict))
    .filter((d) => !onlyTraining || d.row.isTraining);

  // Describes the list beneath it. The old header counted only the outliers while the list showed every
  // recording, so "5 resemble none of the others" sat above rows reading "Matches the others" - both true,
  // and together unreadable.
  const header =
    trainingCount < 2 && aloneCount === 0
      ? t("people:vpNothingToCompare")
      : t("people:vpTrainedOn", { trained: trainingCount, total: rows.length }) +
        (aloneCount > 0 ? ` ${t("people:vpSomeAlone", { count: aloneCount })}` : "");

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-gray-500 dark:text-gray-400">{header}</p>
        {/* Offered only when it could keep something. A control whose only possible effect is to empty
            the list is worse than no control. */}
        <div className="flex flex-wrap items-center gap-3">
          {checkable > 0 && (
            <label className="flex items-center gap-1.5 text-xs text-gray-700 dark:text-gray-200">
              <input
                type="checkbox"
                checked={onlyWorthChecking}
                onChange={(e) => setOnlyWorthChecking(e.target.checked)}
                aria-label={t("people:vpOnlyWorthChecking")}
              />
              {t("people:vpOnlyWorthChecking")}
            </label>
          )}
          {/* Offered only when it could hide something. Most people's rows all train, and a tick box that
              can never change the list is noise. */}
          {trainingCount > 0 && trainingCount < rows.length && (
            <label className="flex items-center gap-1.5 text-xs text-gray-700 dark:text-gray-200">
              <input
                type="checkbox"
                checked={onlyTraining}
                onChange={(e) => setOnlyTraining(e.target.checked)}
                aria-label={t("people:vpOnlyTraining")}
              />
              {t("people:vpOnlyTraining")}
            </label>
          )}
        </div>
      </div>

      {rows.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t("people:voiceprintNoSamples")}</p>
      ) : (
        <ul className="space-y-2">
          {visible.map(({ row: r, diagnosis, verdict }) => (
            <li key={r.speakerId} className="rounded border p-3 text-sm dark:border-gray-700">
              <PersonAttributionRow
                personId={person.id}
                personName={person.name}
                attribution={r}
                sample={samples.find((s) => s.id === r.voiceSampleId) ?? null}
                diagnosis={diagnosis}
                verdict={verdict}
                // The server's answer, never recomputed here, or the two drift the first time either side is
                // edited. Someone may still SEE what a voiceprint learned from without it.
                canManage={person.canManageBiometrics}
                onPlay={(speakerId, queue) => {
                  void onPlay(speakerId, queue);
                }}
                onStop={stop}
                onChanged={() => void refresh()}
                playingSegmentId={playingSegmentId}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
