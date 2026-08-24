import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { formatDuration } from "../lib/format";
import type { Person, VoiceSample } from "../lib/types";

/// What a person's voiceprint was actually trained on.
///
/// The data has been on `GET /api/people/{id}` since voiceprints shipped and was rendered nowhere, which
/// is why a drifting voiceprint had no diagnosis: you could see that recognition was wrong but not what it
/// had learned from. A sample from a misattributed speaker is the usual cause, and it can be dropped
/// individually.
///
/// **Every duration is stated twice where the two differ** - what was selected, and what the worker
/// actually pooled after its cap. Showing only the selection would quietly promise something untrue.
export default function PersonVoiceprintTab({ person }: { person: Person }) {
  const { t } = useTranslation(["people", "common"]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["person", person.id],
    queryFn: () => api.getPerson(person.id),
    // A queued recompute is server-derived (`pending`), so polling picks it up and it survives a reload.
    refetchInterval: (query) =>
      query.state.data?.samples.some((s) => s.pending) ? 3000 : false,
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
              <SampleHeading sample={s} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/// One contributing recording: which it was, which speaker slot, and how much of its audio is behind the
/// voiceprint.
function SampleHeading({ sample }: { sample: VoiceSample }) {
  const { t } = useTranslation("people");

  // Pending first: while a recompute is in flight the old figures describe a selection that is no longer
  // the one being used, so stating them would be worse than saying nothing.
  const duration = sample.pending
    ? t("voiceprintRecomputing")
    : sample.usedMs != null && sample.usedMs < sample.selectedMs
      ? t("voiceprintUsingOf", {
          used: formatDuration(sample.usedMs),
          selected: formatDuration(sample.selectedMs),
        })
      : formatDuration(sample.selectedMs);

  return (
    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
      <span className="font-medium text-gray-900 dark:text-gray-50">{sample.recordingName}</span>
      <span className="text-xs text-gray-500 dark:text-gray-400">{sample.speakerLabel}</span>
      <span className="text-xs text-gray-500 dark:text-gray-400">{duration}</span>
      {sample.stale && (
        <span className="rounded bg-amber-100 px-1.5 py-0.5 text-xs text-amber-900 dark:bg-amber-900/40 dark:text-amber-200">
          {t("voiceprintNeedsRecompute")}
        </span>
      )}
    </div>
  );
}
