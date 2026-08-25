import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDuration } from "../lib/format";
import type { SpeakerSuggestion } from "../lib/types";

/// Voices Diariz thinks it recognises but would not name on its own, gathered in one place.
///
/// This is how you find a backlog; the prompt on the transcript is how you judge one you happened to open.
/// Both call the same accept and reject, so a decision made here is the same decision made there.
///
/// Your own recordings only. A queue spanning the platform would tell whoever held it who appears in every
/// meeting in the instance, and the people who can actually answer were in the room.
export default function SpeakerSuggestions() {
  const { t } = useTranslation(["workspace", "common"]);
  const [error, setError] = useState<string | null>(null);
  /// Ids already decided, so a row leaves the list immediately rather than after a refetch.
  const [done, setDone] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState<string | null>(null);

  const { data = [], isLoading, isError } = useQuery({
    queryKey: ["speakerSuggestions"],
    queryFn: () => api.getSpeakerSuggestions(),
  });

  async function decide(s: SpeakerSuggestion, accept: boolean) {
    setBusy(s.speakerId);
    setError(null);
    try {
      if (accept) await api.acceptSpeakerSuggestion(s.speakerId);
      else await api.rejectSpeakerSuggestion(s.speakerId);
      // Only after the server agreed: dropping the row on a failure would look like the decision stuck.
      setDone((prev) => new Set(prev).add(s.speakerId));
    } catch (e) {
      setError(apiErrorMessage(e, t("workspace:suggestionFailed")));
    } finally {
      setBusy(null);
    }
  }

  const rows = data.filter((s) => !done.has(s.speakerId));

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-4 p-4">
      <header>
        <h1 className="text-lg font-semibold text-gray-900 dark:text-gray-50">
          {t("workspace:suggestionsTitle")}
        </h1>
        <p className="mt-1 text-sm text-gray-600 dark:text-gray-300">
          {t("workspace:suggestionsIntro")}
        </p>
      </header>

      {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}

      {isLoading ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
      ) : isError ? (
        <p className="text-sm text-red-600 dark:text-red-400">{t("workspace:suggestionsFailed")}</p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:suggestionsNone")}</p>
      ) : (
        <ul className="flex flex-col gap-2">
          {rows.map((s) => (
            <li
              key={s.speakerId}
              className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded border p-3 text-sm dark:border-gray-700"
            >
              <span className="font-medium text-gray-900 dark:text-gray-50">{s.personName}</span>
              <span className="text-xs text-gray-500 dark:text-gray-400">{s.speakerLabel}</span>
              {/* Straight to the transcript: the words and the audio are the evidence, and a borderline
                  call is exactly the one worth listening to before answering. */}
              <Link
                to={`/recordings/${s.recordingId}`}
                className="min-w-0 truncate text-xs underline text-gray-600 dark:text-gray-300"
              >
                {s.recordingName}
              </Link>
              <span className="text-xs text-gray-500 dark:text-gray-400">
                {formatDuration(s.speechMs)}
              </span>
              <div className="ml-auto flex shrink-0 items-center gap-2">
                <button
                  type="button"
                  onClick={() => void decide(s, true)}
                  disabled={busy === s.speakerId}
                  className="rounded border px-2 py-1 text-xs disabled:opacity-50 dark:border-gray-600 dark:text-gray-200"
                >
                  {t("workspace:suggestionYes")}
                </button>
                <button
                  type="button"
                  onClick={() => void decide(s, false)}
                  disabled={busy === s.speakerId}
                  className="rounded border px-2 py-1 text-xs disabled:opacity-50 dark:border-gray-600 dark:text-gray-200"
                >
                  {t("workspace:suggestionNo")}
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
