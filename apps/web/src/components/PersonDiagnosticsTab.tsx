import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Person, SampleDiagnosis } from "../lib/types";

/// Which of this person's enrolled samples resemble each other, and which resemble nothing.
///
/// A voiceprint built from several recordings can drift for two very different reasons, and the numbers alone
/// do not separate them: a sample sitting far from the others is either **the same voice in a condition
/// nothing else covers** - a phone, a car, a room microphone - or **a different person enrolled under this
/// name**. Only listening tells you which, so this says where to look rather than what to do.
///
/// Two distances per sample, because they answer different questions and disagree in the case that matters:
/// a sample can sit right beside one companion while that pair together sits well away from everything else.
export default function PersonDiagnosticsTab({ person }: { person: Person }) {
  const { t } = useTranslation(["people", "common"]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["personDiagnostics", person.id],
    queryFn: () => api.getPersonDiagnostics(person.id),
    enabled: !person.voiceprintOptOut,
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
  const training = samples.filter((s) => s.isTraining);

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto p-4">
      {/* Said explicitly rather than left as an empty list. "Everything is fine" and "this did not load"
          must not look the same. */}
      <p className="text-xs text-gray-500 dark:text-gray-400">
        {training.length < 2
          ? t("people:diagNothingToCompare")
          : (data?.aloneCount ?? 0) === 0
            ? t("people:diagAllConsistent")
            : t("people:diagSomeAlone", { count: data?.aloneCount ?? 0 })}
      </p>

      {samples.length > 0 && (
        <ul className="space-y-2">
          {samples.map((s) => (
            <li key={s.voiceSampleId} className="rounded border p-3 text-sm dark:border-gray-700">
              <DiagnosisRow sample={s} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function DiagnosisRow({ sample }: { sample: SampleDiagnosis }) {
  const { t } = useTranslation("people");

  // The verdict in words, never a bare number. "0.62" tells a user nothing they can act on; "resembles none
  // of the others" tells them to go and listen to that recording.
  const verdict: Record<string, { label: string; tone: string }> = {
    Only: { label: t("diagVerdictOnly"), tone: "text-gray-500 dark:text-gray-400" },
    Core: { label: t("diagVerdictCore"), tone: "text-green-700 dark:text-green-400" },
    Variant: { label: t("diagVerdictVariant"), tone: "text-blue-700 dark:text-blue-300" },
    Alone: { label: t("diagVerdictAlone"), tone: "text-amber-800 dark:text-amber-300" },
  };
  const v = verdict[sample.verdict] ?? verdict.Only;

  const pct = (d: number | null) => (d == null ? null : `${Math.round(d * 100)}`);

  return (
    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
      <span className="min-w-0 truncate font-medium text-gray-900 dark:text-gray-50">
        {sample.recordingName}
      </span>
      <span className="text-xs text-gray-500 dark:text-gray-400">{sample.speakerLabel}</span>
      <span className={`text-xs font-medium ${v.tone}`}>{v.label}</span>
      {!sample.isTraining && (
        <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[10px] text-gray-600 dark:bg-gray-800 dark:text-gray-300">
          {t("diagExcluded")}
        </span>
      )}
      {sample.nearestSiblingDistance != null && (
        <span className="text-xs text-gray-500 dark:text-gray-400">
          {t("diagNearest", { value: pct(sample.nearestSiblingDistance) })}
        </span>
      )}
      {sample.distanceToOthers != null && (
        <span className="text-xs text-gray-500 dark:text-gray-400">
          {t("diagToOthers", { value: pct(sample.distanceToOthers) })}
        </span>
      )}
    </div>
  );
}
