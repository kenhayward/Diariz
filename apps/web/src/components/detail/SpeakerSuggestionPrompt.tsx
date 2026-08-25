import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import type { SpeakerInfo } from "../../lib/types";

/// "This might be Ada Lovelace - is it?", asked where the evidence already is.
///
/// A borderline voice match is not applied on its own, but it is worth asking about. The transcript is the
/// best place to ask: the words and the audio are already on screen, so the question can be answered without
/// going anywhere. The same decision is available in bulk from the review queue, which is how you find a
/// backlog rather than how you judge one.
export default function SpeakerSuggestionPrompt({
  speakerId,
  info,
  onDecided,
}: {
  speakerId: string;
  info: SpeakerInfo;
  /// Called only after the server has accepted the decision - see the error path below.
  onDecided: () => void;
}) {
  const { t } = useTranslation("workspace");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!info.suggestedPersonId) return null;

  async function decide(accept: boolean) {
    setBusy(true);
    setError(null);
    try {
      if (accept) await api.acceptSpeakerSuggestion(speakerId);
      else await api.rejectSpeakerSuggestion(speakerId);
      // Only now: reporting a decision the server refused would clear the prompt while the suggestion is
      // still pending, and it would reappear on the next load with nothing to explain why.
      onDecided();
    } catch (e) {
      setError(apiErrorMessage(e, t("suggestionFailed")));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mt-1 flex flex-wrap items-center gap-2 rounded bg-blue-50 px-2 py-1 text-xs dark:bg-blue-900/30">
      <span className="min-w-0 text-gray-700 dark:text-gray-200">
        {t("suggestionAsk", { name: info.suggestedPersonName })}
      </span>
      <button
        type="button"
        onClick={() => void decide(true)}
        disabled={busy}
        className="rounded border px-1.5 py-0.5 disabled:opacity-50 dark:border-gray-600"
      >
        {t("suggestionYes")}
      </button>
      <button
        type="button"
        onClick={() => void decide(false)}
        disabled={busy}
        className="rounded border px-1.5 py-0.5 disabled:opacity-50 dark:border-gray-600"
      >
        {t("suggestionNo")}
      </button>
      {error && <span className="text-red-600 dark:text-red-400">{error}</span>}
    </div>
  );
}
